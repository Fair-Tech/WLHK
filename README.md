# Wave Link Hotkey Manager (WLHK) 2.5

Wave Link Hotkey Manager is a lightweight background utility that provides system-wide custom hotkey support for Elgato Wave Link features. Configure tap, hold, and double-press actions per key for muting channels, adjusting volume, and switching output devices - natively on Windows, without a Stream Deck.

Built by FairTech.

**v2.5 is a ground-up rewrite in C# / .NET Native AOT.** The Electron runtime is gone:

|  | v1 (Electron) | v2 (Native AOT) |
|---|---|---|
| Idle RAM (private) | ~90 MB | **~13 MB** |
| Processes | 4+ (main, GPU, 2 renderers, key server) | **1** |
| Portable exe | self-extracts to `%TEMP%` every launch | **true single file, zero extraction** |
| Distribution | ~80 MB installer | **~18 MB exe, no runtime install** |
| Keystroke handling | out-of-process helper over stdio | in-process low-level hook |

## Features
- **Per-key trigger customization:** tap, hold (configurable duration), and double-press actions on any key or combo.
- **Left/right modifiers:** optionally bind `LSHIFT+H` and `RSHIFT+H` to different actions; hotkeys recorded without a side keep matching either.
- **System-wide detection:** low-level OS keyboard hook - works regardless of app focus, bypassing Elgato's software limitations.
- **Smart OS suppression:** mapped keys are swallowed, so binding your keyboard's Volume Mute to a Wave Link channel doesn't also mute Windows.
- **Actions:** toggle mute, volume up/down (configurable step), set absolute volume, switch output device, cycle output devices. Channel actions can target **All Mixes** or one named mix.
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

For channel actions, choose **All Mixes** to affect the channel everywhere, or select a named mix to affect only that mix. For example, configure Toggle Mute for **Microphone** in the **VC** mix to mute it only in VC while leaving its other mixes unchanged.

**Running as Administrator** is recommended if you use hotkeys while focused on elevated apps (Task Manager, some games). Enable "Auto-Elevate to Admin on Start" in Global Settings. The `--no-elevate` command-line flag skips auto-elevation for a single launch.

> **Note on Antivirus:** the global keyboard hook (`SetWindowsHookEx`) can trigger false positives in some AV products. The source is open for inspection; add an exclusion if needed.

## Credits, Acknowledgments, & Disclaimers
The Wave Link WebSocket protocol handling in v1 was based on [node-wave-link-sdk](https://github.com/DarrellVS/node-wave-link-sdk) by [@darrellvs](https://github.com/darrellvs) - v2's native client is a from-scratch implementation of the same protocol, and that library's existence made both versions dramatically easier to build.

This utility is not affiliated with, endorsed by, or sponsored by Elgato or Corsair. Elgato and Wave Link are trademarks of their respective owners. I just don't like how they lock down their ecosystem. (But opening Wave Link software for any mic is a HUGE step in the right direction! Thanks Elgato!)

This utility is built with minimal AI input, generally limited to auto-completion, commit descriptions, and automated debugging processes. No "Make No Mistakes" here, there will be plenty of them.
