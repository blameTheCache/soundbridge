using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SoundBridge.Core.Diagnostics;
using SoundBridge.UI;

namespace SoundBridge.Platform;

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon, with a hidden window to receive its messages.
/// Re-adds itself when Explorer restarts and reports taskbar light/dark changes so the icon can be redrawn.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string Category = "Tray";

    private const int WmApp = 0x8000;
    private const int WmSettingChange = 0x001A;
    private const int WmContextMenu = 0x007B;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const int CallbackMessage = WmApp + 1;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NimSetVersion = 4;
    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;
    private const int NifShowTip = 0x80;
    private const int NiifUser = 0x04;
    private const int NiifLargeIcon = 0x20;
    private const int NotifyIconVersion4 = 4;
    private const int SmCxSmallIcon = 49;

    private readonly ILog _log;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private nint _icon;
    private string _tooltip = "SoundBridge";
    private bool _added;
    private bool _disposed;

    public TrayIcon(ILog log)
    {
        _log = log;
        var parameters = new HwndSourceParameters("SoundBridge.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW: keeps it out of Alt+Tab
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Left click, double click or keyboard activation.</summary>
    public event EventHandler? Activated;

    /// <summary>Right click or the context-menu key.</summary>
    public event EventHandler? MenuRequested;

    public event EventHandler? TaskbarThemeChanged;

    public nint WindowHandle => _window.Handle;

    public static int IconSize => Math.Max(16, GetSystemMetrics(SmCxSmallIcon));

    /// <summary>The taskbar follows the Windows mode, which can differ from the app theme.</summary>
    public static bool TaskbarIsLight
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
    }

    public void Show(BitmapSource image, string tooltip)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _icon;
        _icon = CreateIcon(image);
        _tooltip = Truncate(tooltip, 127);

        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        if (_added && Shell_NotifyIcon(NimModify, ref data))
        {
            Destroy(previous);
            return;
        }

        // First use, or Explorer restarted and forgot the icon.
        _added = Shell_NotifyIcon(NimAdd, ref data);
        if (_added)
        {
            data.uVersion = NotifyIconVersion4;
            Shell_NotifyIcon(NimSetVersion, ref data);
        }
        else
        {
            _log.Warn(Category, $"The tray icon could not be added (error {Marshal.GetLastWin32Error()}).");
        }

        Destroy(previous);
    }

    public void ShowNotification(string title, string text)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = NewData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NiifUser | NiifLargeIcon;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NimDelete, ref data);
        }

        Destroy(_icon);
        _icon = 0;
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NinSelect:
                case NinKeySelect:
                case WmLeftButtonDoubleClick:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case WmContextMenu:
                    MenuRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }

            handled = true;
        }
        else if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false;
            TaskbarThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message == WmSettingChange && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            TaskbarThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    private NotifyIconData NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
        hBalloonIcon = _icon,
    };

    private nint CreateIcon(BitmapSource image)
    {
        // A PNG frame is a valid icon resource since Windows Vista, which avoids hand-building mask bitmaps.
        var png = LogoRenderer.EncodePng(image);
        var icon = CreateIconFromResourceEx(png, png.Length, true, 0x00030000, image.PixelWidth, image.PixelHeight, 0);
        if (icon == 0)
        {
            _log.Warn(Category, $"The tray image could not be converted to an icon (error {Marshal.GetLastWin32Error()}).");
        }

        return icon;
    }

    private static void Destroy(nint icon)
    {
        if (icon != 0)
        {
            DestroyIcon(icon);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(byte[] bits, int size, [MarshalAs(UnmanagedType.Bool)] bool isIcon, int version, int width, int height, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
