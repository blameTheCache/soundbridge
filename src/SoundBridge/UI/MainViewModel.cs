using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoundBridge.Bluetooth;
using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using SoundBridge.Core.Settings;
using SoundBridge.Platform;

namespace SoundBridge.UI;

/// <summary>What the view model needs from the application shell.</summary>
internal interface IAppShell
{
    void ShowWindow();

    void HideWindowToTray();

    void ShowTrayHint();

    void Exit();
}

internal sealed class DeviceItem(AudioSource source) : ObservableObject
{
    private string _name = source.Name;
    private string _status = "Not connected";
    private bool _isLinked;

    public string Id { get; } = source.Id;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>State of SoundBridge's own audio link to this device.</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsLinked
    {
        get => _isLinked;
        set => SetProperty(ref _isLinked, value);
    }

    public AudioSource ToSource() => new(Id, Name);
}

internal sealed class ThemeOption(ThemePalette palette) : ObservableObject
{
    private bool _isSelected;

    public string Id { get; } = palette.Id;

    public string Name { get; } = palette.Name;

    public Brush Background { get; } = Brush(palette, palette.Background);

    public Brush Surface { get; } = Brush(palette, palette.Surface);

    public Brush Line { get; } = Brush(palette, palette.Line);

    public Brush Text { get; } = Brush(palette, palette.Text);

    public Brush Accent { get; } = Brush(palette, palette.Accent);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static SolidColorBrush Brush(ThemePalette palette, string hex)
    {
        var brush = new SolidColorBrush(palette.Color(hex));
        brush.Freeze();
        return brush;
    }
}

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string Category = "UI";
    private const int DoubleClickGuardMilliseconds = 500;
    private static readonly TimeSpan MusicRestoreDelay = TimeSpan.FromSeconds(1.5);

    private readonly ConnectionController _controller;
    private readonly CallsController _calls;
    private readonly AudioSourceWatcher _watcher;
    private readonly BluetoothRadioMonitor _radio;
    private readonly SettingsStore _store;
    private readonly StartupRegistration _startup;
    private readonly ThemeManager _themes;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private readonly IAppShell _shell;
    private readonly DispatcherTimer _countdown;

    private AppSettings _settings;
    private AudioSource? _target;
    private DeviceItem? _selectedDevice;
    private ConnectionPresentation _presentation;
    private readonly bool _hadPreferredPhoneAtStart;
    private bool _syncingSelection;
    private bool _radioUnavailable;
    private ConnectionPhase _lastPhase;
    private long _lastPrimaryPress = -DoubleClickGuardMilliseconds;
    private bool _launchAtSignIn;
    private bool _isSettingsOpen;
    private bool _isClosePromptOpen;
    private bool _isRefreshing;
    private bool _disposed;

    public MainViewModel(
        ConnectionController controller,
        CallsController calls,
        AudioSourceWatcher watcher,
        BluetoothRadioMonitor radio,
        SettingsStore store,
        AppSettings settings,
        StartupRegistration startup,
        ThemeManager themes,
        ILog log,
        Dispatcher dispatcher,
        IAppShell shell)
    {
        _controller = controller;
        _calls = calls;
        _watcher = watcher;
        _radio = radio;
        _store = store;
        _settings = settings;
        _startup = startup;
        _themes = themes;
        _log = log;
        _dispatcher = dispatcher;
        _shell = shell;

        _target = settings.PreferredDeviceId is { } id ? new AudioSource(id, settings.PreferredDeviceName ?? "Phone") : null;
        _hadPreferredPhoneAtStart = _target is not null;
        _launchAtSignIn = startup.IsEnabled;
        _presentation = ConnectionPresenter.Describe(controller.Current, _target is not null, hasSources: false, DateTimeOffset.Now);

        DarkThemes = [.. ThemeManager.Palettes.Where(palette => palette.IsDark).Select(palette => new ThemeOption(palette))];
        LightThemes = [.. ThemeManager.Palettes.Where(palette => !palette.IsDark).Select(palette => new ThemeOption(palette))];
        MarkSelectedTheme();

        PrimaryCommand = new RelayCommand(ExecutePrimary, () => _controller.Current.IsActive || _target is not null);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsRefreshing);
        OpenBluetoothSettingsCommand = new RelayCommand(() => Launch("ms-settings:bluetooth"));
        OpenSoundSettingsCommand = new RelayCommand(() => Launch("ms-settings:sound"));
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        SelectThemeCommand = new RelayCommand(parameter => SelectTheme(parameter as string));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        ShowWindowCommand = new RelayCommand(_shell.ShowWindow);
        ExitCommand = new RelayCommand(_shell.Exit);
        KeepInTrayCommand = new RelayCommand(() => AnswerClosePrompt(closeToTray: true));
        ExitOnCloseCommand = new RelayCommand(() => AnswerClosePrompt(closeToTray: false));
        DismissClosePromptCommand = new RelayCommand(() => IsClosePromptOpen = false);

        _countdown = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshPresentation(), dispatcher) { IsEnabled = false };

        _controller.Changed += OnConnectionChanged;
        _calls.Changed += OnCallsChanged;
        _calls.CallEnded += OnCallEnded;
        _watcher.Changed += OnSourcesChanged;
        _radio.Changed += OnRadioChanged;
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public IReadOnlyList<ThemeOption> DarkThemes { get; }

    public IReadOnlyList<ThemeOption> LightThemes { get; }

    public ICommand PrimaryCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenBluetoothSettingsCommand { get; }

    public ICommand OpenSoundSettingsCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand CloseSettingsCommand { get; }

    public ICommand SelectThemeCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public ICommand ShowWindowCommand { get; }

    public ICommand ExitCommand { get; }

    public ICommand KeepInTrayCommand { get; }

    public ICommand ExitOnCloseCommand { get; }

    public ICommand DismissClosePromptCommand { get; }

    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value) || _syncingSelection || value is null)
            {
                return;
            }

            SetTarget(value.ToSource());
        }
    }

    public bool HasDevices => Devices.Count > 0;

    /// <summary>The phone can only be switched while nothing is connected or in progress.</summary>
    public bool CanChooseDevice => !_controller.Current.IsActive;

    public string PhoneName => _target?.Name ?? "No phone selected";

    public string Tone => _presentation.Tone.ToString();

    public string StatusTitle => _presentation.Title;

    public string StatusDetail => _presentation.Detail;

    public string PrimaryText => _presentation.ActionText;

    /// <summary>"Connect" is the emphasised action; "Disconnect" and "Cancel" are deliberately quieter.</summary>
    public bool PrimaryIsAccent => !_controller.Current.IsActive;

    public string TrayActionText => _controller.Current.IsActive || _target is null
        ? _presentation.ActionText
        : $"Connect to {_target.Name}";

    public string TrayTooltip => _target is null
        ? "SoundBridge - no phone selected"
        : $"SoundBridge - {_presentation.Title} - {_target.Name}";

    public bool ShowBluetoothBanner => _radio.State is BluetoothRadioState.Off or BluetoothRadioState.NoAdapter;

    public string BluetoothBannerText => _radio.State == BluetoothRadioState.NoAdapter
        ? "No Bluetooth adapter was found on this PC."
        : "Bluetooth is turned off on this PC.";

    public string EmptyListText => ShowBluetoothBanner
        ? "Paired phones appear here once Bluetooth is on."
        : "No paired audio sources yet. Pair your phone in Windows Bluetooth settings and it will appear here.";

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool IsClosePromptOpen
    {
        get => _isClosePromptOpen;
        set => SetProperty(ref _isClosePromptOpen, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public bool LaunchAtSignIn
    {
        get => _launchAtSignIn;
        set
        {
            if (value != _launchAtSignIn && _startup.SetEnabled(value))
            {
                _launchAtSignIn = value;
            }

            // Always re-read: if Windows refused the change the switch must fall back.
            OnPropertyChanged();
        }
    }

    public bool StartHiddenAtSignIn
    {
        get => _settings.StartHiddenAtSignIn;
        set
        {
            if (Save(_settings with { StartHiddenAtSignIn = value }))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool ConnectOnLaunch
    {
        get => _settings.ConnectOnLaunch;
        set
        {
            if (Save(_settings with { ConnectOnLaunch = value }))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set
        {
            if (Save(_settings with { AutoReconnect = value }))
            {
                _controller.SetPolicy(_settings.ToReconnectPolicy());
                OnPropertyChanged();
            }
        }
    }

    public int MaxReconnectAttempts
    {
        get => _settings.MaxReconnectAttempts;
        set
        {
            if (Save(_settings with { MaxReconnectAttempts = value }))
            {
                _controller.SetPolicy(_settings.ToReconnectPolicy());
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Take the hands-free role so calls answered on the phone move to this PC.</summary>
    public bool RouteCalls
    {
        get => _settings.RouteCalls && CanRouteCalls;
        set
        {
            if (Save(_settings with { RouteCalls = value }))
            {
                _log.Info(Category, $"Call routing switched {(value ? "on" : "off")}.");
                ApplyCallRouting();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowCallsLine));
            }
        }
    }

    /// <summary>False when this copy of the app, or this Windows version, cannot take calls at all.</summary>
    public bool CanRouteCalls => _calls.Support == CallSupport.Available;

    public string CallsStatusText => ConnectionPresenter.DescribeCalls(_calls.Current, _calls.Support);

    public bool ShowCallsLine => RouteCalls;

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            // Choosing here answers the first-close question as well.
            if (Save(_settings with { CloseToTray = value, CloseChoiceMade = true }))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(CloseButtonTip));
            }
        }
    }

    public string CloseButtonTip => !_settings.CloseChoiceMade
        ? "Close"
        : _settings.CloseToTray ? "Close to tray - SoundBridge keeps running" : "Exit SoundBridge";

    public string VersionText =>
        $"SoundBridge {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev"}";

    /// <summary>Loads the radio state and device list, then begins the session's automatic connect if enabled.</summary>
    public async Task InitializeAsync()
    {
        await _radio.RefreshAsync();
        ApplyRadioState();

        _watcher.Start();
        await _watcher.RefreshAsync();
        SyncDevices();
        ApplyCallRouting();

        // A new session starts each time the app starts. This is the only automatic connect that does not
        // follow an established connection, and it is the documented point where a manual disconnect from a
        // previous session stops applying. A phone first seen in this session is never connected unasked.
        if (_settings.ConnectOnLaunch && _hadPreferredPhoneAtStart && _target is not null && !_disposed)
        {
            _log.Info(Category, "New session: connecting to the preferred phone.");
            _controller.Connect(_target, ConnectOrigin.SessionStart);
        }
    }

    /// <summary>Called when the window's close button, Alt+F4 or the taskbar asks to close.</summary>
    public void OnCloseRequested()
    {
        if (!_settings.CloseChoiceMade)
        {
            _log.Info(Category, "Close requested; asking whether to keep running in the tray.");
            IsClosePromptOpen = true;
        }
        else if (_settings.CloseToTray)
        {
            _log.Info(Category, "Close requested; hiding to the tray.");
            HideToTray();
        }
        else
        {
            _log.Info(Category, "Close requested; exiting.");
            _shell.Exit();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _countdown.Stop();
        _controller.Changed -= OnConnectionChanged;
        _calls.Changed -= OnCallsChanged;
        _calls.CallEnded -= OnCallEnded;
        _watcher.Changed -= OnSourcesChanged;
        _radio.Changed -= OnRadioChanged;
    }

    private void ExecutePrimary()
    {
        // The button's meaning flips after each press, so the second click of a double-click
        // would undo the first (connect, then cancel). Swallow presses that arrive that fast.
        var now = Environment.TickCount64;
        if (now - _lastPrimaryPress < DoubleClickGuardMilliseconds)
        {
            _log.Info(Category, "Ignored a repeated press of the main action.");
            return;
        }

        _lastPrimaryPress = now;
        _log.Info(Category, $"Action requested: {PrimaryText}.");
        if (_controller.Current.IsActive)
        {
            _controller.Disconnect();
        }
        else if (_target is not null)
        {
            _controller.Connect(_target, ConnectOrigin.Manual);
        }
    }

    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await _radio.RefreshAsync();
            await _watcher.RefreshAsync();

            // Long enough for the refresh to register as having happened.
            await Task.Delay(350);
        }
        finally
        {
            IsRefreshing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void AnswerClosePrompt(bool closeToTray)
    {
        IsClosePromptOpen = false;
        _log.Info(Category, $"Close behaviour chosen: {(closeToTray ? "keep running in the tray" : "exit")}.");
        Save(_settings with { CloseToTray = closeToTray, CloseChoiceMade = true });
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(CloseButtonTip));

        if (closeToTray)
        {
            HideToTray();
        }
        else
        {
            _shell.Exit();
        }
    }

    private void HideToTray()
    {
        _shell.HideWindowToTray();
        if (!_settings.TrayHintShown)
        {
            _shell.ShowTrayHint();
            Save(_settings with { TrayHintShown = true });
        }
    }

    private void SelectTheme(string? id)
    {
        if (id is null || string.Equals(id, _settings.Theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Save(_settings with { Theme = id });
        _themes.Apply(id);
        MarkSelectedTheme();
    }

    private void MarkSelectedTheme()
    {
        var current = ThemeManager.Find(_settings.Theme).Id;
        foreach (var option in DarkThemes.Concat(LightThemes))
        {
            option.IsSelected = option.Id == current;
        }
    }

    private void SetTarget(AudioSource source)
    {
        if (_target == source)
        {
            return;
        }

        _target = source;
        Save(_settings with { PreferredDeviceId = source.Id, PreferredDeviceName = source.Name });
        _log.Info(Category, $"Preferred phone is now '{source.Name}'.");
        ApplyCallRouting();
        RefreshPresentation();
    }

    private bool Save(AppSettings settings)
    {
        if (settings == _settings)
        {
            return false;
        }

        _settings = settings;
        _store.Save(settings);
        return true;
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // An open music link proves the phone is in range: a good moment to bring the hands-free link up too.
        var phase = _controller.Current.Phase;
        if (phase == ConnectionPhase.Connected && _lastPhase != ConnectionPhase.Connected)
        {
            _calls.NotifyPhoneReachable();
        }

        _lastPhase = phase;
        RefreshPresentation();
    });

    private void OnCallsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(CallsStatusText)));

    /// <summary>
    /// After a call Windows keeps reporting the music link as open but plays nothing from it, and the phone
    /// goes on "playing" into silence. A fresh link brings the sound back, which is what reconnecting by hand
    /// does. The short wait lets the call's own audio channel close first, and a new call cancels it.
    /// </summary>
    private async void OnCallEnded(object? sender, EventArgs e)
    {
        try
        {
            await Task.Delay(MusicRestoreDelay).ConfigureAwait(false);
            if (!_disposed && _calls.Current.State != CallsState.InCall)
            {
                _controller.Reopen("a phone call ended");
            }
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"Could not restore the music link after the call: {ex.Message}");
        }
    }

    /// <summary>Tells the calls controller the current wish. It works out what, if anything, has to change.</summary>
    private void ApplyCallRouting() => _calls.Configure(_settings.RouteCalls, _target);

    private void OnSourcesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(SyncDevices);

    private void OnRadioChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        var wasUnavailable = _radioUnavailable;
        ApplyRadioState();
        if (wasUnavailable && !_radioUnavailable)
        {
            // Paired devices are re-enumerated when the radio returns.
            _ = _watcher.RefreshAsync();
            _calls.NotifyPhoneReachable();
        }
    });

    private void ApplyRadioState()
    {
        // Unknown counts as available so a radio Windows will not describe never blocks a connection.
        _radioUnavailable = ShowBluetoothBanner;
        _controller.SetBluetoothAvailable(!_radioUnavailable);
        OnPropertyChanged(nameof(ShowBluetoothBanner));
        OnPropertyChanged(nameof(BluetoothBannerText));
        OnPropertyChanged(nameof(EmptyListText));
        RefreshPresentation();
    }

    private void SyncDevices()
    {
        if (_disposed)
        {
            return;
        }

        var sources = _watcher.Sources;
        var known = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);

        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!known.ContainsKey(Devices[index].Id))
            {
                Devices.RemoveAt(index);
            }
        }

        foreach (var source in sources)
        {
            if (Devices.FirstOrDefault(device => device.Id == source.Id) is { } existing)
            {
                existing.Name = source.Name;
            }
            else
            {
                Devices.Add(new DeviceItem(source));
            }
        }

        if (_target is null && sources.Count > 0)
        {
            // Nothing chosen yet: adopt the first paired phone so the first click can be "Connect".
            SetTarget(sources[0]);
        }
        else if (_target is not null && known.TryGetValue(_target.Id, out var current) && current.Name != _target.Name)
        {
            SetTarget(current);
        }

        _syncingSelection = true;
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == _target?.Id);
        _syncingSelection = false;

        OnPropertyChanged(nameof(HasDevices));
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _controller.Current;
        _presentation = ConnectionPresenter.Describe(snapshot, _target is not null, Devices.Count > 0, DateTimeOffset.Now);
        _countdown.IsEnabled = snapshot.Phase == ConnectionPhase.WaitingToRetry;

        foreach (var device in Devices)
        {
            var linked = snapshot.IsActive && snapshot.Device?.Id == device.Id;
            device.IsLinked = linked;
            device.Status = !linked
                ? "Not connected"
                : snapshot.Phase switch
                {
                    ConnectionPhase.Connected => "Connected",
                    ConnectionPhase.Connecting when snapshot.RetryAttempt == 0 => "Connecting…",
                    ConnectionPhase.WaitingForBluetooth => "Waiting for Bluetooth",
                    _ => "Reconnecting…",
                };
        }

        OnPropertyChanged(nameof(PhoneName));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(PrimaryIsAccent));
        OnPropertyChanged(nameof(CanChooseDevice));
        OnPropertyChanged(nameof(TrayActionText));
        OnPropertyChanged(nameof(TrayTooltip));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenLogs()
    {
        if (_log is FileLog fileLog)
        {
            System.IO.Directory.CreateDirectory(fileLog.Directory);
            Launch(fileLog.Directory);
        }
    }

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn(Category, $"Could not open '{target}': {ex.Message}");
        }
    }
}
