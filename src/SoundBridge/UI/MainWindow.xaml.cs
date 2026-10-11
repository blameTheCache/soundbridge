using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SoundBridge.UI;

internal partial class MainWindow : Window
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly MainViewModel _viewModel;
    private readonly ThemeManager _themes;
    private string? _appliedTone;

    public MainWindow(MainViewModel viewModel, ThemeManager themes)
    {
        _viewModel = viewModel;
        _themes = themes;

        InitializeComponent();
        DataContext = viewModel;

        // Fits small laptop screens; the lists scroll when the window is shorter.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 16);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        themes.Changed += OnThemeChanged;

        // The phone's audio level is sampled only while this window can show it.
        IsVisibleChanged += (_, _) => ReportVisibility();
        StateChanged += (_, _) => ReportVisibility();
        ApplyTone(viewModel.Tone);
    }

    /// <summary>Set by the app when it is really exiting; otherwise closing is routed through the view model.</summary>
    public bool AllowClose { get; set; }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // Raises the window even when Windows would only flash the taskbar button.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowFrame();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            _viewModel.OnCloseRequested();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _themes.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsClosePromptOpen)
            {
                _viewModel.IsClosePromptOpen = false;
                e.Handled = true;
            }
            else if (_viewModel.IsSettingsOpen)
            {
                _viewModel.IsSettingsOpen = false;
                e.Handled = true;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Tone):
                ApplyTone(_viewModel.Tone);
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                ShowSettings(_viewModel.IsSettingsOpen);
                break;
            case nameof(MainViewModel.IsClosePromptOpen):
                ShowClosePrompt(_viewModel.IsClosePromptOpen);
                break;
            case nameof(MainViewModel.IsRefreshing) when _viewModel.IsRefreshing:
                RefreshButton.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, Milliseconds(600)) { EasingFunction = EaseOut });
                break;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyWindowFrame();

    private void ReportVisibility() => _viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => _viewModel.IsClosePromptOpen = false;

    /// <summary>The spinner runs only while a request is in flight; the halo marks every state except idle.</summary>
    private void ApplyTone(string tone)
    {
        if (tone == _appliedTone)
        {
            return;
        }

        _appliedTone = tone;
        var busy = tone == nameof(StatusTone.Busy);
        var idle = tone == nameof(StatusTone.Idle);

        Spinner.BeginAnimation(OpacityProperty, new DoubleAnimation(busy ? 1 : 0, Milliseconds(180)));
        Spinner.RenderTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            busy ? new DoubleAnimation(0, 360, Milliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever } : null);

        if (busy)
        {
            var breathe = new SineEase { EasingMode = EasingMode.EaseInOut };
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, Milliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = breathe });
            AnimateScale(Halo, new DoubleAnimation(0.9, 1, Milliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = breathe });
        }
        else
        {
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(idle ? 0 : 1, Milliseconds(260)));
            AnimateScale(Halo, new DoubleAnimation(idle ? 0.9 : 1, Milliseconds(320)) { EasingFunction = EaseOut });
        }
    }

    private void ShowSettings(bool show)
    {
        BackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TitleLogo.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = show ? "Settings" : "SoundBridge";

        var direction = show ? 1 : -1;
        SlideOut(show ? MainPage : SettingsPage, -24 * direction, () => _viewModel.IsSettingsOpen == show);
        SlideIn(show ? SettingsPage : MainPage, 24 * direction);

        if (show)
        {
            SettingsPage.ScrollToTop();
        }
    }

    private void ShowClosePrompt(bool show)
    {
        if (show)
        {
            ClosePrompt.Visibility = Visibility.Visible;
            ClosePrompt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Milliseconds(160)));
            AnimateScale(ClosePromptCard, new DoubleAnimation(0.96, 1, Milliseconds(220)) { EasingFunction = EaseOut });
            KeepInTrayButton.Focus();
            return;
        }

        var fade = new DoubleAnimation(0, Milliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (!_viewModel.IsClosePromptOpen)
            {
                ClosePrompt.Visibility = Visibility.Collapsed;
            }
        };
        ClosePrompt.BeginAnimation(OpacityProperty, fade);
    }

    private static void SlideIn(FrameworkElement page, double from)
    {
        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Milliseconds(220)));
        page.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, Milliseconds(260)) { EasingFunction = EaseOut });
    }

    private static void SlideOut(FrameworkElement page, double to, Func<bool> stillHidden)
    {
        var fade = new DoubleAnimation(0, Milliseconds(140));
        fade.Completed += (_, _) =>
        {
            // A quick back-and-forth may have made this page the visible one again.
            if (stillHidden())
            {
                page.Visibility = Visibility.Collapsed;
            }
        };
        page.BeginAnimation(OpacityProperty, fade);
        page.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, Milliseconds(200)) { EasingFunction = EaseOut });
    }

    private static void AnimateScale(UIElement element, DoubleAnimation animation)
    {
        element.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        element.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static Duration Milliseconds(double value) => new(TimeSpan.FromMilliseconds(value));

    /// <summary>Matches the DWM frame to the theme: dark/light non-client area, rounded corners, border colour.</summary>
    private void ApplyWindowFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        const int UseImmersiveDarkMode = 20;
        const int WindowCornerPreference = 33;
        const int BorderColor = 34;

        var palette = _themes.Current;
        var dark = palette.IsDark ? 1 : 0;
        var round = 2;
        var line = palette.Color(palette.Line);
        var colorRef = line.R | (line.G << 8) | (line.B << 16);

        // Corner and border attributes exist on Windows 11 only; on Windows 10 the calls fail harmlessly.
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref round, sizeof(int));
        _ = DwmSetWindowAttribute(handle, BorderColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
