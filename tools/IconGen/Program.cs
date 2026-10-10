using System.IO;
using System.Windows.Media;
using SoundBridge.UI;

namespace IconGen;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: IconGen <output.ico> [preview-directory]");
            return 1;
        }

        var path = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, LogoRenderer.BuildAppIco(16, 20, 24, 32, 40, 48, 64, 256));
        Console.WriteLine($"Wrote {path}");

        if (args.Length == 2)
        {
            WritePreviews(Path.GetFullPath(args[1]));
        }

        return 0;
    }

    /// <summary>PNG renders of the app and tray marks, for checking them by eye at real sizes.</summary>
    private static void WritePreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        var connected = Color.FromRgb(0x4C, 0xC3, 0x8A);

        foreach (var size in new[] { 16, 24, 32, 256 })
        {
            File.WriteAllBytes(Path.Combine(directory, $"app-{size}.png"), LogoRenderer.EncodePng(LogoRenderer.RenderAppIcon(size)));
            File.WriteAllBytes(Path.Combine(directory, $"tray-idle-{size}.png"), LogoRenderer.EncodePng(LogoRenderer.RenderTrayIcon(size, Colors.White, Colors.White)));
            File.WriteAllBytes(Path.Combine(directory, $"tray-connected-{size}.png"), LogoRenderer.EncodePng(LogoRenderer.RenderTrayIcon(size, Colors.White, connected)));
        }

        Console.WriteLine($"Wrote previews to {directory}");
    }
}
