const { app, Tray, Menu, BrowserWindow, ipcMain, nativeImage, screen } = require('electron');
const path = require('path');
const fs = require('fs');
const { WaveLinkController } = require('@darrellvs/node-wave-link-sdk');
const { GlobalKeyboardListener } = require('node-global-key-listener');
const { HotkeyManager, getComboString } = require('./hotkeys');

let tray = null;
let osdWindow = null;
let configWindow = null;

// The central wave link state controller
const wlController = new WaveLinkController();

// Hotkey Manager reference
// Hotkey Manager reference
let hotkeyManager = null;

// Config
const configPath = path.join(app.getPath('userData'), 'config.json');
let config = { hotkeys: {} };

const gotTheLock = app.requestSingleInstanceLock();
if (!gotTheLock) {
  app.quit();
  process.exit(0);
} else {
  app.on('second-instance', (event, commandLine, workingDirectory) => {
    // Someone tried to run a second instance, we should focus our window.
    if (configWindow) {
      if (configWindow.isMinimized()) configWindow.restore();
      configWindow.focus();
    }
  });
}

function loadConfig() {
  try {
    if (fs.existsSync(configPath)) {
      config = JSON.parse(fs.readFileSync(configPath, 'utf-8'));
    } else {
      // Default dummy config for testing
      config = {
        hotkeys: {
          "F13": {
            normalAction: { type: "mute_channel", channelId: "some-id" }
          }
        }
      };
      saveConfig();
    }
  } catch (e) {
    console.error("Failed to load config", e);
  }

  if (config.hotkeysEnabled === undefined) config.hotkeysEnabled = true;
  if (config.osdEnabled === undefined) config.osdEnabled = true;
}

function saveConfig() {
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2));
}

app.on('ready', async () => {
  loadConfig();
  // Hide the dock icon on macOS (no-op on Windows, but good practice)
  if (app.dock) app.dock.hide();

  // Create Tray
  // We need an icon. For now, use a native placeholder or blank icon.
  // NativeImage.createEmpty() could be used but we'll try to load a blank or default icon later.
  const iconPath = path.join(__dirname, '../assets/WLHK.ico');
  // We'll create a dummy icon or tell the user to put one.

  try {
    tray = new Tray(iconPath);
  } catch (e) {
    // Fallback if icon missing
    tray = new Tray(require('electron').nativeImage.createEmpty());
  }

  updateContextMenu();

  tray.on('double-click', openConfigWindow);

  // Initialize WaveLink Controller
  setupWaveLink();

  // Initialize OSD Window
  setupOSDWindow();

  // Initialize Hotkeys
  setupHotkeys();
});

function updateContextMenu() {
  const contextMenu = Menu.buildFromTemplate([
    { label: 'Configure Hotkeys', click: openConfigWindow },
    { type: 'separator' },
    {
      label: config.hotkeysEnabled ? 'Disable Hotkeys' : 'Enable Hotkeys', click: () => {
        config.hotkeysEnabled = !config.hotkeysEnabled;
        saveConfig();
        if (hotkeyManager) hotkeyManager.enabled = config.hotkeysEnabled;
        updateContextMenu();
        if (configWindow) configWindow.webContents.send('config-data', config);
      }
    },
    {
      label: config.osdEnabled ? 'Disable OSD' : 'Enable OSD', click: () => {
        config.osdEnabled = !config.osdEnabled;
        saveConfig();
        updateContextMenu();
        if (configWindow) configWindow.webContents.send('config-data', config);
      }
    },
    { type: 'separator' },
    { label: 'Quit', click: () => app.quit() }
  ]);
  tray.setToolTip('Wave Link Hotkey Manager');
  tray.setContextMenu(contextMenu);
}

function setupWaveLink() {
  wlController.on('ready', () => {
    console.log('Connected to Wave Link 3.1+');
    broadcastWaveData();
  });

  wlController.on('disconnected', () => {
    console.log('Disconnected from Wave Link');
  });

  wlController.on('channelsChanged', broadcastWaveData);
  wlController.on('outputDevicesChanged', broadcastWaveData);

  wlController.connect();
}

function broadcastWaveData() {
  if (configWindow) {
    configWindow.webContents.send('wave-data', {
      channels: wlController.getChannels(),
      outputDevices: wlController.getOutputDevices()
    });
  }
}

function setupOSDWindow() {
  osdWindow = new BrowserWindow({
    width: 300,
    height: 100,
    frame: false,
    transparent: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    show: false, // suspended until needed
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: false
    }
  });

  osdWindow.loadFile(path.join(__dirname, 'ui/osd/index.html'));
}

function positionOSD() {
  if (!osdWindow) return;
  const currentDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
  const workArea = currentDisplay.workArea;

  const osdPos = config.osdPosition || "bottom-right";
  const bounds = osdWindow.getBounds();
  const padding = 20;

  let x = workArea.x;
  let y = workArea.y;

  if (osdPos.includes('right')) x += workArea.width - bounds.width - padding;
  else if (osdPos.includes('left')) x += padding;
  else x += Math.round((workArea.width - bounds.width) / 2); // center

  if (osdPos.includes('bottom')) y += workArea.height - bounds.height - padding;
  else if (osdPos.includes('top')) y += padding;
  else y += Math.round((workArea.height - bounds.height) / 2); // center

  osdWindow.setBounds({ x, y, width: bounds.width, height: bounds.height });
}

function showOSD(title, value, type = 'text') {
  if (!config.osdEnabled) return;
  if (!osdWindow) return;
  positionOSD();
  osdWindow.webContents.send('show-osd', { title, value, type });
  osdWindow.showInactive();

  // Auto-hide after 2s
  if (osdWindow.hideTimeout) clearTimeout(osdWindow.hideTimeout);
  osdWindow.hideTimeout = setTimeout(() => {
    osdWindow.webContents.send('hide-osd');
    setTimeout(() => {
      if (osdWindow) osdWindow.hide();
    }, 250); // wait for fade out animation
  }, 2000);
}

function openConfigWindow() {
  if (configWindow) {
    configWindow.focus();
    return;
  }
  configWindow = new BrowserWindow({
    width: 800,
    height: 600,
    title: "Wave Link Hotkey Manager",
    icon: nativeImage.createFromPath(path.join(__dirname, '../assets/WLHK.ico')),
    autoHideMenuBar: true,
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: false
    }
  });
  configWindow.loadFile(path.join(__dirname, 'ui/config/index.html'));

  configWindow.on('closed', () => {
    configWindow = null;
  });
}

// IPC Handlers
ipcMain.on('request-data', (event) => {
  event.reply('config-data', config);
  event.reply('wave-data', {
    channels: wlController.getChannels(),
    outputDevices: wlController.getOutputDevices()
  });
});

ipcMain.on('save-config', (event, newConfig) => {
  config = newConfig;
  saveConfig();
  event.reply('config-data', config);
  updateContextMenu();
  // Re-init hotkey manager with new config
  setupHotkeys();
});

let isRecording = false;
let recordingListener = null;

ipcMain.on('start-recording', (event) => {
  if (isRecording) return;
  isRecording = true;

  recordingListener = new GlobalKeyboardListener();
  recordingListener.addListener((e, down) => {
    if (e.state === 'DOWN' || e.state === 'UP') {
      const combo = getComboString(e, down);
      if (combo) { // it's not just a standalone modifier
        event.reply('recording-result', combo);
        isRecording = false;
        recordingListener.kill();
        recordingListener = null;
        return true; // Block the action while recording
      }
    }
    return false;
  });
});

function setupHotkeys() {
  if (hotkeyManager) {
    // If re-initializing, kill the old listener to avoid double triggers
    hotkeyManager.listener.kill();
  }
  hotkeyManager = new HotkeyManager(wlController, config, showOSD);
  hotkeyManager.enabled = config.hotkeysEnabled;
}

// Ensure app stays open even if all windows closed
app.on('window-all-closed', (e) => {
  e.preventDefault();
});
