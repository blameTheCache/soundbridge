using SoundBridge.Core.Settings;

namespace SoundBridge.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soundbridge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLog _log = new();

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var settings = new SettingsStore(FilePath, _log).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(settings.AutoReconnect);
        Assert.True(settings.CloseToTray);
        Assert.False(settings.CloseChoiceMade);
        Assert.Null(settings.PreferredDeviceId);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var store = new SettingsStore(FilePath, _log);
        var saved = new AppSettings
        {
            PreferredDeviceId = @"\\?\BTHENUM#abc",
            PreferredDeviceName = "Pixel",
            AutoReconnect = false,
            MaxReconnectAttempts = 10,
            ConnectOnLaunch = false,
            CloseToTray = false,
            CloseChoiceMade = true,
            TrayHintShown = true,
            StartHiddenAtSignIn = false,
            Theme = "sand",
        };

        Assert.True(store.Save(saved));

        Assert.Equal(saved, new SettingsStore(FilePath, _log).Load());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Unreadable_file_falls_back_to_defaults_and_is_set_aside()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var settings = new SettingsStore(FilePath, _log).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(File.Exists(FilePath + ".unreadable"));
        Assert.False(File.Exists(FilePath));
        Assert.True(_log.Contains("could not be read"));
    }

    [Fact]
    public void Out_of_range_values_are_normalised_on_load()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "maxReconnectAttempts": 500, "theme": "  ", "preferredDeviceId": "", "preferredDeviceName": "ghost" }""");

        var settings = new SettingsStore(FilePath, _log).Load();

        Assert.Equal(10, settings.MaxReconnectAttempts);
        Assert.Equal(AppSettings.DefaultTheme, settings.Theme);
        Assert.Null(settings.PreferredDeviceId);
        Assert.Null(settings.PreferredDeviceName);
    }

    [Fact]
    public void Unknown_properties_and_a_null_document_are_tolerated()
    {
        Directory.CreateDirectory(_directory);
        var store = new SettingsStore(FilePath, _log);

        File.WriteAllText(FilePath, """{ "futureOption": true, "autoReconnect": false }""");
        Assert.False(store.Load().AutoReconnect);

        File.WriteAllText(FilePath, "null");
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Settings_map_to_the_reconnect_policy()
    {
        var policy = new AppSettings { AutoReconnect = false, MaxReconnectAttempts = 3 }.ToReconnectPolicy();

        Assert.False(policy.Enabled);
        Assert.Equal(3, policy.MaxRetries);
    }
}
