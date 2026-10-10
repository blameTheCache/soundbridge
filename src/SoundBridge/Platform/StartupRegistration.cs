using System.IO;
using Microsoft.Win32;
using SoundBridge.Core.Diagnostics;
using Windows.ApplicationModel;

namespace SoundBridge.Platform;

/// <summary>
/// "Launch at sign-in". Neither route needs elevation, and in both Windows stays the source of truth so the
/// toggle always reflects what will actually happen.
/// <list type="bullet">
/// <item>Unpackaged: a value under the per-user Run key.</item>
/// <item>Installed (packaged): the startup task declared in the package manifest, which Windows also lists
/// under Startup apps. If the user switched it off there, only they can switch it back on.</item>
/// </list>
/// </summary>
internal sealed class StartupRegistration(ILog log)
{
    public const string AutostartArgument = "--autostart";

    /// <summary>Must match the TaskId in packaging/AppxManifest.xml.</summary>
    private const string StartupTaskId = "SoundBridgeStartup";

    private const string Category = "Startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundBridge";

    private static string Command => $"\"{Environment.ProcessPath}\" {AutostartArgument}";

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (AppIdentity.IsPackaged)
                {
                    return StartupTaskState() is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                }

                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                log.Warn(Category, $"Could not read the sign-in setting: {ex.Message}");
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (AppIdentity.IsPackaged)
            {
                return SetStartupTask(enabled);
            }

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
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log.Error(Category, $"Could not change the sign-in setting: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// When the installed app finds a sign-in entry left by the unpackaged exe, it takes the setting over.
    /// Otherwise Windows would keep starting the unpackaged copy, which cannot route calls.
    /// </summary>
    public void AdoptEntryFromUnpackagedCopy()
    {
        if (!AppIdentity.IsPackaged)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is null)
            {
                return;
            }

            var adopted = SetStartupTask(enabled: true);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            log.Info(Category, $"Took over launch at sign-in from the unpackaged copy (startup task enabled: {adopted}).");
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not take over the sign-in entry: {ex.Message}");
        }
    }

    private bool SetStartupTask(bool enabled)
    {
        var state = Task.Run(async () =>
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return task.State;
            }

            return await task.RequestEnableAsync();
        }).GetAwaiter().GetResult();

        var isEnabled = state is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        log.Info(Category, $"Launch at sign-in {(enabled ? "requested" : "disabled")}; Windows reports {state}.");
        return isEnabled == enabled;
    }

    private static StartupTaskState StartupTaskState() =>
        Task.Run(async () => (await StartupTask.GetAsync(StartupTaskId)).State).GetAwaiter().GetResult();
}
