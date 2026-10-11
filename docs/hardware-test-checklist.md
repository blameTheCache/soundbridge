# Hardware test checklist

These steps need a real phone, a real Bluetooth radio and ears. The automated tests cover the connection
logic against a fake link (see the README). Work through this list on the target laptop and note any step
whose result differs.

**Confirmed so far** (project owner, October 2026, one laptop and one phone): A1, B1 to B3, B6 and G1 to
G3, and F3 (a four-hour sleep after a manual disconnect: Windows delivered the suspend and resume
notifications and the music link stayed disconnected). The session log also shows a dropped link recovering on the first retry twice (as in D1 and D2, though
what caused the drops is not known). **Still open:** everything else, in particular sections C to F and H. Steps B4 and B5 are observations
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
| B5 | Open Windows **Volume mixer** while connected. | The phone appears as its own entry, named after the phone ("… A2DP SNK"). Confirmed. |
| B3a | Play music on the phone with the SoundBridge window open. | Within a second or two the status changes from "Connected" to "Playing" and a ring of coloured bars moves around the phone, livelier on loud passages. |
| B3b | Pause the music on the phone. | About four seconds later the ring fades out and the status returns to "Connected". |
| B3c | Close the window to the tray while music plays, then hover the tray icon. | The tooltip says "Connected", not "Playing": nothing is sampled while the window is hidden. |
| B5a | Play music on the phone and drag SoundBridge's volume slider. | The phone gets quieter and louder in the headphones; laptop sounds do not change, and neither does the volume shown on the phone. The mixer entry from B5 moves with the slider. |
| B5b | Set the slider to about a third, disconnect, connect again. | The slider and the loudness are where you left them. |
| B5c | Change the phone's entry in the Windows Volume mixer, then click the SoundBridge window. | The slider moves to the new level. |
| B5d | Click the speaker button beside the slider. | The phone goes silent on the PC and the label reads "Muted"; clicking again restores it. |
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
| D3 | Repeat D1 and leave the phone's Bluetooth off. | After attempt 5: "Waiting for the phone". About every two minutes it briefly shows "Checking for the phone…" and returns to waiting. |
| D3a | While it is waiting, turn the phone's Bluetooth back on. | It reconnects within two minutes without a click, sooner if Windows notices the phone first. The log shows either "was seen on Bluetooth; checking now" or a scheduled check succeeding. |
| D3b | Turn **Keep waiting for the phone** off and repeat D3. | After attempt 5: "Couldn't reconnect … Stopped after 5 reconnection attempts." Nothing further happens until you click **Connect**. |
| D3c | While it is waiting, click **Cancel**, then turn the phone's Bluetooth back on. | "Not connected", and it stays that way. |
| D3d | While connected, disconnect this PC from the phone's own Bluetooth menu. Each time SoundBridge reconnects, do it again straight away. | After the third quick reconnection is closed, SoundBridge shows "Stopped reconnecting" and leaves the phone alone until you click **Connect**. |
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

**Confirmed so far** (project owner, October 2026): J1, J2, J3, J7, J12 and J13.

| # | Do this | Expect |
|---|---------|--------|
| J1 | Turn the switch on, off, and on again. | Status goes "Ready" → "Off." → "Ready". The log shows the role being claimed, unregistered, and claimed again. |
| J1a | With the switch on, turn the phone's Bluetooth off. | The status changes from "Ready" to saying the phone is not connected for calls. It returns to "Ready" when the phone is back. |
| J2 | Have someone call the phone. Answer **on the phone**. | Within a second or two the caller's voice moves to the PC's headphones, and they can hear you through the PC's microphone. No window pops up on the PC. Status reads "Call in progress on this PC." |
| J3 | Hang up. | Status returns to "Ready". The log shows "The call ended." |
| J4 | Let a call ring without answering, then decline it on the phone. | Nothing moves to the PC, and music that was playing is not interrupted by a reconnect. The log shows "The call ended without being answered." |
| J5 | During a call on the PC, switch the audio back to the phone from the phone's call screen. | The call continues on the phone and SoundBridge does not pull it back. |
| J6 | Place an **outgoing** call from the phone. | Once the other side answers, the call moves to the PC as in J2. Note what you hear while it is still ringing out. |
| J7 | Take a call while phone music is playing through SoundBridge. | Music pauses for the call. A second or two after hanging up the status briefly shows "Connecting…", then "Connected", and the music is audible again without touching anything. The log shows "Reopening the link … a phone call ended". |
| J8 | Turn the switch off, then take a call. | The call stays on the phone. |
| J9 | Exit SoundBridge from the tray, then take a call. | The call stays on the phone. Note whether Windows starts SoundBridge by itself. |
| J10 | Run the plain `dist\SoundBridge\SoundBridge.exe` instead of the installed app. | The switch is disabled and the status explains that calls need the installed version. |
| J11 | If callers say you sound quiet. | Windows Sound settings → your default communication microphone → check its input volume. |
| J12 | With the SoundBridge window closed to the tray, have someone call. | A small prompt appears above the taskbar with the caller's name or number, **Answer** and **Decline**. It does not take keyboard focus from what you were doing. |
| J13 | Click **Answer** on the prompt. | The call is answered without touching the phone and is heard on the PC. The prompt disappears. |
| J14 | On another call, click **Decline**. | The phone stops ringing and the caller is rejected. |
| J15 | During a call, open the window and use **Mute**, then **Unmute**. | The other side cannot hear you while muted. The strip reads "Call on this PC, muted". |
| J16 | Use **Move to phone**, then **Move to this PC**. | The audio follows each time, and SoundBridge does not move it again on its own. |
| J17 | Click **Hang up** (window or tray menu). | The call ends on the phone. Music, if connected, comes back as in J7. |
| J18 | Click the **x** on the prompt while the phone rings. | The prompt goes away and the phone keeps ringing. |
| J19 | Turn **Ask on this PC when the phone rings** off and take a call. | No prompt. The Answer and Decline buttons are still in the main window. |

## I. Persistence and appearance

| # | Do this | Expect |
|---|---------|--------|
| I1 | Change the theme (try all six), exit, start again. | The chosen theme is restored. |
| I2 | Change reconnection attempts and toggles, exit, start again. | Values are restored. |
| I3 | Pair a second phone, select it in the list while disconnected. | The top panel switches to it; it is remembered after a restart. While connected, the list cannot be changed. |
