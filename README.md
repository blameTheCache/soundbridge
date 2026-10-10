# SoundBridge

Hear your Android phone and your Windows laptop at the same time, through whatever is plugged into the laptop.

SoundBridge turns the PC into a Bluetooth audio receiver for one phone, using the A2DP sink that has been
built into Windows since Windows 10 version 2004. Windows mixes the phone's audio with the laptop's own and
plays both on the current output device. Everything is local: no account, no cloud service, no phone app and
no audio driver.

On Windows 11 the installed version can also take **phone calls**: answer on the phone as usual, and the call
moves to the PC's speaker or headphones and microphone.

> **Status.** Connecting, simultaneous phone and laptop playback, disconnecting and tray operation have
> been confirmed on real hardware (one laptop, one phone). Automatic reconnection, Bluetooth off/on and
> sleep/resume are covered by automated tests but **have not yet been exercised on hardware**. Phone calls
> through the app, and music coming back by itself after a call, have been confirmed on the same hardware.
> See
> [Verification](#verification) and the [hardware checklist](docs/hardware-test-checklist.md).

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- A Bluetooth adapter, and a phone already paired with the PC in Windows settings.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the default build
  (or build self-contained, below).
- No administrator rights for music.
- For phone calls: Windows 11, and Developer Mode switched on so the app can be installed without a signing
  certificate (switching Developer Mode on is the one step that asks for administrator consent).

## Run

Start `dist\SoundBridge\SoundBridge.exe`. The window opens on every launch you start yourself.

1. Pair the phone with the PC if you have not already (**Bluetooth settings** opens the right page).
2. The phone appears under **Audio sources** and is selected. Click **Connect**.
3. Play something on the phone.

### Phone calls (Windows 11)

Windows only gives the Bluetooth hands-free role to an *installed* app, so calls need one extra step:

1. Turn on **Developer Mode**: Settings > System > Advanced (older builds: "For developers").
2. Exit SoundBridge if it is running, then run `.\packaging\Install.ps1`.
3. Start **SoundBridge** from the Start menu. Started any other way it has no package identity and calls
   stay unavailable.
4. In Settings, turn on **Hear phone calls on this PC**.

Settings and logs are shared with the plain exe. `.\packaging\Install.ps1 -Uninstall` removes the
installation; keep `dist\SoundBridge-package` where it is until then, because Windows runs it in place.

## Build

```powershell
# Tests, then dist\SoundBridge (plain exe) and dist\SoundBridge-package (installable). Needs the .NET 10 SDK.
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

### Phone calls

With **Hear phone calls on this PC** on, SoundBridge registers the PC as the phone's hands-free device.

- **Answer on the phone.** Windows 11 shows no incoming-call prompt for this, and SoundBridge does not add
  one. As soon as the call is being talked on, SoundBridge asks Windows to move its audio to the PC.
- **Audio devices.** Windows carries the call between the phone and its *default communication* speaker and
  microphone (Settings > System > Sound). SoundBridge does not handle the audio and cannot choose the devices.
- **Each call is moved once.** If you send a call back to the phone from the phone's own call screen, it
  stays there.
- **Ringing calls are left alone**, so declining or ignoring a call works as it always did.
- **Music after a call.** When a call ends, Windows goes on reporting the music link as open but plays
  nothing from it, while the phone keeps "playing" into silence. SoundBridge therefore opens a fresh music
  link about a second and a half after each call. Expect a brief gap before the music is back.
- The role is held only while SoundBridge runs with the switch on. Switching it off, or exiting, gives it back.
- Windows starts the installed app by itself when a call arrives. If SoundBridge is already running, that
  extra launch ends at once without showing anything.

### Closing and exiting

- **Close (X)** hides the window to the tray and keeps the phone connected. The first time, SoundBridge asks
  which you want; the choice is under **Settings → Tray**, and the X button's tooltip states what it will do.
- **Exit** (tray menu, or **Settings → About**) disconnects the phone and ends the process.
- The tray icon's centre dot shows the state: green connected, amber working or waiting, red failed.
- **Launch at sign-in** writes a per-user entry under `HKCU\...\Run`; the installed version uses its
  package's startup task instead, which Windows also lists under Startup apps, and takes the setting over
  from the plain exe the first time it runs. Started at sign-in, SoundBridge stays in the tray unless
  **Start in the tray at sign-in** is turned off.

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
  Calls/                    CallsController (holds the hands-free role, moves calls), ICallTransport
  Settings/                 AppSettings, SettingsStore (JSON, atomic write, falls back to defaults)
  Diagnostics/              ILog, FileLog (daily files, retention, size cap)

src/SoundBridge             the Windows app
  Bluetooth/                BluetoothAudioLink (AudioPlaybackConnection), AudioSourceWatcher (DeviceWatcher),
                            BluetoothRadioMonitor, PhoneCallTransport (PhoneLineTransportDevice)
  Platform/                 TrayIcon (Shell_NotifyIcon), StartupRegistration (Run key or startup task),
                            PowerMonitor, SingleInstance, AppIdentity (packaged or not, why started)
  UI/                       MainWindow, MainViewModel, ConnectionPresenter, ThemeManager, Styles
  App.xaml.cs               composition root

tests/SoundBridge.Core.Tests   xUnit
tools/IconGen                  regenerates Assets/app.ico from the drawing code the tray icon uses
packaging/                     AppxManifest.xml, logos and Install.ps1 for the installed version
```

**Connection control.** `ConnectionController` is the only owner of the audio link. Its state is an immutable
`ConnectionSnapshot` behind one lock. Every open attempt and every retry timer belongs to an *activity*; any
change of intent retires the current activity, so a late result from an abandoned attempt, a timer that had
already fired, or a "closed" event from a link that was let go is recognised as stale and dropped. Retired
links are disposed before the next one is opened, and event handlers are removed when a link is retired.
`OpenAsync` receives a cancellation token; whether Windows abandons the request early is up to Windows, so
the result of a cancelled attempt is ignored regardless.

**Phone calls.** `CallsController` takes a wish ("route calls for this phone" or not) and a single worker
brings Windows in line with the latest wish one step at a time, so overlapping requests cannot interleave.
Two things are needed for Windows to register the app, and neither is obvious: `RequestAccessAsync` must be
called before `RegisterApp`, and the package manifest must declare the `windows.phoneCallActivation`
extension. Without the extension `RegisterApp` returns normally and registers nothing.

**Windows APIs used.** `Windows.Media.Audio.AudioPlaybackConnection`,
`Windows.Devices.Enumeration.DeviceWatcher`, `Windows.Devices.Radios.Radio` (read-only),
`Windows.ApplicationModel.Calls` (`PhoneLineTransportDevice`, `PhoneLine`, `PhoneCall`),
`Shell_NotifyIcon`, `DwmSetWindowAttribute`, and `SystemEvents.PowerModeChanged`. `AudioPlaybackConnection`
is declared agile with `ThreadingModel.Both` in the OS metadata, so it is created and closed on thread-pool
threads and never depends on the UI thread.

## Verification

### Automated (run on every `build.ps1`)

84 unit tests in `tests/SoundBridge.Core.Tests`, against a fake link, a fake clock and a fake call transport:

- duplicate connect requests while connecting or connected start nothing new;
- manual disconnect cancels an in-flight attempt, ignores its late success, cancels a pending retry, and is
  not undone by a late "closed" event, Bluetooth toggling, sleep/resume or a settings change;
- retry timing (2, 4, 8, 16, 32 s), the retry bound, and the budget resetting after a successful reconnect;
- Bluetooth off/on and sleep/resume paths, with and without automatic reconnection;
- switching phones releases the old link first; a link that closes while opening is treated as a failure;
- a randomised multi-threaded run that must leave no link undisposed and the state disconnected;
- settings round-trip, corrupt-file recovery and value clamping; log format, rotation and retention;
- call routing: the role is claimed once and released when switched off, on a phone change and on exit; a
  refused claim is reported and retried only when there is a new reason to; a call being talked on at the
  phone is moved once, a ringing call is not, and a call sent back to the phone is not pulled again;
- reopening the music link: only an established link is reopened, never one the user disconnected, and a
  reopen that fails is recovered like any dropped link.

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

### Phone calls on hardware

By the project owner in October 2026, on the same setup, running Windows 11:

- **Proven with a test build:** a packaged probe claimed the hands-free role, saw an incoming call, and
  moved it to the PC four seconds after it was answered on the phone. The owner heard the caller in the
  headphones and the caller heard the owner. Windows showed no incoming-call prompt. The music link stayed
  up throughout the call.
- **Confirmed in the app itself:** installing; the role being claimed, released and claimed again; and
  two real calls answered on the phone and moved to the PC, including while music was playing.
- **Found by that test:** after each call the music stayed silent although the app showed "Connected" and
  the phone showed the song playing. The log shows the owner disconnecting and reconnecting after both
  calls. SoundBridge now reopens the music link itself when a call ends.
- **Confirmed after that change:** a third call, with the music link reopened automatically a second and a
  half after hanging up and reported open again half a second later. The owner reports it working, and the
  log shows no manual reconnect afterwards.
- **Not yet done:** outgoing calls, and a call arriving while SoundBridge is not running.

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
- One phone at a time. Wi-Fi streaming and recording are out of scope.
- Phone calls need Windows 11 and the installed version, and installing currently needs Developer Mode
  because the package is unsigned. A signed `.msix` would remove that requirement.
- No call screen: SoundBridge does not ring, show who is calling, or offer answer and hang-up buttons. Answer,
  decline and hang up on the phone.
- Call audio uses Windows' default communication devices. If callers say you are too quiet, check that
  microphone's level in Windows; another project reports phones turning it down and Windows keeping it there.
- Only one app can hold a phone's calls. If Phone Link (or another app) has claimed the phone, Windows
  refuses SoundBridge; removing and re-pairing the phone is reported to clear that.
- Windows' automatic Focus rules (full-screen apps, games) are reported by another project to drop the
  hands-free link when they switch on or off. Not observed here yet.
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
- `dotnet run --project tools/IconGen -- src/SoundBridge/Assets/app.ico` regenerates the icon, and
  `dotnet run --project tools/IconGen -- --package-images packaging/Images` the package logos.
- Phone calls cannot be simulated or run from `dotnet run`: without package identity Windows answers
  `DeniedBySystem`. Build, install with `packaging\Install.ps1`, and start from the Start menu.

## Attribution and licence

SoundBridge is released under the [MIT License](LICENSE).

The Bluetooth connection flow is adapted from
[AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) by Richard Yu, also MIT-licensed.
The upstream copyright notice and licence text are reproduced in [NOTICE.md](NOTICE.md).

The phone-call support was written from scratch against Microsoft's API, with no code taken from other
projects. Two facts it depends on were learned by reading how others got it working, and are credited here:
the `windows.phoneCallActivation` manifest requirement, from [Sefirah](https://github.com/shrimqy/Sefirah)
(the one open-source app found doing this on Windows 11), and the need to call `RequestAccessAsync` before
`RegisterApp`, from the findings published with [klangbruecke](https://github.com/MYSTRAVIL/klangbruecke).

API references: [AudioPlaybackConnection](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioplaybackconnection)
and [Enable audio playback from remote Bluetooth-connected devices](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/enable-remote-audio-playback).
