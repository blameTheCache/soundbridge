# SoundBridge

Hear your Android phone and your Windows laptop at the same time, through whatever is plugged into the laptop.

SoundBridge turns the PC into a Bluetooth audio receiver for one phone, using the A2DP sink that has been
built into Windows since Windows 10 version 2004. Windows mixes the phone's audio with the laptop's own and
plays both on the current output device. Everything is local: no account, no cloud service, no phone app and
no audio driver.

> **Status.** Connecting, simultaneous phone and laptop playback, disconnecting and tray operation have
> been confirmed on real hardware (one laptop, one phone). Automatic reconnection, Bluetooth off/on and
> sleep/resume are covered by automated tests but **have not yet been exercised on hardware**. See
> [Verification](#verification) and the [hardware checklist](docs/hardware-test-checklist.md).

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- A Bluetooth adapter, and a phone already paired with the PC in Windows settings.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the default build
  (or build self-contained, below).
- No administrator rights, at any point.

## Run

Start `dist\SoundBridge\SoundBridge.exe`. The window opens on every launch you start yourself.

1. Pair the phone with the PC if you have not already (**Bluetooth settings** opens the right page).
2. The phone appears under **Audio sources** and is selected. Click **Connect**.
3. Play something on the phone.

## Build

```powershell
# Tests, then a single-file build in .\dist\SoundBridge (needs the .NET 10 SDK)
.\build.ps1

# Same, bundling the .NET runtime so the target PC needs nothing installed
.\build.ps1 -SelfContained

# Or step by step
dotnet test tests/SoundBridge.Core.Tests
dotnet publish src/SoundBridge -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/SoundBridge

# Run from source
dotnet run --project src/SoundBridge
```

## How it behaves

### Connection states

| State | Meaning | Main button |
|-------|---------|-------------|
| Not connected | No link, nothing pending. | Connect |
| Connecting… / Reconnecting… | A request to Windows is in flight. | Cancel |
| Connected | Windows reports the audio link as open. | Disconnect |
| Reconnecting in N s | An automatic retry is scheduled. | Cancel |
| Waiting for Bluetooth | The PC's Bluetooth is off; reconnection resumes when it returns. | Cancel |
| Couldn't connect / Connection lost | The last attempt failed and nothing is scheduled. | Connect |

"Connected" means the link is open. SoundBridge cannot see whether the phone is actually playing, so it
never says so, and it shows no level meters or latency figures.

### Automatic reconnection

With **Reconnect automatically** on, SoundBridge restores a link that it had established when:

- the link drops (phone out of range, phone Bluetooth off),
- the PC wakes from sleep,
- the PC's Bluetooth is switched back on.

Retries are bounded: 3, 5 or 10 attempts, waiting 2 s, 4 s, 8 s, 16 s, 32 s and then 60 s between them.
When they run out it stops and says so. A connection you start by hand is not retried if its first attempt
fails — you get the reason straight away. A refusal by Windows ("denied by system") is never retried.

### Intentional disconnects

**Disconnect** and **Cancel** are final for the session. After either, nothing reconnects — not a retry
timer, not waking from sleep, not Bluetooth coming back — until you click **Connect**.

**A new session begins each time SoundBridge starts.** If **Connect when SoundBridge starts** is on (the
default), the remembered phone is connected at start-up, including after a manual disconnect in the previous
session. Turn that setting off if you want a disconnect to survive restarts. A phone that SoundBridge sees
for the first time is never connected automatically.

### Closing and exiting

- **Close (X)** hides the window to the tray and keeps the phone connected. The first time, SoundBridge asks
  which you want; the choice is under **Settings → Tray**, and the X button's tooltip states what it will do.
- **Exit** (tray menu, or **Settings → About**) disconnects the phone and ends the process.
- The tray icon's centre dot shows the state: green connected, amber working or waiting, red failed.
- **Launch at sign-in** writes a per-user entry under `HKCU\...\Run`. Started that way, SoundBridge stays in
  the tray unless **Start in the tray at sign-in** is turned off.

### Where things are stored

| What | Where |
|------|-------|
| Settings | `%LOCALAPPDATA%\SoundBridge\settings.json` |
| Logs | `%LOCALAPPDATA%\SoundBridge\logs\soundbridge-YYYYMMDD.log` (7 days kept, 2 MB per file) |

Logs record connection events, errors and the actions you take. They contain device names and Windows device
IDs (which include Bluetooth addresses), and they never leave the PC. Audio does not pass through the
SoundBridge process, so it cannot be logged or recorded.

## Architecture

**C# on .NET 10, WPF for the interface.** The upstream project is C++/WinRT, but the part worth reusing is a
~40-line call sequence; the rest is a tray flyout around the system device picker. Re-expressing that sequence
in C# buys a declarative UI (themes, animation) and — more importantly — a connection state machine that is
plain .NET and can be tested with a fake link and a fake clock. The cost is a dependency on the .NET runtime.
There are no third-party packages in the shipped app.

```
src/SoundBridge.Core        no Windows APIs; fully unit-tested
  Connection/               ConnectionController (state + reconnection), ReconnectPolicy, IAudioLink
  Settings/                 AppSettings, SettingsStore (JSON, atomic write, falls back to defaults)
  Diagnostics/              ILog, FileLog (daily files, retention, size cap)

src/SoundBridge             the Windows app
  Bluetooth/                BluetoothAudioLink (AudioPlaybackConnection), AudioSourceWatcher (DeviceWatcher),
                            BluetoothRadioMonitor
  Platform/                 TrayIcon (Shell_NotifyIcon), StartupRegistration (Run key), PowerMonitor,
                            SingleInstance
  UI/                       MainWindow, MainViewModel, ConnectionPresenter, ThemeManager, Styles
  App.xaml.cs               composition root

tests/SoundBridge.Core.Tests   xUnit
tools/IconGen                  regenerates Assets/app.ico from the drawing code the tray icon uses
```

**Connection control.** `ConnectionController` is the only owner of the audio link. Its state is an immutable
`ConnectionSnapshot` behind one lock. Every open attempt and every retry timer belongs to an *activity*; any
change of intent retires the current activity, so a late result from an abandoned attempt, a timer that had
already fired, or a "closed" event from a link that was let go is recognised as stale and dropped. Retired
links are disposed before the next one is opened, and event handlers are removed when a link is retired.
`OpenAsync` receives a cancellation token; whether Windows abandons the request early is up to Windows, so
the result of a cancelled attempt is ignored regardless.

**Windows APIs used.** `Windows.Media.Audio.AudioPlaybackConnection`,
`Windows.Devices.Enumeration.DeviceWatcher`, `Windows.Devices.Radios.Radio` (read-only),
`Shell_NotifyIcon`, `DwmSetWindowAttribute`, and `SystemEvents.PowerModeChanged`. `AudioPlaybackConnection`
is declared agile with `ThreadingModel.Both` in the OS metadata, so it is created and closed on thread-pool
threads and never depends on the UI thread.

## Verification

### Automated (run on every `build.ps1`)

57 unit tests in `tests/SoundBridge.Core.Tests`, against a fake link and a fake clock:

- duplicate connect requests while connecting or connected start nothing new;
- manual disconnect cancels an in-flight attempt, ignores its late success, cancels a pending retry, and is
  not undone by a late "closed" event, Bluetooth toggling, sleep/resume or a settings change;
- retry timing (2, 4, 8, 16, 32 s), the retry bound, and the budget resetting after a successful reconnect;
- Bluetooth off/on and sleep/resume paths, with and without automatic reconnection;
- switching phones releases the old link first; a link that closes while opening is treated as a failure;
- a randomised multi-threaded run that must leave no link undisposed and the state disconnected;
- settings round-trip, corrupt-file recovery and value clamping; log format, rotation and retention.

As a check on the tests themselves, seven deliberate faults were introduced into the controller one at a
time; six were caught, and the seventh (not cancelling the timer on disconnect) is harmless because a second
guard still rejects the stale timer.

### Confirmed on hardware

By the project owner in October 2026, on one setup: a Windows 11 laptop, an Android phone paired over
Bluetooth, and wired headphones on the laptop.

- **Connecting and disconnecting.** Eleven connections in one session, each reported by Windows as opened,
  with no warnings or errors in the log.
- **Simultaneous playback.** Phone audio heard through the laptop's headphones alongside the laptop's own
  audio. This is the owner's observation; the app cannot see or hear audio itself.
- **Tray operation.** Closing to the tray with the phone connected, and reopening from the tray icon.
- **Launch at sign-in** switched on from Settings. An actual start at sign-in has not been observed yet.

One phone and one Bluetooth adapter is a single data point, not a compatibility claim.

### Exercised on a real machine with a simulated link

First-launch window, device listing, all six themes, settings persistence, the close prompt,
single-instance activation, hidden start with `--autostart`, the tray menu and clean exit. Dropping,
retrying and failing were driven through a **simulated link** in a Debug build (see Development), so those
screens are verified but the Bluetooth behaviour behind them is not.

### Not yet exercised on hardware

The log from the hardware session contains no dropped link, retry, Bluetooth-off or sleep event, so these
paths have only run against the fake and simulated links:

- automatic reconnection after the phone drops or goes out of range, and giving up after the retry limit;
- switching the laptop's Bluetooth off and on;
- sleep and resume;
- starting at sign-in, and exiting while connected.

Sections C to F and H of [docs/hardware-test-checklist.md](docs/hardware-test-checklist.md) cover them.

## Phone volume and output device: what was investigated

`AudioPlaybackConnection` has these members and no others (checked against the Windows SDK 10.0.26100
headers): `Start`/`StartAsync`, `Open`/`OpenAsync`, `State`, `StateChanged`, `DeviceId`, `Close`, and the
static `GetDeviceSelector` and `TryCreateFromId`. There is nothing for volume, levels or choosing an output.
The audio is rendered by Windows itself, not by this process, so SoundBridge has no audio stream of its own
to adjust or redirect.

- **Independent phone volume** — not available through this API. It may be reachable with the Core Audio
  session APIs (`IAudioSessionManager2`, `ISimpleAudioVolume`) *if* Windows exposes the phone's stream as an
  audio session; that is unconfirmed. Checklist item B5 (does the phone appear in the Volume mixer?) answers
  it. Until then, use the phone's own media volume.
- **Per-stream output device** — not available through this API, and Windows has no documented API for
  routing another process's stream. Deferred; the MVP plays on the Windows default output.

Both are deferred rather than promised.

## Known limitations

- Confirmed on one hardware setup only. Reconnection, Bluetooth toggling and sleep/resume have not been
  exercised on hardware, as stated above.
- One phone at a time. Phone calls, the headphone microphone, Wi-Fi streaming and recording are out of scope.
- The list shows the state of SoundBridge's own link. It does not show whether a phone is in range.
- After the retry limit is reached, reconnection needs a click; SoundBridge does not keep trying indefinitely.
- Sleep handling relies on Windows' suspend/resume notifications. If a device does not deliver them (some
  Modern Standby configurations), recovery falls back to the ordinary dropped-link path.
- Windows' **Startup apps** switch (Task Manager) can disable the sign-in entry while SoundBridge's toggle
  still shows it as on.
- Bluetooth audio has inherent delay and the codec is negotiated by Windows and the phone. SoundBridge does
  not measure or control either, and compatibility depends on the phone and the PC's Bluetooth adapter.
- Rounded corners and the coloured window border need Windows 11; on Windows 10 the window is square.
- Only the x64 build has been run. `-Runtime win-arm64` is provided but untried.

## Development

- `SOUNDBRIDGE_DATA_DIR=<folder>` relocates settings and logs, keeping test runs away from real settings.
- **Debug builds only:** `SOUNDBRIDGE_SIMULATE=<script>` replaces the Bluetooth link with a simulated one so
  every screen can be reached without a phone. The script is a comma-separated list applied to successive
  attempts, the last entry repeating: `ok`, `fail`, `deny`, `drop` (opens, then closes after 4 s). For
  example `drop,fail,ok` shows connect → drop → failed retry → successful retry. The window title reads
  "SoundBridge (simulated link)". This code is not compiled into Release builds.
- `dotnet run --project tools/IconGen -- src/SoundBridge/Assets/app.ico` regenerates the icon.

## Attribution and licence

SoundBridge is released under the [MIT License](LICENSE).

The Bluetooth connection flow is adapted from
[AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) by Richard Yu, also MIT-licensed.
The upstream copyright notice and licence text are reproduced in [NOTICE.md](NOTICE.md).

API references: [AudioPlaybackConnection](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection)
and [Enable audio playback from remote Bluetooth-connected devices](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/enable-remote-audio-playback).
