using System.IO;
using Microsoft.Win32;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Platform;

/// <summary>
/// "Launch at sign-in" through the per-user Run key. No elevation needed, and the registry stays the single
/// source of truth so the toggle always reflects what Windows will actually do.
/// </summary>
internal sealed class StartupRegistration(ILog log)
{
    public const string AutostartArgument = "--autostart";

    private const string Category = "Startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundBridge";

    private static string Command => $"\"{Environment.ProcessPath}\" {AutostartArgument}";

    /// <summary>True only when the entry exists and points at this executable.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                log.Warn(Category, $"Could not read the sign-in entry: {ex.Message}");
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            log.Info(Category, $"Launch at sign-in {(enabled ? "enabled" : "disabled")}.");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Error(Category, $"Could not change the sign-in entry: {ex.Message}");
            return false;
        }
    }
}
