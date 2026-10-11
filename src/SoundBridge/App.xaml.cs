using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using SoundBridge.Bluetooth;
using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using SoundBridge.Core.Settings;
using SoundBridge.Platform;
using SoundBridge.UI;

namespace SoundBridge;

/// <summary>Composition root: builds the modules, wires them together and owns the app's lifetime.</summary>
public partial class App : Application, IAppShell
{
    private const string Category = "App";

    private SingleInstance? _instance;
    private FileLog? _log;
    private ConnectionController? _controller;
    private CallsController? _calls;
    private AudioSourceWatcher? _watcher;
    private BluetoothRadioMonitor? _radio;
    private PhonePresenceMonitor? _presence;
    private CallPromptWindow? _callPrompt;
    private bool _callPromptDismissed;
    private PowerMonitor? _power;
    private ThemeManager? _themes;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private ContextMenu? _trayMenu;
    private (string Tone, string Tooltip, bool Light) _trayState;
    private bool _exiting;

    void IAppShell.ShowWindow() => ShowWindow("menu");

    void IAppShell.HideWindowToTray() => _window?.Hide();

    void IAppShell.ShowTrayHint() => _tray?.ShowNotification(
        "SoundBridge is still running",
        "Your phone stays connected. To quit, right-click the tray icon and choose Exit.");

    void IAppShell.Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log?.Info(Category, "Exit requested.");
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SOUNDBRIDGE_DATA_DIR relocates settings and logs, which keeps development runs away from real
        // settings. Such a run is also its own single-instance scope, so it can start beside the installed app.
        var customData = Environment.GetEnvironmentVariable("SOUNDBRIDGE_DATA_DIR") is { Length: > 0 } custom ? Path.GetFullPath(custom) : null;
        var dataDirectory = customData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBridge");
        // A stable hash: string.GetHashCode differs from one process to the next.
        var scope = customData is null
            ? string.Empty
            : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(customData.ToUpperInvariant())), 0, 6);

        // Windows starts the installed app by itself when a call comes in. If SoundBridge is already
        // running, that extra launch must end here without raising the window.
        _instance = SingleInstance.TryAcquire(showExisting: !AppIdentity.StartedForPhoneCall, scope);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        var log = _log = new FileLog(Path.Combine(dataDirectory, "logs"));
        var autostart = AppIdentity.StartedAtSignIn || e.Args.Contains(StartupRegistration.AutostartArgument, StringComparer.OrdinalIgnoreCase);
        log.Info(Category, $"SoundBridge {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion.VersionString} (installed app: {AppIdentity.IsPackaged}, launched at sign-in: {autostart}, started for a call: {AppIdentity.StartedForPhoneCall}).");
        if (_instance.Unguarded)
        {
            log.Warn(Category, "Another program holds the names SoundBridge uses to keep to one copy, and no other SoundBridge is running. Starting anyway; a second copy would not be stopped.");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // A UI fault should not cut the audio link; record it and carry on.
            log.Error(Category, $"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => log.Error(Category, $"Fatal exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error(Category, $"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        if (!BluetoothAudioLinkFactory.IsSupported)
        {
            log.Error(Category, "AudioPlaybackConnection is not available on this version of Windows.");
            MessageBox.Show(
                "SoundBridge needs Windows 10 version 2004 or later, which added the Bluetooth audio receiver it relies on.",
                "SoundBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var store = new SettingsStore(Path.Combine(dataDirectory, "settings.json"), log);
        var settings = store.Load();

        var themes = _themes = new ThemeManager(this);
        themes.Apply(settings.Theme);

        IAudioLinkFactory links = new BluetoothAudioLinkFactory(log);
        var title = "SoundBridge";
#if DEBUG
        if (Environment.GetEnvironmentVariable("SOUNDBRIDGE_SIMULATE") is { Length: > 0 } script)
        {
            links = new SimulatedAudioLinkFactory(script, log);
            title = "SoundBridge (simulated link)";
        }
#endif

        var controller = _controller = new ConnectionController(links, settings.ToReconnectPolicy(), log: log);
        var watcher = _watcher = new AudioSourceWatcher(log);
        var radio = _radio = new BluetoothRadioMonitor(log);
        var presence = _presence = new PhonePresenceMonitor(log);
        ICallTransportFactory callTransports = new PhoneCallTransportFactory(log);
#if DEBUG
        if (Environment.GetEnvironmentVariable("SOUNDBRIDGE_SIMULATE_CALLS") is { Length: > 0 })
        {
            callTransports = new SimulatedCallTransportFactory(log);
            title = "SoundBridge (simulated calls)";
        }
#endif

        var calls = _calls = new CallsController(callTransports, log);
        var startup = new StartupRegistration(log);
        startup.AdoptEntryFromUnpackagedCopy();

        var viewModel = _viewModel = new MainViewModel(controller, calls, watcher, radio, presence, store, settings, startup, themes, log, Dispatcher, this);
        MainWindow = _window = new MainWindow(viewModel, themes) { Title = title };

        // Whether the prompt is needed depends on whether the main window, which has the same buttons, is in front.
        _window.Activated += (_, _) => UpdateCallPrompt();

        // The phone's volume can also be changed in the Windows Volume mixer; catch up when the window comes forward.
        _window.Activated += (_, _) => viewModel.RefreshPhoneVolume();
        _window.Deactivated += (_, _) => UpdateCallPrompt();

        var tray = _tray = new TrayIcon(log);
        tray.Activated += (_, _) => ShowWindow("tray icon");
        tray.MenuRequested += (_, _) => OpenTrayMenu();
        tray.TaskbarThemeChanged += (_, _) => UpdateTray(force: true);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.TrayTooltip) or nameof(MainViewModel.Tone))
            {
                UpdateTray(force: false);
            }
            else if (args.PropertyName is nameof(MainViewModel.IsCallRinging) or nameof(MainViewModel.ShowCallPrompt))
            {
                UpdateCallPrompt();
            }
        };
        UpdateTray(force: true);

        var power = _power = new PowerMonitor(log);
        power.Suspending += (_, _) => controller.OnSuspending();
        power.Resumed += (_, _) => controller.OnResumed();

        _instance.OnActivationRequested(() => Dispatcher.BeginInvoke(() => ShowWindow("second launch")));

        // The window is shown on every launch the user starts. It stays hidden when Windows started the app:
        // at sign-in (if the user chose to start in the tray) or to announce a phone call.
        if (!(autostart && settings.StartHiddenAtSignIn) && !AppIdentity.StartedForPhoneCall)
        {
            _window.Show();
        }

        _ = InitializeAsync(viewModel, log);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Order matters: stop listening first, then give back the hands-free role and release the music
        // link, then remove the tray icon.
        if (_callPrompt is not null)
        {
            _callPrompt.AllowClose = true;
            _callPrompt.Close();
        }

        _viewModel?.Dispose();
        _power?.Dispose();
        _presence?.Dispose();
        _calls?.Dispose();
        _controller?.Dispose();
        _watcher?.Dispose();
        _radio?.Dispose();
        _tray?.Dispose();
        _log?.Info(Category, "Stopped.");
        _log?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static async Task InitializeAsync(MainViewModel viewModel, ILog log)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            log.Error(Category, $"Start-up did not complete: {ex}");
        }
    }

    private void ShowWindow(string reason)
    {
        _log?.Info(Category, $"Showing the window ({reason}).");
        _window?.ShowAndActivate();
    }

    /// <summary>
    /// Shows the Answer / Decline prompt while the phone rings, unless the main window is already in front
    /// (it carries the same buttons) or the user dismissed the prompt for this call.
    /// </summary>
    private void UpdateCallPrompt()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!_viewModel.IsCallRinging)
        {
            _callPromptDismissed = false;
            _callPrompt?.Hide();
            return;
        }

        var mainWindowInFront = _window is { IsVisible: true, IsActive: true };
        if (!_viewModel.ShowCallPrompt || _callPromptDismissed || mainWindowInFront)
        {
            _callPrompt?.Hide();
            return;
        }

        if (_callPrompt is null)
        {
            _callPrompt = new CallPromptWindow(_viewModel);
            _callPrompt.Dismissed += (_, _) =>
            {
                _callPromptDismissed = true;
                _callPrompt.Hide();
            };
        }

        if (!_callPrompt.IsVisible)
        {
            _log?.Info(Category, "Showing the incoming-call prompt.");
            _callPrompt.Show();
        }
    }

    private void UpdateTray(bool force)
    {
        if (_tray is null || _viewModel is null || _themes is null)
        {
            return;
        }

        var state = (_viewModel.Tone, _viewModel.TrayTooltip, TrayIcon.TaskbarIsLight);
        if (!force && state == _trayState)
        {
            return;
        }

        _trayState = state;
        var foreground = state.TaskbarIsLight ? Color.FromRgb(0x1B, 0x1F, 0x24) : Colors.White;
        var palette = _themes.Current;
        var status = state.Tone switch
        {
            nameof(StatusTone.On) => Color.FromRgb(0x4C, 0xC3, 0x8A),
            nameof(StatusTone.Busy) or nameof(StatusTone.Waiting) => Color.FromRgb(0xE5, 0xB4, 0x54),
            nameof(StatusTone.Error) => palette.Color(palette.Danger),
            _ => foreground,
        };

        _tray.Show(LogoRenderer.RenderTrayIcon(TrayIcon.IconSize, foreground, status), state.TrayTooltip);
    }

    private void OpenTrayMenu()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _trayMenu ??= BuildTrayMenu(_viewModel);
        _trayMenu.Placement = PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;

        // A menu opened from the tray must own the foreground, or it will not close when the user clicks elsewhere.
        var handle = PresentationSource.FromVisual(_trayMenu) is HwndSource source ? source.Handle : _tray.WindowHandle;
        SetForegroundWindow(handle);
    }

    private ContextMenu BuildTrayMenu(MainViewModel viewModel)
    {
        var itemStyle = (Style)FindResource("TrayMenuItem");

        var open = new MenuItem { Header = "Open SoundBridge", Command = viewModel.ShowWindowCommand, Style = itemStyle, FontWeight = FontWeights.SemiBold };
        var toggle = new MenuItem { Command = viewModel.PrimaryCommand, Style = itemStyle };
        toggle.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(MainViewModel.TrayActionText)));
        var exit = new MenuItem { Header = "Exit", Command = viewModel.ExitCommand, Style = itemStyle, ToolTip = "Disconnects your phone and quits completely" };

        var menu = new ContextMenu { DataContext = viewModel, Style = (Style)FindResource("TrayMenu") };

        // Call actions sit at the top and exist only while there is a call.
        var visibility = (IValueConverter)FindResource("BoolToVisibility");
        void AddCallItem(string header, System.Windows.Input.ICommand command, string visibleWhen)
        {
            var item = new MenuItem { Header = header, Command = command, Style = itemStyle };
            item.SetBinding(UIElement.VisibilityProperty, new Binding(visibleWhen) { Converter = visibility });
            menu.Items.Add(item);
        }

        AddCallItem("Answer call", viewModel.AnswerCallCommand, nameof(MainViewModel.IsCallRinging));
        AddCallItem("Decline call", viewModel.DeclineCallCommand, nameof(MainViewModel.IsCallRinging));
        AddCallItem("Hang up", viewModel.HangUpCommand, nameof(MainViewModel.IsCallLive));
        var callSeparator = new Separator { Style = (Style)FindResource("TrayMenuSeparator") };
        callSeparator.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(MainViewModel.IsInCall)) { Converter = visibility });
        menu.Items.Add(callSeparator);

        menu.Items.Add(open);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });
        menu.Items.Add(exit);
        return menu;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
