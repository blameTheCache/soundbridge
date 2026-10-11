using System.Windows;
using System.Windows.Media;

namespace SoundBridge.UI;

internal sealed record ThemePalette(
    string Id,
    string Name,
    bool IsDark,
    string Background,
    string BackgroundDeep,
    string Surface,
    string SurfaceAlt,
    string Line,
    string Text,
    string TextMuted,
    string TextFaint,
    string Accent,
    string OnAccent,
    string Success,
    string Warning,
    string Danger,
    string Vivid1,
    string Vivid2,
    string Vivid3)
{
    public Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>
/// Three dark and three light palettes. Applying one replaces the brush resources the whole UI reads through
/// DynamicResource, so the change is immediate and needs no restart.
/// <para>
/// The interface itself stays restrained. The three "vivid" colours of each palette are reserved for the one
/// thing that should draw the eye: the live indicator shown while the phone is actually playing.
/// </para>
/// </summary>
internal sealed class ThemeManager(Application application)
{
    public static readonly IReadOnlyList<ThemePalette> Palettes =
    [
        new("graphite", "Graphite", true, "#131517", "#0E1011", "#1B1E21", "#24282C", "#2E3338", "#ECEEF0", "#9CA4AD", "#6C747D", "#62BFAD", "#08201B", "#5CC98B", "#E5B454", "#F0736A", "#3FF0C2", "#4FA8FF", "#B48CFF"),
        new("midnight", "Midnight", true, "#0E1118", "#0A0C12", "#161B26", "#1E2533", "#293142", "#E8ECF5", "#94A1B9", "#65728C", "#8A9FFB", "#0B1130", "#5CC98B", "#E5B454", "#F0736A", "#6FA0FF", "#B57CFF", "#FF74C8"),
        new("ember", "Ember", true, "#151211", "#100D0C", "#1E1A18", "#28221F", "#352E2A", "#F1ECE8", "#ACA199", "#796E67", "#E2A65C", "#2A1A04", "#6FCB8F", "#E9BC5E", "#F0736A", "#FFC24A", "#FF7A45", "#FF4F93"),
        new("paper", "Paper", false, "#F5F6F7", "#ECEEF0", "#FFFFFF", "#F0F2F4", "#DDE1E6", "#16191D", "#5B6470", "#8A939E", "#2F6FDE", "#FFFFFF", "#1E8E4E", "#A9730B", "#C93B30", "#1F7BF2", "#7B4DFF", "#E23F9A"),
        new("mist", "Mist", false, "#EEF3F4", "#E4EBEC", "#FAFCFC", "#E6EEEF", "#CFDBDD", "#132024", "#4F6368", "#7F9196", "#1C8474", "#FFFFFF", "#1E8E4E", "#A9730B", "#C93B30", "#0FAE96", "#2585E6", "#6A57E8"),
        new("sand", "Sand", false, "#F5F0E8", "#ECE5DA", "#FDFAF5", "#EEE7DB", "#DBD0BF", "#241D14", "#6B5F50", "#978A79", "#B25C34", "#FFFFFF", "#2F8551", "#A9730B", "#C2412F", "#E08A1E", "#E0503F", "#A247D6"),
    ];

    public event EventHandler? Changed;

    public ThemePalette Current { get; private set; } = Palettes[0];

    public static ThemePalette Find(string? id) =>
        Palettes.FirstOrDefault(palette => string.Equals(palette.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Palettes[0];

    public void Apply(string? id)
    {
        var palette = Find(id);
        Current = palette;

        var text = palette.Color(palette.Text);
        var accent = palette.Color(palette.Accent);
        var resources = application.Resources;

        resources["WindowBackgroundBrush"] = Frozen(new LinearGradientBrush(palette.Color(palette.Background), palette.Color(palette.BackgroundDeep), 90));
        resources["SurfaceBrush"] = Solid(palette.Color(palette.Surface));
        resources["SurfaceAltBrush"] = Solid(palette.Color(palette.SurfaceAlt));
        resources["LineBrush"] = Solid(palette.Color(palette.Line));
        resources["TextBrush"] = Solid(text);
        resources["TextMutedBrush"] = Solid(palette.Color(palette.TextMuted));
        resources["TextFaintBrush"] = Solid(palette.Color(palette.TextFaint));
        resources["AccentBrush"] = Solid(accent);
        resources["OnAccentBrush"] = Solid(palette.Color(palette.OnAccent));
        resources["AccentSoftBrush"] = Solid(WithAlpha(accent, palette.IsDark ? 0.16 : 0.12));
        resources["SuccessBrush"] = Solid(palette.Color(palette.Success));
        resources["SuccessSoftBrush"] = Solid(WithAlpha(palette.Color(palette.Success), 0.16));
        resources["WarningBrush"] = Solid(palette.Color(palette.Warning));
        resources["WarningSoftBrush"] = Solid(WithAlpha(palette.Color(palette.Warning), 0.14));
        resources["DangerBrush"] = Solid(palette.Color(palette.Danger));
        resources["DangerSoftBrush"] = Solid(WithAlpha(palette.Color(palette.Danger), 0.14));
        resources["HoverBrush"] = Solid(WithAlpha(text, 0.07));
        resources["PressedBrush"] = Solid(WithAlpha(text, 0.12));
        resources["ScrimBrush"] = Solid(WithAlpha(Colors.Black, palette.IsDark ? 0.6 : 0.35));

        var vivid = new GradientStopCollection
        {
            new GradientStop(palette.Color(palette.Vivid1), 0),
            new GradientStop(palette.Color(palette.Vivid2), 0.5),
            new GradientStop(palette.Color(palette.Vivid3), 1),
        };
        resources["VividBrush"] = Frozen(new LinearGradientBrush(vivid, new Point(0, 0), new Point(1, 1)));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);

    private static SolidColorBrush Solid(Color color) => Frozen(new SolidColorBrush(color));

    private static T Frozen<T>(T brush)
        where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
