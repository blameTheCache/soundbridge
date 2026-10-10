# Hardware test checklist

These steps need a real phone, a real Bluetooth radio and ears. The automated tests cover the connection
logic against a fake link (see the README). Work through this list on the target laptop and note any step
whose result differs.

**Confirmed so far** (project owner, October 2026, one laptop and one phone): A1, B1 to B3, B6 and G1 to
G3. **Still open:** everything else, in particular sections C to F and H. Steps B4 and B5 are observations
to record rather than pass/fail checks.

**Setup used for the checklist:** Windows laptop, Android phone paired over Bluetooth, wired headphones on a
3.5 mm-to-USB-C adapter selected as the Windows output device.

When a step fails, open **Settings → Diagnostics → Open folder** and keep the latest `soundbridge-YYYYMMDD.log`.
Each line is timestamped; the `[Connection]` and `[Bluetooth]` lines around the failure are the useful ones.

## A. First launch

| # | Do this | Expect |
|---|---------|--------|
| A1 | Run `SoundBridge.exe` for the first time. | The window opens (not just a tray icon). Your paired phone is listed and shown at the top as "Not connected". Nothing connects by itself. |
| A2 | Look at the notification area. | A SoundBridge icon is present; hovering shows the phone name and "Not connected". |
| A3 | Click **Bluetooth settings**, then **Sound settings**. | Windows Settings opens on the Bluetooth page and the Sound page respectively. |
| A4 | Click the refresh button next to **Audio sources**. | The icon spins once and the list still shows the phone. |

## B. Simultaneous phone and laptop playback

| # | Do this | Expect |
|---|---------|--------|
| B1 | Start music or a video on the laptop. | Sound in the headphones. |
| B2 | Click **Connect**. | Spinner and "Connecting…", then a green ring, "Connected" and "Since hh:mm". The list row reads "Connected". |
| B3 | Play media on the phone. | Phone audio is heard in the headphones **together with** the laptop audio. |
| B4 | Change the phone's media volume. | Note what happens to the phone's loudness in the headphones (this is the investigation item in the README). |
| B5 | Open Windows **Volume mixer** while connected. | Note whether the phone appears as its own entry, and under what name. |
| B6 | Click **Disconnect**. | "Not connected". Phone audio stops in the headphones; the phone plays through its own speaker or pauses. Laptop audio continues. |

## C. Duplicate requests and cancelling

| # | Do this | Expect |
|---|---------|--------|
| C1 | Turn Bluetooth off on the phone, then click **Connect**. | "Connecting…" then "Couldn't connect" with a reason. No endless retrying (a manual attempt is not retried). |
| C2 | Click **Connect** and, while it says "Connecting…", click **Cancel**. | Returns to "Not connected" promptly and stays there. The log shows the request and a later "Ignored stale result" line if Windows answered after the cancel. |
| C3 | Double-click **Connect** quickly. | It connects once and stays connected: the second click is ignored rather than acting as "Cancel". The log shows one "Opening link" and "Ignored a repeated press of the main action." |

## D. Automatic reconnection

Settings → **Reconnect automatically** on, attempts = 5.

| # | Do this | Expect |
|---|---------|--------|
| D1 | While connected, turn Bluetooth off on the **phone**. | Within a few seconds: amber ring, "Reconnecting in N s", "Attempt 1 of 5 is next". |
| D2 | Turn the phone's Bluetooth back on before the attempts run out. | A later attempt succeeds and the state returns to "Connected". Phone audio resumes in the headphones once you press play. |
| D3 | Repeat D1 and leave the phone's Bluetooth off. | After attempt 5: "Couldn't reconnect … Stopped after 5 reconnection attempts." Nothing further happens until you click **Connect**. |
| D4 | Repeat D1, and during "Reconnecting in N s" click **Cancel**. | "Not connected". Wait two minutes with the phone's Bluetooth back on: it must **not** reconnect. |
| D5 | Connect, click **Disconnect**, then turn the phone's Bluetooth off and on. | Stays "Not connected". |
| D6 | Turn **Reconnect automatically** off, connect, then turn the phone's Bluetooth off. | "Connection lost" with no retries. |

## E. Bluetooth off and on (laptop)

| # | Do this | Expect |
|---|---------|--------|
| E1 | While connected (auto-reconnect on), turn Bluetooth off in Windows quick settings. | A banner "Bluetooth is turned off on this PC." appears; state "Waiting for Bluetooth". The list may empty. |
| E2 | Turn Bluetooth back on. | Banner disappears, the phone is listed again, "Reconnecting in 2 s", then "Connected". |
| E3 | Click **Disconnect**, turn Bluetooth off, then on. | Stays "Not connected" throughout. |
| E4 | With Bluetooth off, click **Connect**. | Immediate "Bluetooth is off" error, no spinner. Turning Bluetooth on clears the error to "Not connected" without connecting. |

## F. Sleep and resume

| # | Do this | Expect |
|---|---------|--------|
| F1 | While connected (auto-reconnect on), put the laptop to sleep for a minute, then wake it. | Shortly after wake: "Reconnecting…", then "Connected". The log has "Suspending while Connected" and "Resumed from sleep". |
| F2 | If the log has **no** "Windows is suspending" line (possible on Modern Standby hardware). | The link drop should still be recovered by the normal reconnection path; note this result either way. |
| F3 | Click **Disconnect**, sleep, wake. | Stays "Not connected". |
| F4 | Repeat F1 with the lid closed for 10+ minutes. | Same as F1. |

## G. Tray, closing and exiting

| # | Do this | Expect |
|---|---------|--------|
| G1 | On a fresh profile, click the window's **X**. | A prompt asks whether to keep running in the tray or exit. |
| G2 | Choose **Keep in tray** while connected. | Window disappears, tray icon remains, a one-time notification says SoundBridge is still running. Phone audio keeps playing. |
| G3 | Left-click the tray icon. | The window returns. |
| G4 | Right-click the tray icon. | A menu with **Open SoundBridge**, **Disconnect** (or **Connect to <phone>**) and **Exit**. It closes when you click elsewhere. |
| G5 | Use **Disconnect** / **Connect** from the tray menu. | Same behaviour as the main button; the tray icon's dot turns green when connected. |
| G6 | Click **X** again. | No prompt this time; it hides to the tray. The tooltip on **X** says "Close to tray". |
| G7 | Run `SoundBridge.exe` again while it is in the tray. | No second copy; the existing window comes forward. |
| G8 | Tray menu → **Exit** while connected. | Icon disappears, phone audio stops in the headphones, no `SoundBridge.exe` left in Task Manager. The log ends with "Stopped." |
| G9 | Settings → turn **Keep running when the window is closed** off, then click **X**. | The app exits completely (as G8). |
| G10 | Restart Explorer from Task Manager while SoundBridge runs. | The tray icon reappears. |

## H. Startup and session behaviour

| # | Do this | Expect |
|---|---------|--------|
| H1 | Settings → **Launch at sign-in** on. Sign out and in. | SoundBridge is running in the tray with no window (with **Start in the tray at sign-in** on). |
| H2 | With **Connect when SoundBridge starts** on, exit and start the app. | It connects to the preferred phone by itself, even if you had disconnected manually before exiting. This is the documented "new session" rule. |
| H3 | Turn **Connect when SoundBridge starts** off, exit and start the app. | It starts "Not connected" and stays that way. |
| H4 | Turn **Launch at sign-in** off. Sign out and in. | SoundBridge is not running. No UAC prompt appeared at any point. |

## J. Phone calls (Windows 11, installed version)

Install with `packaging\Install.ps1`, start SoundBridge from the Start menu, and turn on
**Settings → Hear phone calls on this PC**. The status under the switch should read "Ready".

**Confirmed so far** (project owner, October 2026): J1, J2, J3 and J7.

| # | Do this | Expect |
|---|---------|--------|
| J1 | Turn the switch on, off, and on again. | Status goes "Ready" → "Off." → "Ready". The log shows the role being claimed, unregistered, and claimed again. |
| J2 | Have someone call the phone. Answer **on the phone**. | Within a second or two the caller's voice moves to the PC's headphones, and they can hear you through the PC's microphone. No window pops up on the PC. Status reads "Call in progress on this PC." |
| J3 | Hang up. | Status returns to "Ready". The log shows "The call ended." |
| J4 | Let a call ring without answering, then decline it on the phone. | Nothing moves to the PC. |
| J5 | During a call on the PC, switch the audio back to the phone from the phone's call screen. | The call continues on the phone and SoundBridge does not pull it back. |
| J6 | Place an **outgoing** call from the phone. | Once the other side answers, the call moves to the PC as in J2. Note what you hear while it is still ringing out. |
| J7 | Take a call while phone music is playing through SoundBridge. | Music pauses for the call. A second or two after hanging up the status briefly shows "Connecting…", then "Connected", and the music is audible again without touching anything. The log shows "Reopening the link … a phone call ended". |
| J8 | Turn the switch off, then take a call. | The call stays on the phone. |
| J9 | Exit SoundBridge from the tray, then take a call. | The call stays on the phone. Note whether Windows starts SoundBridge by itself. |
| J10 | Run the plain `dist\SoundBridge\SoundBridge.exe` instead of the installed app. | The switch is disabled and the status explains that calls need the installed version. |
| J11 | If callers say you sound quiet. | Windows Sound settings → your default communication microphone → check its input volume. |

## I. Persistence and appearance

| # | Do this | Expect |
|---|---------|--------|
| I1 | Change the theme (try all six), exit, start again. | The chosen theme is restored. |
| I2 | Change reconnection attempts and toggles, exit, start again. | Values are restored. |
| I3 | Pair a second phone, select it in the list while disconnected. | The top panel switches to it; it is remembered after a restart. While connected, the list cannot be changed. |
