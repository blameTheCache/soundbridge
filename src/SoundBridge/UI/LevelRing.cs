using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SoundBridge.Core.Audio;
using SoundBridge.Platform;

namespace SoundBridge.UI;

/// <summary>
/// The live indicator around the phone: a ring of radial bars that moves with the phone's audio.
/// <para>
/// Every bar is a measured level. The newest reading is at the head of the ring and older ones trail away
/// down both sides, so the ring is a short history of how loud the phone has been, wrapped into a circle.
/// It is not a spectrum analyser: Windows reports loudness, not frequency content, and nothing here
/// pretends otherwise.
/// </para>
/// <para>
/// Each bar is drawn once and afterwards only moved and faded, which the graphics card does; redrawing the
/// shapes every frame cost twenty times as much processor time. It animates only while it is on screen and
/// audio is playing, and idle it costs nothing.
/// </para>
/// </summary>
internal sealed class LevelRing : FrameworkElement
{
    public static readonly DependencyProperty MeterProperty = DependencyProperty.Register(
        nameof(Meter), typeof(PhoneLevelMeter), typeof(LevelRing), new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LevelRing), new PropertyMetadata(false, (d, _) => ((LevelRing)d).UpdateRunning()));

    /// <summary>Radius of the phone's circle this ring is drawn around, in device-independent pixels.</summary>
    public static readonly DependencyProperty InnerRadiusProperty = DependencyProperty.Register(
        nameof(InnerRadius), typeof(double), typeof(LevelRing), new PropertyMetadata(38d));

    private const int Bars = 72;
    private const int Half = Bars / 2;
    private const double Gap = 7;
    private const double MinLength = 2.5;
    private const double MaxLength = 21;
    private const double BarThickness = 2.6;
    private const double GlowThickness = 8;
    private const double DegreesPerSecond = 9;
    private const double MinimumSpan = 0.05;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(32);

    private readonly VisualCollection _visuals;
    private readonly ContainerVisual _root = new();
    private readonly ContainerVisual _ring = new();
    private readonly DrawingVisual _aura = new();
    private readonly DrawingVisual[] _bars = new DrawingVisual[Bars];
    private readonly MatrixTransform[] _barTransforms = new MatrixTransform[Bars];
    private readonly ScaleTransform _auraScale = new(1, 1);
    private readonly RotateTransform _rotation = new(0);
    private readonly float[] _history = new float[LevelTracker.HistoryLength];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Brush? _paletteSource;
    private TimeSpan _lastFrame;
    private double _intensity;
    private double _floor;
    private double _span = 1;
    private bool _running;

    public LevelRing()
    {
        _visuals = new VisualCollection(this) { _root };
        IsHitTestVisible = false;

        _aura.Transform = _auraScale;
        _ring.Transform = _rotation;
        for (var i = 0; i < Bars; i++)
        {
            _bars[i] = new DrawingVisual { Transform = _barTransforms[i] = new MatrixTransform() };
            _ring.Children.Add(_bars[i]);
        }

        _root.Children.Add(_aura);
        _root.Children.Add(_ring);
        _root.Opacity = 0;

        IsVisibleChanged += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Run(false);
    }

    public PhoneLevelMeter? Meter
    {
        get => (PhoneLevelMeter?)GetValue(MeterProperty);
        set => SetValue(MeterProperty, value);
    }

    /// <summary>True while audio is playing. The ring fades in and out around this; it never shows by itself.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double InnerRadius
    {
        get => (double)GetValue(InnerRadiusProperty);
        set => SetValue(InnerRadiusProperty, value);
    }

    // WPF asks for this while the element is still being constructed, before the collection exists.
    protected override int VisualChildrenCount => _visuals?.Count ?? 0;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _root.Offset = new Vector(sizeInfo.NewSize.Width / 2, sizeInfo.NewSize.Height / 2);
    }

    private void UpdateRunning() => Run(IsVisible && (IsActive || _intensity > 0.001));

    private void Run(bool run)
    {
        if (run == _running)
        {
            return;
        }

        _running = run;
        if (run)
        {
            _lastFrame = _clock.Elapsed;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastFrame;
        if (elapsed < FrameInterval)
        {
            return;
        }

        _lastFrame = now;

        // Fade towards shown or hidden over about a third of a second.
        var target = IsActive ? 1 : 0;
        var change = elapsed.TotalSeconds * 3.2;
        _intensity = target > _intensity ? Math.Min(target, _intensity + change) : Math.Max(target, _intensity - change);
        _root.Opacity = _intensity;

        if (_intensity <= 0.001)
        {
            if (!IsActive)
            {
                Run(false);
            }

            return;
        }

        EnsureShapes();

        var progress = 0f;
        var rawLevel = Meter?.Read(_history, out progress) ?? 0f;
        var start = InnerRadius + Gap;

        // Music sits in a narrow band of loudness, so on an absolute scale every bar would be about the same
        // length. Stretch the band actually in use across the bar's full travel. The readings are unchanged;
        // only the scale they are drawn on follows them, and it moves slowly so it does not pump.
        var lowest = 1f;
        var highest = 0f;
        foreach (var reading in _history)
        {
            if (reading > 0)
            {
                lowest = Math.Min(lowest, reading);
                highest = Math.Max(highest, reading);
            }
        }

        if (highest > 0)
        {
            var follow = Math.Min(1, elapsed.TotalSeconds * 2.5);
            _floor += (lowest - _floor) * follow;
            _span += (Math.Max(highest - lowest, MinimumSpan) - _span) * follow;
        }

        var level = Stretch(rawLevel);
        var breath = 0.82 + (0.18 * level);

        for (var i = 0; i < Bars; i++)
        {
            // Mirror about the ring's own axis: bar 0 is its head, and both sides run newest to oldest.
            var step = i <= Half ? i : Bars - i;
            var value = Stretch(Sample(step, progress));
            var eased = 1 - ((1 - value) * (1 - value));
            var length = MinLength + (eased * MaxLength * breath);

            // The bar was drawn pointing up at full length: shorten it, move it out to the ring, turn it into place.
            var matrix = Matrix.Identity;
            matrix.Scale(1, length / MaxLength);
            matrix.Translate(0, -start);
            matrix.Rotate(i * 360.0 / Bars);
            _barTransforms[i].Matrix = matrix;
        }

        // A soft pool of colour behind the bars that swells with the current level, and a slow turn of the whole ring.
        var auraRadius = start + 6 + (level * 30);
        _auraScale.ScaleX = _auraScale.ScaleY = auraRadius;
        _aura.Opacity = 0.35 + (0.65 * level);
        _rotation.Angle = now.TotalSeconds * DegreesPerSecond % 360;
    }

    /// <summary>Places a reading within the range currently in use. Silence stays at zero; the quietest sound still shows a short bar.</summary>
    private double Stretch(double reading) =>
        reading <= 0 ? 0 : 0.14 + (0.86 * Math.Clamp((reading - _floor) / _span, 0, 1));

    /// <summary>The level <paramref name="step"/> places back from the newest, sliding with the meter so the bars flow instead of jumping.</summary>
    private double Sample(int step, float progress)
    {
        var position = (_history.Length - 1) - ((step * (_history.Length - 2.0) / Half) + (1 - progress));
        if (position <= 0)
        {
            return _history[0];
        }

        var index = (int)position;
        if (index >= _history.Length - 1)
        {
            return _history[^1];
        }

        var fraction = position - index;
        return _history[index] + ((_history[index + 1] - _history[index]) * fraction);
    }

    /// <summary>Draws the bars and the aura in the theme's vivid colours: once, and again whenever the theme changes.</summary>
    private void EnsureShapes()
    {
        var source = TryFindResource("VividBrush") as Brush;
        if (ReferenceEquals(source, _paletteSource) && _paletteSource is not null)
        {
            return;
        }

        _paletteSource = source;
        var stops = source is GradientBrush { GradientStops.Count: >= 3 } gradient
            ? gradient.GradientStops.Select(stop => stop.Color).ToArray()
            : [Colors.MediumAquamarine, Colors.CornflowerBlue, Colors.MediumPurple];

        for (var i = 0; i < Bars; i++)
        {
            // Once round the ring passes through all three colours and returns to the first without a seam.
            var position = (double)i / Bars * 3;
            var index = (int)position % 3;
            var colour = Mix(stops[index], stops[(index + 1) % 3], position - Math.Floor(position));

            using var dc = _bars[i].RenderOpen();
            var glow = Frozen(new SolidColorBrush(Color.FromArgb(48, colour.R, colour.G, colour.B)));
            dc.DrawRoundedRectangle(glow, null, new Rect(-GlowThickness / 2, -MaxLength - 2.5, GlowThickness, MaxLength + 5), GlowThickness / 2, GlowThickness / 2);
            dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(colour)), null, new Rect(-BarThickness / 2, -MaxLength, BarThickness, MaxLength), BarThickness / 2, BarThickness / 2);
        }

        var middle = stops[1];
        var pool = new RadialGradientBrush(
            Color.FromArgb(120, middle.R, middle.G, middle.B),
            Color.FromArgb(0, middle.R, middle.G, middle.B));
        using (var dc = _aura.RenderOpen())
        {
            // Unit radius; the scale transform sets the real size each frame.
            dc.DrawEllipse(Frozen(pool), null, default, 1, 1);
        }
    }

    private static T Frozen<T>(T brush)
        where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * amount)),
        (byte)(a.G + ((b.G - a.G) * amount)),
        (byte)(a.B + ((b.B - a.B) * amount)));
}
