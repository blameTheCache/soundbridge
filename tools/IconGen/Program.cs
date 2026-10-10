using System.IO;
using System.Windows.Media;
using SoundBridge.UI;

namespace IconGen;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--package-images")
        {
            WritePackageImages(Path.GetFullPath(args[1]));
            return 0;
        }

        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: IconGen <output.ico> [preview-directory]");
            Console.Error.WriteLine("       IconGen --package-images <directory>");
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

    /// <summary>The logos the package manifest (packaging/AppxManifest.xml) refers to.</summary>
    private static void WritePackageImages(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, size) in new[] { ("Square150x150Logo.png", 150), ("Square44x44Logo.png", 44), ("StoreLogo.png", 50) })
        {
            File.WriteAllBytes(Path.Combine(directory, name), LogoRenderer.EncodePng(LogoRenderer.RenderAppIcon(size)));
        }

        Console.WriteLine($"Wrote package images to {directory}");
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
