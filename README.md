# Wave Link Hotkey Manager (WLHK) 2.6

Wave Link Hotkey Manager is a lightweight background utility that provides system-wide custom hotkey support for Elgato Wave Link features. Configure tap, hold, and double-press actions per key for muting channels, adjusting volume, and switching output devices - natively on Windows, without a Stream Deck.

Built by FairTech.

**v2 is a ground-up rewrite in C# / .NET Native AOT.** The Electron runtime is gone:

|  | v1 (Electron) | v2 (Native AOT) |
|---|---|---|
| Idle RAM (private) | ~90 MB | **~13 MB** |
| Processes | 4+ (main, GPU, 2 renderers, key server) | **1** |
| Portable exe | self-extracts to `%TEMP%` every launch | **true single file, zero extraction** |
| Distribution | ~80 MB installer | **~18 MB exe, no runtime install** |
| Keystroke handling | out-of-process helper over stdio | in-process low-level hook |

## Features
- **Per-key trigger customization:** tap, hold (configurable duration), and double-press actions on any key or combo. Hold actions can repeat while the key is held, at a configurable interval.
- **Left/right modifiers:** optionally bind `LSHIFT+H` and `RSHIFT+H` to different actions; hotkeys recorded without a side keep matching either.
- **System-wide detection:** low-level OS keyboard hook - works regardless of app focus, bypassing Elgato's software limitations.
- **Smart OS suppression:** mapped keys are swallowed, so binding your keyboard's Volume Mute to a Wave Link channel doesn't also mute Windows.
- **Actions:** Mute (toggle, mute or unmute) and Volume (up, down or set a level) for a channel, a channel in one mix, a whole mix, an output device, the **Active Target**, or the focused app's channel. Plus Microphone, Effect, switch/cycle output device, Move Focused App, Focused App Volume, media keys and Change Target. Each action can show or hide the OSD on its own.
- **Microphone:** hardware mute, gain up/down/set in dB, the Wave's Mic/PC monitor mix, and gain lock.
- **Effects:** turn a channel's effects (noise gate, EQ, ...) on or off.
- **Focused app:** move the focused app to another Wave Link channel, or change its own volume the way the Windows Volume Mixer does.
- **Media controls:** Play/Pause, Next, Previous, Stop and system volume up/down/mute for your Windows default output device. Handled by Windows, so they work with any player and don't need Wave Link running.
- **Active Target:** keep a list of targets (channels, mixes, outputs, or the focused app's channel) and switch which one your hotkeys control, by cycling, picking one, holding a hotkey, or holding a modifier. Optionally remembered across restarts.
- **Volume keys:** hand your keyboard's volume keys over to the Active Target without recording a hotkey for each. They fall back to Windows whenever Wave Link isn't connected.
- **On-Screen Display:** frameless dark overlay with 9 anchor positions, shown on the monitor your cursor is on; text and volume-bar modes; configurable duration.
- **Auto-reconnect:** finds Wave Link via its `ws-info.json` port file (with a 1884–1893 scan fallback) and retries forever with backoff - at boot, after sleep/wake, or if Wave Link restarts.
- **Start with Windows**, **auto-elevate to Admin** (for hotkeys in elevated apps), single-instance, light/dark mode.
- **Portable data mode:** create a `WLHK_data` folder next to `WLHK.exe` and all settings live there instead of `%APPDATA%\WLHK`.
- **v1 config migration:** existing v1 hotkey configs are picked up automatically on first run.

## Requirements
- Windows 10/11 (x64)
- Elgato Wave Link 3.x

## Building
- .NET 10 SDK
- Visual Studio 2022+ with the "Desktop development with C++" workload (the Native AOT linker needs MSVC)

Run **`Build-Portable.bat`** - output is a single self-contained `dist\WLHK.exe`.

For development: `dotnet run` inside `src/` (JIT mode, no MSVC needed).

Run **`Build-Test.bat`** (or `dotnet test WLHK.slnx`) for the test suite.

## Usage
The app lives in your system tray. Double-click the tray icon to open the configuration window; right-click for quick toggles (hotkeys, OSD), reconnect, and quit.

Click **Record New Hotkey**, press the key or combo you want, then enable and configure any of the three triggers (Normal Press / Hold / Double Press) on its card.

Enable **Distinguish L/R Modifiers** in Global Settings to record new hotkeys with side-specific modifiers (`LSHIFT+H`, `RCTRL+RSHIFT+H`), so the left and right modifier keys can trigger different actions. Existing hotkeys are unaffected: a hotkey recorded as `SHIFT+H` still fires for either shift, and a side-specific hotkey takes precedence over the side-agnostic one when both are bound.

Each trigger picks an **Action** and, where it has one, a **Mode**: Mute can Toggle, Mute or Unmute (the last two always send that state, so other programs watching Wave Link can rely on them as on/off signals), and Volume can go Up, Down or Set Level. Untick **OSD** on any action to keep it silent.

On a **Hold** trigger, volume and gain steps, track skips and target cycling can be set to **Repeat** every so many milliseconds for as long as the key is held, so a held hotkey ramps the volume instead of needing to be tapped. With Repeat on, the interval also decides when a press becomes a hold, so **Hold Time** is greyed out. If the same key has a Normal Press or Double Press action, the first repeat waits at least 250 ms so quick taps still count as taps.

When a key has both a **Normal Press** and a **Double Press** action, a single press waits out the double-press window before running, so a double press runs only the Double Press action.

### Active Target

In **Global Settings**, build an **Active Target list**. An entry can be a channel (in all mixes or one), a whole mix, an output device, or **Focused App's Channel**, which follows whatever app has focus. Then set any Mute, Volume or Effect action's Target to **Active Target** and it controls whichever entry is active. Switch the active entry with a **Change Target** action:

- **Next Target / Previous Target** step through the list.
- **Select Target** jumps straight to one entry.
- **Target While Held** aims at an entry only while that key is held, then switches back. Use it on a Hold trigger, or on a Normal Press with nothing else set.

Each entry can also have a **Hold** modifier. Holding it while pressing an Active Target hotkey aims that one press at the entry: give *Wave Mic* the Shift modifier, and `SHIFT+F13` runs your `F13` volume hotkey against the mic. A hotkey you've actually recorded as `SHIFT+F13` always takes priority. The list starts on its first entry each time WLHK launches, unless **Persistent Target** is ticked, in which case it picks up where you left it.

### Volume Keys

Rather than recording `VOLUME_UP`, `VOLUME_DOWN` and `VOLUME_MUTE` one at a time, set **Keyboard volume keys** to **Control the Active Target** in Global Settings. They ramp on the keyboard's own key repeat like the native keys do, and only take over while Wave Link is connected, so you never end up with volume keys that do nothing. A recorded hotkey for the same key keeps priority, and this only fills in triggers it leaves empty. Target modifiers work here too, and a modifier that isn't assigned to a target falls through to Windows, so `CTRL+VOLUME_UP` still changes the Windows volume.

### Microphone, effects and apps

- **Microphone** controls the mic itself rather than a channel: hardware mute (the Wave's mute light), **gain** in whole dB within the device's range, the Wave's **Mic/PC mix** (the monitoring balance in your headphones), and **gain lock**. Mics without a feature say so on the OSD instead of guessing.
- **Effect** turns one of a channel's effects on or off. On a fixed channel you pick the effect itself; on the Active Target or the focused app's channel it's matched by name, so one hotkey can toggle *Noise Gate* on whichever channel is active.
- **Move Focused App** routes the app Wave Link sees as focused to a channel you pick.
- **Focused App Volume** changes the focused app's own volume, exactly like its slider in the Windows Volume Mixer. It works across every playback device, including each Wave Link channel, and covers apps that play from a helper process, like browsers and Discord. It doesn't need Wave Link running.

Media actions (Play / Pause, Next, Previous, Stop, System Volume Up/Down/Mute) press the matching Windows media key, so they reach whichever app currently has media control and move the volume of your default output device. They work whether or not Wave Link is running, and they're safe to bind to the physical media keys themselves: WLHK ignores the keypresses it generates, so nothing loops.

For channel actions, choose **All Mixes** to affect the channel everywhere, or select a named mix to affect only that mix. For example, configure Toggle Mute for **Microphone** in the **VC** mix to mute it only in VC while leaving its other mixes unchanged.

> **Upgrading from 2.5:** 2.6 merges the old one-per-behavior actions (Toggle Mute, Volume Up, Volume Down, Set Volume, and the individual media actions) into Mute, Volume and Media Key with a Mode. Your config is converted on first launch and the original is kept as `config.json.pre-2.6.bak`, which is what to restore if you go back to 2.5.

**Running as Administrator** is recommended if you use hotkeys while focused on elevated apps (Task Manager, some games). Enable "Auto-Elevate to Admin on Start" in Global Settings. The `--no-elevate` command-line flag skips auto-elevation for a single launch.

> **Note on Antivirus:** the global keyboard hook (`SetWindowsHookEx`) can trigger false positives in some AV products. The source is open for inspection; add an exclusion if needed.

## Credits, Acknowledgments, & Disclaimers
The Wave Link WebSocket protocol handling in v1 was based on [node-wave-link-sdk](https://github.com/DarrellVS/node-wave-link-sdk) by [@darrellvs](https://github.com/darrellvs) - v2's native client is a from-scratch implementation of the same protocol, and that library's existence made both versions dramatically easier to build.

This utility is not affiliated with, endorsed by, or sponsored by Elgato or Corsair. Elgato and Wave Link are trademarks of their respective owners. I just don't like how they lock down their ecosystem. (But opening Wave Link software for any mic is a HUGE step in the right direction! Thanks Elgato!)

This utility is built with minimal AI input, generally limited to auto-completion, commit descriptions, and automated debugging processes. No "Make No Mistakes" here, there will be plenty of them.
