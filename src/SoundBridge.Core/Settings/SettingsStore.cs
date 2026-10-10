using System.Text.Json;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or unreadable file yields defaults, never an exception.</summary>
public sealed class SettingsStore
{
    private const string Category = "Settings";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly ILog _log;

    public SettingsStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log ?? NullLog.Instance;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                return (settings ?? new AppSettings()).Normalized();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Error(Category, $"Settings could not be read ({ex.Message}); using defaults.");
                SetAsideUnreadableFile();
                return new AppSettings();
            }
        }
    }

    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write beside the target and swap, so a crash mid-write cannot leave a truncated file.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), Options));
                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(Category, $"Settings could not be saved: {ex.Message}");
                return false;
            }
        }
    }

    private void SetAsideUnreadableFile()
    {
        try
        {
            File.Move(_path, _path + ".unreadable", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Category, $"The unreadable settings file could not be set aside: {ex.Message}");
        }
    }
}
