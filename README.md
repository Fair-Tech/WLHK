# Wave Link Hotkey Manager

Wave Link Hotkey Manager is a lightweight background utility that provides system-wide custom hotkey support for the Elgato Wave Link software. It allows you to configure advanced, object-focused hotkeys for volume control, muting channels, and cycling output devices natively on Windows, all without a Stream Deck. 

Built by FairTech.

## Features
- **Object-Focused Customization:** Create standard tap, hold, and double-press actions per-key.
- **System-Wide Detection:** Hooks natively into your OS to capture keystrokes, completely bypassing Elgato's limitations.
- **Smart OS Suppression:** Blocks the default OS behavior when triggering hotkeys (e.g. mapping your keyboard's native Volume Mute button strictly to Wave Link without actually muting your whole system).
- **On-Screen Display (OSD):** Beautiful, modern overlay that shows the action taken with fully customizable anchor positioning.
- **Global Settings:** Instantly disable/enable hotkeys or the OSD without closing the app.

## Requirements
- Windows 10/11
- Elgato Wave Link 3.1+ 
- Node.js (for local development)

## Installation & Usage

### Running w/out Building
1. Clone the repository.
2. Run `npm install`
3. Run `npm start` to launch the background daemon. 

### Building
1. Ensure all dependencies are installed.
2. Run `npm run dist` to compile into a standalone `.exe` installer.
3. The generated installer will be located in the `/dist` directory.

## Configuration
The app runs seamlessly in the background and is accessible via your System Tray. Double click the Wave Link Hotkey Manager icon in your tray to open the configuration dashboard, or right-click for quick options for disabling the OSD and Hotkeys globally.

## Credits, Acknowledgments, & Disclaimers
Credits to the creator of the **`node-wave-link-sdk`** ([@darrellvs](https://github.com/darrellvs))
This tool would have been a lot more effort to make if not for this library!

This utility is not affiliated with, endorsed by, or sponsored by Elgato or Corsair. Elgato and Wave Link are trademarks of their respective owners. I just don't like how they lock down their ecosystem. (But opening Wave Link software for any mic is a HUGE step in the right direction! Thanks Elgato!)
