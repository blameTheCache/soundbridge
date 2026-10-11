using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoundBridge.Bluetooth;
using SoundBridge.Core.Audio;
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
    private readonly PhonePresenceMonitor _presence;
    private readonly PhoneVolumeControl _volume;
    private readonly SettingsStore _store;
    private readonly StartupRegistration _startup;
    private readonly ThemeManager _themes;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private readonly IAppShell _shell;
    private readonly DispatcherTimer _countdown;
    private readonly DispatcherTimer _volumeSettle;
    private readonly DispatcherTimer _volumeLog;

    private AppSettings _settings;
    private AudioSource? _target;
    private DeviceItem? _selectedDevice;
    private ConnectionPresentation _presentation;
    private readonly bool _hadPreferredPhoneAtStart;
    private bool _syncingSelection;
    private bool _radioUnavailable;
    private ConnectionPhase _lastPhase;
    private PhoneVolume? _phoneVolume;
    private float? _pendingVolume;
    private PhoneVolume? _unsavedVolume;
    private bool _applyingVolume;
    private bool _restoringVolume;
    private bool _windowVisible;
    private bool _wasPlaying;
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
        PhonePresenceMonitor presence,
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
        _presence = presence;
        _volume = new PhoneVolumeControl(log);
        LevelMeter = new PhoneLevelMeter(log);
        LevelMeter.PlayingChanged += OnPlayingChanged;
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
        AnswerCallCommand = new RelayCommand(() => CallAction("Answer", _calls.Answer));
        DeclineCallCommand = new RelayCommand(() => CallAction("Decline", _calls.Decline));
        HangUpCommand = new RelayCommand(() => CallAction("Hang up", _calls.HangUp));
        ToggleMuteCommand = new RelayCommand(() => CallAction(CallMuteText, () => _calls.SetMuted(!_calls.Current.CallMuted)));
        ToggleCallDeviceCommand = new RelayCommand(() => CallAction(CallMoveText, () => _calls.MoveCall(!_calls.Current.CallOnPc)));
        TogglePhoneMuteCommand = new RelayCommand(TogglePhoneMute, () => HasPhoneVolume);

        _countdown = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshPresentation(), dispatcher) { IsEnabled = false };
        _volumeSettle = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => OnPhoneVolumeSettled(), dispatcher) { IsEnabled = false };
        _volumeLog = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => OnPhoneVolumeLogDue(), dispatcher) { IsEnabled = false };

        _controller.Changed += OnConnectionChanged;
        _calls.Changed += OnCallsChanged;
        _calls.CallEnded += OnCallEnded;
        _presence.Seen += OnPhoneSeen;
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

    public ICommand AnswerCallCommand { get; }

    public ICommand DeclineCallCommand { get; }

    public ICommand HangUpCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    public ICommand ToggleCallDeviceCommand { get; }

    public ICommand TogglePhoneMuteCommand { get; }

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

    /// <summary>Samples the phone's audio level while the window is on screen. The live indicator draws from it.</summary>
    public PhoneLevelMeter LevelMeter { get; }

    /// <summary>True while audio from the phone has actually been measured, not merely while the link is open.</summary>
    public bool IsPhonePlaying => LevelMeter.IsPlaying && _controller.Current.Phase == ConnectionPhase.Connected;

    /// <summary>
    /// True while Windows has a music stream for the phone whose volume can be read and set. The slider is
    /// shown only then, so it never offers a control that would do nothing.
    /// </summary>
    public bool HasPhoneVolume => _phoneVolume is not null && _controller.Current.Phase == ConnectionPhase.Connected;

    /// <summary>
    /// How loud the phone's music is on this PC, 0 to 100. It is the phone's entry in the Windows Volume mixer:
    /// separate from the phone's own volume and from every other sound on the PC.
    /// </summary>
    public double PhoneVolumePercent
    {
        get => Math.Round((_phoneVolume?.Level ?? 1f) * 100);
        set
        {
            var level = (float)Math.Clamp(value / 100, 0, 1);
            if (_phoneVolume is null || Math.Abs(level - _phoneVolume.Level) < 0.005f)
            {
                return;
            }

            _phoneVolume = _phoneVolume with { Level = level };
            _unsavedVolume = _phoneVolume;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PhoneVolumeText));
            QueuePhoneVolume(level);
        }
    }

    public bool IsPhoneMuted => _phoneVolume?.Muted == true;

    public string PhoneVolumeText => IsPhoneMuted ? "Muted" : $"{PhoneVolumePercent:0}%";

    public string PhoneVolumeGlyph => IsPhoneMuted ? "" : "";

    /// <summary>After the quick retries, go on checking for the phone instead of giving up.</summary>
    public bool KeepWaitingForPhone
    {
        get => _settings.KeepWaitingForPhone;
        set
        {
            if (Save(_settings with { KeepWaitingForPhone = value }))
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

    /// <summary>Show the Answer / Decline prompt when the phone rings.</summary>
    public bool ShowCallPrompt
    {
        get => _settings.ShowCallPrompt;
        set
        {
            if (Save(_settings with { ShowCallPrompt = value }))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool IsInCall => _calls.Current.State == CallsState.InCall;

    public bool IsCallRinging => IsInCall && _calls.Current.CallPhase == CallPhase.Incoming;

    /// <summary>A call that has been answered or is being dialled: the one hang up, mute and move apply to.</summary>
    public bool IsCallLive => IsInCall && !IsCallRinging;

    public string CallTitle => _calls.Current switch
    {
        { CallPhase: CallPhase.Incoming } => "Incoming call",
        { CallPhase: CallPhase.Dialing } => "Calling…",
        { CallPhase: CallPhase.Held } => "Call on hold",
        { CallOnPc: true, CallMuted: true } => "Call on this PC, muted",
        { CallOnPc: true } => "Call on this PC",
        _ => "Call on the phone",
    };

    public string CallCaller => _calls.Current.Caller ?? string.Empty;

    public string CallCallerOrUnknown => _calls.Current.Caller ?? "Unknown caller";

    public string CallMuteText => _calls.Current.CallMuted ? "Unmute" : "Mute";

    public string CallMoveText => _calls.Current.CallOnPc ? "Move to phone" : "Move to this PC";

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
        _ = _presence.WatchAsync(_target);

        // A new session starts each time the app starts. This is the only automatic connect that does not
        // follow an established connection, and it is the documented point where a manual disconnect from a
        // previous session stops applying. A phone first seen in this session is never connected unasked.
        // The window is usable while the lines above wait; the controller declines this connect if the user
        // has already connected or disconnected by hand.
        if (_settings.ConnectOnLaunch && _hadPreferredPhoneAtStart && _target is not null && !_disposed
            && _controller.Connect(_target, ConnectOrigin.SessionStart))
        {
            _log.Info(Category, "New session: connecting to the preferred phone.");
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

        // A slider moved a moment ago has not been saved yet; without this the next launch restores the old level.
        SaveUnsavedVolume();
        _disposed = true;
        _countdown.Stop();
        _volumeSettle.Stop();
        _volumeLog.Stop();
        LevelMeter.PlayingChanged -= OnPlayingChanged;
        LevelMeter.Dispose();
        _controller.Changed -= OnConnectionChanged;
        _calls.Changed -= OnCallsChanged;
        _calls.CallEnded -= OnCallEnded;
        _presence.Seen -= OnPhoneSeen;
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
        _ = _presence.WatchAsync(_target);
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
            _ = RestorePhoneVolumeAsync();
        }
        else if (phase != ConnectionPhase.Connected)
        {
            ShowPhoneVolume(null);
        }

        UpdateLevelMeter();

        _lastPhase = phase;
        RefreshPresentation();
    });

    private void OnCallsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        OnPropertyChanged(nameof(CallsStatusText));
        OnPropertyChanged(nameof(IsInCall));
        OnPropertyChanged(nameof(IsCallLive));
        OnPropertyChanged(nameof(CallTitle));
        OnPropertyChanged(nameof(CallCaller));
        OnPropertyChanged(nameof(CallCallerOrUnknown));
        OnPropertyChanged(nameof(CallMuteText));
        OnPropertyChanged(nameof(CallMoveText));

        // Last, because the app shows or hides the incoming-call prompt on this one.
        OnPropertyChanged(nameof(IsCallRinging));
    });

    /// <summary>The phone came up on Bluetooth: resume a wait for it, and bring the calls link up if it is down.</summary>
    private void OnPhoneSeen(object? sender, EventArgs e)
    {
        _controller.NotifyPhoneSeen();
        _calls.NotifyPhoneReachable();
    }

    /// <summary>
    /// Re-reads the phone's volume from Windows. It can also be changed in the Windows Volume mixer, so this
    /// runs when the window comes forward rather than on a timer.
    /// </summary>
    /// <summary>
    /// Called by the window when it appears, is hidden or is minimised. The level is sampled only while
    /// there is something on screen to show it, so a hidden app does no sampling.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        UpdateLevelMeter();
    }

    private void UpdateLevelMeter()
    {
        if (!_disposed && _windowVisible && _target?.Name is { } name && _controller.Current.Phase == ConnectionPhase.Connected)
        {
            LevelMeter.Start(name);
        }
        else
        {
            LevelMeter.Stop();
        }
    }

    private void OnPlayingChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (_wasPlaying != IsPhonePlaying)
        {
            _wasPlaying = IsPhonePlaying;
            _log.Info(Category, _wasPlaying ? "The phone's audio started." : "The phone's audio stopped.");
        }

        RefreshPresentation();
    });

    public void RefreshPhoneVolume()
    {
        // While a just-opened link is being brought back to the remembered level, Windows still holds its own
        // default; reading that now would mistake it for something the user chose.
        if (!_restoringVolume)
        {
            _ = SyncPhoneVolumeAsync(linkJustOpened: false);
        }
    }

    /// <summary>
    /// Brings the slider and Windows into agreement. Windows starts every new stream at full volume, so when
    /// the link has just opened the remembered level is put back; at any other time whatever Windows holds
    /// wins, because the user may have changed it in the Volume mixer.
    /// </summary>
    private async Task SyncPhoneVolumeAsync(bool linkJustOpened)
    {
        try
        {
            // The stream appears a moment after the link opens. Look often at first so that a remembered
            // low volume is back before much audio has played at full volume.
            var attempts = linkJustOpened ? 15 : 1;
            for (var attempt = 0; attempt < attempts && !_disposed; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(300);
                }

                var name = _target?.Name;
                if (name is null || _controller.Current.Phase != ConnectionPhase.Connected)
                {
                    return;
                }

                var found = await Task.Run(() => _volume.Read(name));
                if (_applyingVolume)
                {
                    // The slider is being moved right now; what it holds is newer than what was just read.
                    return;
                }

                if (found is null)
                {
                    continue;
                }

                // What the slider holds is newer than what is on disk, which is written a moment after it rests.
                var remembered = _unsavedVolume ?? _phoneVolume ?? new PhoneVolume(_settings.PhoneVolumeLevel, _settings.PhoneMuted);
                if (!linkJustOpened)
                {
                    RememberPhoneVolume(found);
                    ShowPhoneVolume(found);
                    return;
                }

                // Keep going for the whole window instead of stopping at the first sighting: for a moment
                // Windows can still list the previous link's stream, and the new one arrives at full volume.
                if (Math.Abs(found.Level - remembered.Level) >= 0.005f || found.Muted != remembered.Muted)
                {
                    await Task.Run(() =>
                    {
                        _volume.SetLevel(name, remembered.Level);
                        _volume.SetMuted(name, remembered.Muted);
                    });
                    _log.Info(Category, $"Put the phone's volume on this PC back to {remembered.Level:P0}{(remembered.Muted ? ", muted" : string.Empty)}.");
                }

                ShowPhoneVolume(remembered);
            }

            if (!linkJustOpened || _phoneVolume is null)
            {
                ShowPhoneVolume(null);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"Could not read the phone's volume: {ex.Message}");
        }
    }

    private async Task RestorePhoneVolumeAsync()
    {
        _restoringVolume = true;
        try
        {
            await SyncPhoneVolumeAsync(linkJustOpened: true);
        }
        finally
        {
            _restoringVolume = false;
        }
    }

    private void RememberPhoneVolume(PhoneVolume volume) =>
        Save(_settings with { PhoneVolumeLevel = volume.Level, PhoneMuted = volume.Muted });

    private void ShowPhoneVolume(PhoneVolume? volume)
    {
        if (_phoneVolume == volume)
        {
            return;
        }

        _phoneVolume = volume;
        OnPropertyChanged(nameof(HasPhoneVolume));
        OnPropertyChanged(nameof(PhoneVolumePercent));
        OnPropertyChanged(nameof(PhoneVolumeText));
        OnPropertyChanged(nameof(PhoneVolumeGlyph));
        OnPropertyChanged(nameof(IsPhoneMuted));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Applies slider movements one at a time, always ending on the latest position.</summary>
    private async void QueuePhoneVolume(float level)
    {
        // Each movement restarts the wait; saving and logging happen when the slider comes to rest.
        _volumeSettle.Stop();
        _volumeSettle.Start();

        _pendingVolume = level;
        if (_applyingVolume)
        {
            return;
        }

        _applyingVolume = true;
        try
        {
            while (_pendingVolume is { } next)
            {
                _pendingVolume = null;
                if (_target?.Name is { } name)
                {
                    await Task.Run(() => _volume.SetLevel(name, next));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"Could not set the phone's volume: {ex.Message}");
        }
        finally
        {
            _applyingVolume = false;
        }

    }

    /// <summary>Runs once the slider has been still for a moment, so a drag is saved as one change.</summary>
    private void OnPhoneVolumeSettled()
    {
        _volumeSettle.Stop();
        if (!_disposed && SaveUnsavedVolume())
        {
            // Logging waits longer than saving: a spell of nudging the slider becomes one line, not one per nudge.
            _volumeLog.Stop();
            _volumeLog.Start();
        }
    }

    /// <summary>
    /// Writes a slider change that is still waiting to be saved. Held apart from what is shown, which goes
    /// away when the link closes.
    /// </summary>
    private bool SaveUnsavedVolume()
    {
        if (_unsavedVolume is not { } volume)
        {
            return false;
        }

        _unsavedVolume = null;
        RememberPhoneVolume(volume);
        return true;
    }

    private void OnPhoneVolumeLogDue()
    {
        _volumeLog.Stop();
        if (_phoneVolume is not null && !_disposed)
        {
            _log.Info(Category, $"Phone volume on this PC set to {_phoneVolume.Level:P0}.");
        }
    }

    private void TogglePhoneMute()
    {
        if (_phoneVolume is null || _target?.Name is not { } name)
        {
            return;
        }

        var muted = !_phoneVolume.Muted;
        _log.Info(Category, $"Phone audio on this PC {(muted ? "muted" : "unmuted")}.");
        ShowPhoneVolume(_phoneVolume with { Muted = muted });
        RememberPhoneVolume(_phoneVolume!);
        _ = Task.Run(() => _volume.SetMuted(name, muted));
    }

    private void CallAction(string name, Action action)
    {
        // The caller's name or number is deliberately not part of this line.
        _log.Info(Category, $"Call action requested: {name}.");
        action();
    }

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
        _presentation = ConnectionPresenter.Describe(snapshot, _target is not null, Devices.Count > 0, DateTimeOffset.Now, IsPhonePlaying);
        _countdown.IsEnabled = snapshot.Phase == ConnectionPhase.WaitingToRetry;

        foreach (var device in Devices)
        {
            var linked = snapshot.IsActive && snapshot.Device?.Id == device.Id;
            device.IsLinked = linked;
            device.Status = !linked
                ? "Not connected"
                : snapshot.Phase switch
                {
                    ConnectionPhase.Connected when IsPhonePlaying => "Playing",
                    ConnectionPhase.Connected => "Connected",
                    ConnectionPhase.Connecting when snapshot.BackgroundCheck => "Checking…",
                    ConnectionPhase.WaitingForPhone => "Waiting for phone",
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
        OnPropertyChanged(nameof(IsPhonePlaying));
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
