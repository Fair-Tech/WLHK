# Wave Link Hotkey Manager

Wave Link Hotkey Manager is a lightweight background utility that provides system-wide custom hotkey support for the Elgato Wave Link software. It allows you to configure advanced, object-focused hotkeys for volume control, muting channels, and cycling output devices natively on Windows, all without a Stream Deck.

Built by FairTech.

## Features
- **Object-Focused Customization:** Create standard tap, hold, and double-press actions per-key.
- **System-Wide Detection:** Hooks natively into your OS to capture keystrokes, completely bypassing Elgato's limitations.
- **Smart OS Suppression:** Blocks the default OS behavior when triggering hotkeys (e.g. mapping your keyboard's native Volume Mute button strictly to Wave Link without actually muting your whole system).
- **On-Screen Display (OSD):** Beautiful, modern overlay that shows the action taken with fully customizable anchor positioning across 9 screen positions.
- **Global Settings:** Instantly disable/enable hotkeys or the OSD without closing the app.
- **Auto-Reconnect:** Automatically reconnects to Wave Link on startup, after sleep/wake cycles, or if the connection drops.
- **Start with Windows:** Optional Windows startup entry, visible in Task Manager's Startup tab.
- **Admin Elevation:** Optional auto-elevation on start to ensure hotkeys work in admin-elevated apps like Task Manager.

## Requirements
- Windows 10/11
- Elgato Wave Link 3.1+
- Node.js (for local development only)

## Installation & Usage

### Running without Building
1. Clone the repository.
2. Run `npm install`
3. Run `npm start` to launch the background daemon.

### Building
Double-click either build script from File Explorer — they will automatically request Administrator privileges and install dependencies before building.

- **`Build-Installer.bat`** — Generates a standard Windows installer (`.exe` setup wizard)
- **`Build-Portable.bat`** — Generates a standalone portable `.exe` (no installation required)

The output will be placed in the `/dist` directory.

> **Note on Antivirus:** The keyboard hook library (`node-global-key-listener`) may trigger a false positive from Windows Defender or other AV software due to its low-level input capture. The binary is open-source and available for inspection. You may need to add an exclusion for the application folder.

## Configuration
The app runs seamlessly in the background and is accessible via your System Tray. Double-click the tray icon to open the configuration dashboard, or right-click for quick options to disable/enable hotkeys and the OSD globally, or reconnect to Wave Link.

**Running as Administrator** is recommended if you use hotkeys while focused on admin-elevated applications (e.g. Task Manager, certain games). The app can be configured to auto-elevate on launch from the Global Settings panel.

## Credits, Acknowledgments, & Disclaimers
Credits to the creator of the [node-wave-link-sdk](https://github.com/DarrellVS/node-wave-link-sdk), ([@darrellvs](https://github.com/darrellvs))
This tool would have been a lot more effort to make if not for this library!

This utility is not affiliated with, endorsed by, or sponsored by Elgato or Corsair. Elgato and Wave Link are trademarks of their respective owners. I just don't like how they lock down their ecosystem. (But opening Wave Link software for any mic is a HUGE step in the right direction! Thanks Elgato!)
