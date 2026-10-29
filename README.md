# Wave Link Hotkey Manager (WLHK)

Wave Link Hotkey Manager is a lightweight background utility that provides system-wide custom hotkey support for Elgato Wave Link features. Configure tap, hold, and double-press actions per key for muting channels, adjusting volume, and switching output devices - natively on Windows, without a Stream Deck.

Built by FairTech.

WLHK is a single native executable (C# / .NET Native AOT): one process, around 10 MB of RAM at idle, no runtime to install, and nothing extracted to `%TEMP%`. Keystrokes are handled in-process by a low-level keyboard hook.

## Features

- **Per-key trigger customization:** tap, hold (configurable duration), and double-press actions on any key or combo. Hold actions can repeat while the key is held, at a configurable interval.
- **Left/right modifiers:** optionally bind `LSHIFT+H` and `RSHIFT+H` to different actions; hotkeys recorded without a side keep matching either.
- **System-wide detection:** low-level OS keyboard hook - works regardless of app focus, bypassing Elgato's software limitations.
- **Smart OS suppression:** mapped keys are swallowed, so binding your keyboard's Volume Mute to a Wave Link channel doesn't also mute Windows.
- **Actions:** Mute (toggle, mute or unmute) and Volume (up, down or set a level) for a channel, a channel in one mix, a whole mix, an output device, the current output device, the focused app's channel, the focused app's own volume (as in the Windows Volume Mixer), the default Windows device, or the **Active Target**. Plus Microphone, Effect, switch/cycle output device, Move Focused App, media keys and Change Target. Each action can show or hide the OSD on its own.
- **Microphone:** hardware mute, gain up/down/set in dB, the Wave's Mic/PC monitor mix, and gain lock.
- **Effects:** turn a channel's effects (noise gate, EQ, ...) on or off.
- **Focused app:** control its Wave Link channel or its own Windows volume, move it to another channel, and pick fallbacks for when it isn't in a channel or isn't playing audio.
- **Active Target:** keep an ordered list of targets and switch which one your hotkeys control, by cycling, picking one, holding a hotkey, or holding an entry's own custom modifier keys. Optionally remembered across restarts.
- **Volume keys:** hand your keyboard's volume keys over to the Active Target without recording a hotkey for each. They fall back to Windows whenever Wave Link isn't connected.
- **Media controls:** Play/Pause, Next, Previous, Stop and system volume up/down/mute for your Windows default output device. Handled by Windows, so they work with any player and don't need Wave Link running.
- **On-Screen Display:** frameless dark overlay with 9 anchor positions, shown on the monitor your cursor is on; text and volume-bar modes; configurable duration. Actions on the focused app show its name and icon.
- **Auto-reconnect:** finds Wave Link via its `ws-info.json` port file (with a 1884–1893 scan fallback) and retries forever with backoff - at boot, after sleep/wake, or if Wave Link restarts.
- **Start with Windows**, **auto-elevate to Admin** (for hotkeys in elevated apps), single-instance, light/dark mode.
- **Portable data mode:** create a `WLHK_data` folder next to `WLHK.exe` and all settings live there instead of `%APPDATA%\WLHK`.
- **Config upgrades:** configs from older versions, including the original Electron app, are picked up and converted automatically.

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

Enable **Distinguish L/R Modifiers** in Global Settings to record new hotkeys with side-specific modifiers (`LSHIFT+H`, `RCTRL+RSHIFT+H`), so the left and right modifier keys can trigger different actions. A hotkey recorded as `SHIFT+H` still fires for either shift, and a side-specific hotkey takes precedence over the side-agnostic one when both are bound.

Each trigger picks an **Action** and, where it has one, a **Mode**: Mute can Toggle, Mute or Unmute (the last two always send that state, so other programs watching Wave Link can rely on them as on/off signals), and Volume can go Up, Down or Set Level. Untick **OSD** on any action to keep it silent.

On a **Hold** trigger, volume and gain steps, track skips and target cycling can be set to **Repeat** every so many milliseconds for as long as the key is held, so a held hotkey ramps the volume instead of needing to be tapped. With Repeat on, the interval also decides when a press becomes a hold, so **Hold Time** is greyed out. If the same key has a Normal Press or Double Press action, the first repeat waits at least 250 ms so quick taps still count as taps.

When a key has both a **Normal Press** and a **Double Press** action, a single press waits out the double-press window before running, so a double press runs only the Double Press action.

### Targets

Mute, Volume and Effect actions each have a **Target**:

- **A channel**, in all mixes or in one. For example, Toggle Mute for **Microphone** in the **VC** mix mutes it only in VC and leaves its other mixes alone.
- **A mix** or **an output device**.
- **Output: Current Device** - whichever output device is Wave Link's main output when the hotkey is pressed, so it follows you when you switch outputs.
- **Focused App's Channel** - the Wave Link channel of whatever app has focus.
- **Focused App's Volume** - that app's own volume, exactly like its slider in the Windows Volume Mixer. It works across every playback device, including each Wave Link channel, and covers apps that play from a helper process, like browsers and Discord.
- **Native Volume Controls** - the default Windows playback device, the same level the keyboard volume keys move.
- **Active Target** - whichever entry is active in your Active Target list (below).

Focused App's Volume and Native Volume Controls go through Windows, so they work without Wave Link running.

In Global Settings you can pick fallbacks for the two focused-app targets. **If the focused app isn't in a channel** picks what Focused App's Channel uses for apps Wave Link hasn't routed anywhere, for example Focused App's Volume. **If the focused app has no audio** does the same for Focused App's Volume when the app isn't playing anything Windows can adjust (or nothing is focused), for example Native Volume Controls or Output: Current Device. The two can chain: an app outside every channel goes to its own volume, and if it's silent too, on to the second fallback. Left at Nothing, the OSD just says so.

### Active Target

In **Global Settings**, build an **Active Target list** from any of the targets above, and reorder it with the arrows. Set any Mute, Volume or Effect action's Target to **Active Target** and it controls whichever entry is active. Switch the active entry with a **Change Target** action:

- **Next Target / Previous Target** step through the list.
- **Select Target** jumps straight to one entry.
- **Target While Held** aims at an entry only while the key is held, then switches back. It's offered on **Hold**, and on **Double Press**, where you double-tap and keep holding.

Each entry can also have its own **Hold** keys: a custom modifier. Click **Hold**, press the keys you want (Ctrl+Shift, a spare key like F13, or a mix like Shift+Caps Lock) and let go. Left and right modifiers are separate keys, so Left Shift and Right Shift can each aim at a different entry. While they're held, every Active Target hotkey and volume key aims at that entry. Modifier keys are stripped from the hotkey, so with *Wave Mic* on Left Shift, `LSHIFT+F13` runs your `F13` hotkey against the mic; a hotkey you've actually recorded as `SHIFT+F13` always takes priority. Ordinary keys used as Hold keys stop typing on their own. If several entries' keys are held, the one with the most keys wins, then the higher entry.

The list starts on its first entry each time WLHK launches, unless **Persistent Target** is ticked, in which case it picks up where you left it.

### Volume Keys

Rather than recording `VOLUME_UP`, `VOLUME_DOWN` and `VOLUME_MUTE` one at a time, set **Keyboard volume keys** to **Control the Active Target** in Global Settings. They ramp on the keyboard's own key repeat like the native keys do, and only take over while Wave Link is connected, so you never end up with volume keys that do nothing. A recorded hotkey for the same key keeps priority, and this only fills in triggers it leaves empty. Hold keys work here too, and a modifier that isn't anyone's Hold key falls through to Windows, so `CTRL+VOLUME_UP` still changes the Windows volume.

### Microphone, effects and apps

- **Microphone** controls the mic itself rather than a channel: hardware mute (the Wave's mute light), **gain** in whole dB within the device's range, the Wave's **Mic/PC mix** (the monitoring balance in your headphones), and **gain lock**. Mics without a feature say so on the OSD instead of guessing.
- **Effect** turns one of a channel's effects on or off. On a fixed channel you pick the effect itself; on the Active Target or the focused app's channel it's matched by name, so one hotkey can toggle *Noise Gate* on whichever channel is active.
- **Move Focused App** routes the app Wave Link sees as focused to a channel you pick.

Media actions (Play / Pause, Next, Previous, Stop, System Volume Up/Down/Mute) press the matching Windows media key, so they reach whichever app currently has media control and move the volume of your default output device. They work whether or not Wave Link is running, and they're safe to bind to the physical media keys themselves: WLHK ignores the keypresses it generates, so nothing loops.

**Running as Administrator** is recommended if you use hotkeys while focused on elevated apps (Task Manager, some games). Enable "Auto-Elevate to Admin on Start" in Global Settings. The `--no-elevate` command-line flag skips auto-elevation for a single launch.

> **Note on Antivirus:** the global keyboard hook (`SetWindowsHookEx`) can trigger false positives in some AV products. The source is open for inspection; add an exclusion if needed.

## Credits, Acknowledgments, & Disclaimers

The Wave Link WebSocket protocol handling was originally based on [node-wave-link-sdk](https://github.com/DarrellVS/node-wave-link-sdk) by [@darrellvs](https://github.com/darrellvs) - the current native client is a from-scratch implementation of the same protocol, and that library's existence made this project dramatically easier to build.

This utility is not affiliated with, endorsed by, or sponsored by Elgato or Corsair. Elgato and Wave Link are trademarks of their respective owners. I just don't like how they lock down their ecosystem. (But opening Wave Link software for any mic is a HUGE step in the right direction! Thanks Elgato!)

This utility is built with minimal AI input, generally limited to auto-completion, commit descriptions, and automated debugging processes. No "Make No Mistakes" here, there will be plenty of them. Contributions may use AI, but please mark them as such. 
