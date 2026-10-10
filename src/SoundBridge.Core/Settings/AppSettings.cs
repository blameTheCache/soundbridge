using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Settings;

/// <summary>User preferences persisted between sessions. "Launch at sign-in" is not here: the registry is its source of truth.</summary>
public sealed record AppSettings
{
    public const int CurrentSchema = 1;
    public const string DefaultTheme = "graphite";

    public int Schema { get; init; } = CurrentSchema;

    public string? PreferredDeviceId { get; init; }

    public string? PreferredDeviceName { get; init; }

    public bool AutoReconnect { get; init; } = true;

    public int MaxReconnectAttempts { get; init; } = ReconnectPolicy.Default.MaxRetries;

    /// <summary>After the retries run out, go on checking for the phone at a slow pace instead of giving up.</summary>
    public bool KeepWaitingForPhone { get; init; } = true;

    /// <summary>Connect to the preferred phone when a new app session starts.</summary>
    public bool ConnectOnLaunch { get; init; } = true;

    public bool CloseToTray { get; init; } = true;

    /// <summary>False until the user has answered the "close to tray or exit?" question once.</summary>
    public bool CloseChoiceMade { get; init; }

    public bool TrayHintShown { get; init; }

    public bool StartHiddenAtSignIn { get; init; } = true;

    /// <summary>Take the Bluetooth hands-free role for the preferred phone, so calls can be heard on this PC.</summary>
    public bool RouteCalls { get; init; }

    /// <summary>Show a small Answer / Decline prompt on this PC when the phone rings.</summary>
    public bool ShowCallPrompt { get; init; } = true;

    public string Theme { get; init; } = DefaultTheme;

    public AppSettings Normalized() => this with
    {
        Schema = CurrentSchema,
        PreferredDeviceId = string.IsNullOrWhiteSpace(PreferredDeviceId) ? null : PreferredDeviceId,
        PreferredDeviceName = string.IsNullOrWhiteSpace(PreferredDeviceId) ? null : PreferredDeviceName,
        MaxReconnectAttempts = ReconnectPolicy.ClampRetries(MaxReconnectAttempts),
        Theme = string.IsNullOrWhiteSpace(Theme) ? DefaultTheme : Theme,
    };

    public ReconnectPolicy ToReconnectPolicy() => new()
    {
        Enabled = AutoReconnect,
        MaxRetries = ReconnectPolicy.ClampRetries(MaxReconnectAttempts),
        KeepWaiting = KeepWaitingForPhone,
    };
}
