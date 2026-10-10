using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace SoundBridge.Platform;

/// <summary>
/// Whether this process runs as the installed (packaged) app, and why Windows started it. The same executable
/// also runs unpackaged; a few Windows features, phone calls among them, are only granted to the packaged form.
/// </summary>
internal static class AppIdentity
{
    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    public static string? PackageFamilyName { get; } = ReadFamilyName();

    public static bool IsPackaged => PackageFamilyName is not null;

    /// <summary>The activation Windows reported, or null when unpackaged or when it could not be read.</summary>
    public static ActivationKind? Activation { get; } = ReadActivation();

    /// <summary>Windows starts the app for calls on its own; that is not the user asking to see the window.</summary>
    public static bool StartedForPhoneCall => Activation == ActivationKind.PhoneCallActivation;

    public static bool StartedAtSignIn => Activation == ActivationKind.StartupTask;

    private static string? ReadFamilyName()
    {
        var length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) == NoPackage || length == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length);
        return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    private static ActivationKind? ReadActivation()
    {
        if (PackageFamilyName is null)
        {
            return null;
        }

        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);
}
