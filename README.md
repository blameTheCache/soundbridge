# SoundBridge

Hear your Android phone and your Windows laptop at the same time, through whatever is plugged into the laptop.

SoundBridge turns the PC into a Bluetooth audio receiver for one phone, using the A2DP sink that has been
built into Windows since Windows 10 version 2004. Windows mixes the phone's audio with the laptop's own and
plays both on the current output device. Everything is local: no account, no cloud service, no phone app and
no audio driver.

On Windows 11 the installed version can also take **phone calls**: answer on the phone or from a prompt on
the PC, and the call moves to the PC's speaker or headphones and microphone.

> **Status.** Confirmed on real hardware (one laptop, one phone): connecting, simultaneous phone and laptop
> playback, disconnecting, tray operation, recovery from a dropped link, phone calls answered on the phone
> or from the PC's prompt, and music coming back by itself after a call. **Not yet exercised on hardware:**
> declining, muting and hanging up from the PC (seen only with simulated calls), waiting for an out-of-reach
> phone, Bluetooth off/on, and resuming an open music link after sleep. See [Verification](#verification) and the
> [hardware checklist](docs/hardware-test-checklist.md).

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- A Bluetooth adapter, and a phone already paired with the PC in Windows settings.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the default build
  (or build self-contained, below).
- No administrator rights for music.
- For phone calls: Windows 11 and the installed version. Installing needs either Developer Mode switched on,
  or a signed package whose certificate the PC trusts. Each asks for administrator consent once.

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

**Without Developer Mode.** `.\packaging\Build-Msix.ps1` packs the same folder into a signed
`dist\SoundBridge-<version>.msix` and writes `dist\SoundBridge.cer` beside it. It signs with a self-signed
certificate that it creates in your personal certificate store the first time. On the PC that will run it,
trust the certificate once from an elevated prompt, then install:

```powershell
Import-Certificate -FilePath .\SoundBridge.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\SoundBridge-<version>.msix
```

A self-signed certificate only shows that packages come from the same key, not who made them; whoever
installs has to decide to trust it.

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
| Playing | The link is open and the phone's audio is measurably above silence. | Disconnect |
| Reconnecting in N s | An automatic retry is scheduled. | Cancel |
| Waiting for Bluetooth | The PC's Bluetooth is off; reconnection resumes when it returns. | Cancel |
| Waiting for the phone | The quick retries ran out; the phone is checked for every two minutes. | Cancel |
| Checking for the phone… | One of those checks is in flight. | Cancel |
| Couldn't connect / Connection lost | The last attempt failed and nothing is scheduled. | Connect |

"Connected" means the link is open. **"Playing"** replaces it only while the phone's audio is actually
measured above silence: Windows keeps a level meter for the phone's stream, and SoundBridge reads it. No
latency figures are shown, because nothing measures them.

### The live indicator

While the phone is playing, a ring of coloured bars moves around the phone on the main screen.

- **It is drawn from measurements.** Each bar is how loud the phone was at one moment; the newest reading
  is at the head of the ring and older ones trail away down both sides. It is a short history of loudness,
  not a spectrum analyser: Windows reports how loud the stream is, not what frequencies are in it.
- **Scale.** Music stays in a narrow band of loudness, so the ring stretches the band in use across the
  bars' full travel. The readings are not altered, only the scale they are drawn on.
- **Cost.** The level is sampled about thirty times a second, and only while the window is on screen and
  the link is open. Hidden in the tray, SoundBridge samples nothing, so the tray icon and tooltip say
  "Connected" rather than "Playing". Measured on hardware with real audio: about 3% of one processor core
  while the ring animates, and none while nothing is playing.
- **Audio is not captured.** Only the level Windows already computes is read.

### Automatic reconnection

With **Reconnect automatically** on, SoundBridge restores a link that it had established when:

- the link drops (phone out of range, phone Bluetooth off),
- the PC wakes from sleep,
- the PC's Bluetooth is switched back on.

Quick retries come first: 3, 5 or 10 attempts, waiting 2 s, 4 s, 8 s, 16 s, 32 s and then 60 s between them.

If the phone still does not answer, SoundBridge **waits for it**: it checks every two minutes, and at once
when Windows reports the phone connecting over Bluetooth for any reason. This is still bounded. After 12
hours it stops and says so, and **Cancel** ends it at any moment. Turn **Keep waiting for the phone** off to
stop as soon as the quick retries run out instead.

A connection you start by hand is not retried if its first attempt fails — you get the reason straight
away. A refusal by Windows ("denied by system") is never retried.

**If the phone keeps closing the link.** SoundBridge cannot tell a phone that wandered off from one whose
owner picked another speaker or disconnected the PC on the phone. So it watches for the pattern instead: if
a restored link is closed again within a minute, three times running, it stops with "Stopped reconnecting"
and waits for you to click **Connect**. A link that holds for a minute counts as recovered.

### Intentional disconnects

**Disconnect** and **Cancel** are final for the session. After either, nothing reconnects — not a retry
timer, not waking from sleep, not Bluetooth coming back — until you click **Connect**.

**A new session begins each time SoundBridge starts.** If **Connect when SoundBridge starts** is on (the
default), the remembered phone is connected at start-up, including after a manual disconnect in the previous
session. Turn that setting off if you want a disconnect to survive restarts. A phone that SoundBridge sees
for the first time is never connected automatically.

### Phone volume

While connected, a slider under the status sets how loud the phone's music is **on this PC**. It is the
phone's own entry in the Windows Volume mixer, so it changes neither the phone's own volume nor any other
sound on the PC. The speaker button beside it mutes just the phone.

- The slider is shown only while Windows actually has a stream for the phone.
- The level is remembered. Windows may start a new stream at full volume when the link is reopened, so
  SoundBridge puts the remembered level back each time it connects, including after a call.
- If you change the level in the Windows Volume mixer instead, the slider catches up the next time the
  window comes to the front; it does not poll.

### Phone calls

With **Hear phone calls on this PC** on, SoundBridge registers the PC as the phone's hands-free device.

- **Answer on the phone or on the PC.** Windows 11 shows no incoming-call prompt of its own, so SoundBridge
  shows a small one above the taskbar with **Answer** and **Decline** and the caller as the phone reports it
  (turn it off under **Ask on this PC when the phone rings**). The same buttons are in the main window and
  the tray menu. However the call is answered, SoundBridge asks Windows to move its audio to the PC as soon
  as it is being talked on.
- **During a call** the main window shows **Mute**, **Move to phone** / **Move to this PC** and **Hang up**;
  the tray menu has **Hang up**.
- The PC does not ring. The phone does.
- **Audio devices.** Windows carries the call between the phone and its *default communication* speaker and
  microphone (Settings > System > Sound). SoundBridge does not handle the audio and cannot choose the devices.
- **Each call is moved once.** If you send a call back to the phone, from the phone's own call screen or
  with **Move to phone**, it stays there.
- **Ringing calls are left alone**, so declining or ignoring a call works as it always did.
- **Music after a call.** When a call ends, Windows goes on reporting the music link as open but plays
  nothing from it, while the phone keeps "playing" into silence. SoundBridge therefore opens a fresh music
  link about a second and a half after each call that was answered. Expect a brief gap before the music is
  back. A call that only rang leaves the music link alone.
- **Status.** "Ready" means the phone is connected for calls right now. If the role is held but the phone
  is not connected, the status says so instead.
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
IDs (which include Bluetooth addresses), and they never leave the PC. A caller's name or number is shown on
screen while the phone rings and is never written to the log. Audio does not pass through the
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
  Audio/                    PhoneAudioSessions (which Windows audio session is the phone's music),
                            LevelTracker (smoothed level, level history, "is it playing")
  Settings/                 AppSettings, SettingsStore (JSON, atomic write, falls back to defaults)
  Diagnostics/              ILog, FileLog (daily files, retention, size cap)

src/SoundBridge             the Windows app
  Bluetooth/                BluetoothAudioLink (AudioPlaybackConnection), AudioSourceWatcher (DeviceWatcher),
                            BluetoothRadioMonitor, PhoneCallTransport (PhoneLineTransportDevice),
                            PhonePresenceMonitor (BluetoothDevice connection status)
  Platform/                 TrayIcon (Shell_NotifyIcon), StartupRegistration (Run key or startup task),
                            PowerMonitor, SingleInstance, AppIdentity (packaged or not, why started),
                            PhoneVolumeControl and PhoneLevelMeter (Core Audio session volume and level)
  UI/                       MainWindow, CallPromptWindow, MainViewModel, ConnectionPresenter,
                            ThemeManager, Styles
  App.xaml.cs               composition root

tests/SoundBridge.Core.Tests   xUnit
tools/IconGen                  regenerates Assets/app.ico from the drawing code the tray icon uses
packaging/                     AppxManifest.xml, logos, Install.ps1 (Developer Mode) and Build-Msix.ps1 (signed)
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

136 unit tests in `tests/SoundBridge.Core.Tests`, against a fake link, a fake clock and a fake call transport:

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
  reopen that fails is recovered like any dropped link;
- waiting for the phone: entered when the quick retries run out, checked at the slow interval and at once
  on a sighting, ended by the 12-hour limit, by a manual disconnect, and by turning the option off;
- call actions: answer and decline apply only to a ringing call; hang up, mute and move to the call in
  progress; a call moved to the phone by hand is not pulled back; a button pressed while call routing is
  failing never acts on a later call;
- a link that is restored and closes again within a minute, three times running, ends reconnection, while
  restored links that hold do not count against it.

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
- **Recovery from a dropped link:** the same session's log shows the music link being closed by the phone or
  Windows twice, and reconnecting on the first retry about three seconds later both times.
- **Sleep and resume:** one sleep of about four hours. Windows delivered both the suspend and the resume
  notification; the calls link was back 14 seconds after waking; and music, which had been disconnected by
  hand before the sleep, correctly stayed disconnected.
- **Live indicator:** with the phone playing, the app logged "The phone's audio started" two seconds after
  connecting and showed "Playing" with the ring moving. Whether the ring follows the music convincingly is
  a matter for the eye and has not been judged by anyone but the author's screenshots.
- **Phone volume slider:** setting it to 63, 29 and 35 changed the Windows-side level to match within a
  tenth of a second, read back by a separate program each time; the saved 35% was still in force after a
  disconnect and reconnect. Putting a remembered level back onto a stream that Windows had reset to 100%
  was not seen in that run, because Windows kept the level by itself that time.
- **Answering from the PC:** a later call in the same session shows the prompt appearing, **Answer** being
  clicked ten seconds later and accepted by Windows, the call then moving to the PC, and the music link
  reopening when it ended.
- **Not yet done:** outgoing calls, and a call arriving while SoundBridge is not running. **Decline**,
  **Mute**, **Move** and **Hang up** have been exercised only against simulated calls in a Debug build, so
  the Windows functions behind them (`RejectIncoming`, `Mute`, `End`) have never run against a real call.

### Exercised on a real machine with a simulated link

First-launch window, device listing, all six themes, settings persistence, the close prompt,
single-instance activation, hidden start with `--autostart`, the tray menu and clean exit. Dropping,
retrying and failing were driven through a **simulated link** in a Debug build (see Development), so those
screens are verified but the Bluetooth behaviour behind them is not.

### Not yet exercised on hardware

These paths have only run against the fake and simulated links:

- the phone staying out of reach until the quick retries run out, the waiting that follows, and the
  reconnect when it returns. The signal used to notice the phone was checked on this PC: Windows reported
  it as disconnected while no link was up and as connected once the hands-free link was;
- switching the laptop's Bluetooth off and on;
- sleep and resume with the music link open (see below for what one real sleep did show);
- starting at sign-in;
- installing from the signed `.msix`. The package builds and is signed; nobody has installed from it yet.

Sections C to F and H of [docs/hardware-test-checklist.md](docs/hardware-test-checklist.md) cover them.

## Phone volume and output device: what was investigated

`AudioPlaybackConnection` has these members and no others (checked against the Windows SDK 10.0.26100
headers): `Start`/`StartAsync`, `Open`/`OpenAsync`, `State`, `StateChanged`, `DeviceId`, `Close`, and the
static `GetDeviceSelector` and `TryCreateFromId`. There is nothing for volume, levels or choosing an output.
The audio is rendered by Windows itself, not by this process, so SoundBridge has no audio stream of its own
to adjust or redirect.

- **Independent phone volume** — not available through this API, but available beside it. Windows plays
  the phone's music as an audio session of its own on the output device, named after the phone (for example
  "Microphone (My Phone A2DP SNK)"), and the Core Audio session API (`IAudioSessionManager2`,
  `ISimpleAudioVolume`) can set that session's volume. This is what the slider uses. Measured on hardware
  with only the phone playing: taking the session from 100% to 25% took the output device's peak level from
  about 0.02 to about 0.005, and back.
- **Per-stream output device** — not available through this API, and Windows has no documented API for
  routing another process's stream. Deferred; the MVP plays on the Windows default output.

Output routing stays deferred rather than promised.

## Known limitations

- Confirmed on one hardware setup only. Reconnection, Bluetooth toggling and sleep/resume have not been
  exercised on hardware, as stated above.
- One phone at a time. Wi-Fi streaming and recording are out of scope.
- Phone calls need Windows 11 and the installed version. The signed package uses a self-signed certificate,
  so it still needs one elevated step on each PC to trust it.
- The PC does not ring, and there is no dial pad or call history. Calls are placed from the phone.
- Call audio uses Windows' default communication devices. If callers say you are too quiet, check that
  microphone's level in Windows; another project reports phones turning it down and Windows keeping it there.
- Only one app can hold a phone's calls. If Phone Link (or another app) has claimed the phone, Windows
  refuses SoundBridge; removing and re-pairing the phone is reported to clear that.
- Windows' automatic Focus rules (full-screen apps, games) are reported by another project to drop the
  hands-free link when they switch on or off. Not observed here yet.
- The list shows the state of SoundBridge's own link. It does not show whether a phone is in range.
- Waiting for the phone ends after 12 hours; after that, reconnecting needs a click.
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
- Real phone calls cannot be run from `dotnet run`: without package identity Windows answers
  `DeniedBySystem`. Build, install with `packaging\Install.ps1`, and start from the Start menu.
- **Debug builds only:** `SOUNDBRIDGE_SIMULATE_CALLS=1` replaces the calls role with a simulated one. Seven
  seconds after call routing starts, a call from "Test Caller" rings, which is enough to see the prompt and
  every call button. The window title reads "SoundBridge (simulated calls)".
- **Debug builds only:** `SOUNDBRIDGE_SIMULATE_LEVEL=1` feeds the live indicator a made-up, music-like level
  so it can be seen without a phone playing. Combine it with `SOUNDBRIDGE_SIMULATE=ok`.
- A run with `SOUNDBRIDGE_DATA_DIR` set is its own single-instance scope, so a development copy can start
  beside the installed app.

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
