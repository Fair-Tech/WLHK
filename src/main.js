const { app, Tray, Menu, BrowserWindow, ipcMain, nativeImage, screen, powerMonitor, dialog } = require('electron');
const path = require('path');
const fs = require('fs');
const { exec } = require('child_process');
const { WaveLinkController } = require('@darrellvs/node-wave-link-sdk');
const { GlobalKeyboardListener } = require('node-global-key-listener');
const { HotkeyManager, getComboString } = require('./hotkeys');

let tray = null;
let osdWindow = null;
let configWindow = null;

// The central wave link state controller
let wlController = null;

// Hotkey Manager reference
let hotkeyManager = null;

// Connection state
let wlConnected = false;
let wlConnecting = false;
let wlRetryCount = 0;
const WL_MAX_RETRIES = 5;
const WL_RETRY_DELAY_MS = 3000;

// Config
const configPath = path.join(app.getPath('userData'), 'config.json');
let config = { hotkeys: {} };

const gotTheLock = app.requestSingleInstanceLock();
if (!gotTheLock) {
  app.quit();
  process.exit(0);
} else {
  app.on('second-instance', (event, commandLine, workingDirectory) => {
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
      config = { hotkeys: {} };
      saveConfig();
    }
  } catch (e) {
    console.error("Failed to load config", e);
  }

  if (config.hotkeysEnabled === undefined) config.hotkeysEnabled = true;
  if (config.osdEnabled === undefined) config.osdEnabled = true;
  if (config.autoElevate === undefined) config.autoElevate = false;
  if (config.startWithWindows === undefined) config.startWithWindows = false;
}

function saveConfig() {
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2));
}

function applyLoginItemSettings() {
  // app.setLoginItemSettings writes to HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  // which is what Task Manager's Startup tab reads.
  // PORTABLE_EXECUTABLE_FILE is set by electron-builder when running a portable exe,
  // preventing the startup entry from pointing to the extracted temp folder.
  const exePath = process.env.PORTABLE_EXECUTABLE_FILE || process.execPath;
  app.setLoginItemSettings({
    openAtLogin: config.startWithWindows === true,
    path: exePath,
    name: 'Wave Link Hotkey Manager'
  });
}

// ─── Admin Elevation ──────────────────────────────────────────────────────────

function isRunningAsAdmin() {
  try {
    // On Windows, try to open a privileged resource. If it fails, we're not admin.
    require('child_process').execSync('net session', { stdio: 'ignore' });
    return true;
  } catch (e) {
    return false;
  }
}

function relaunchAsAdmin() {
  const exePath = process.execPath;
  const args = process.argv.slice(1).join(' ');
  exec(`powershell -Command "Start-Process '${exePath}' -ArgumentList '${args}' -Verb RunAs"`, (err) => {
    if (!err) app.quit();
  });
}

// ─── Wave Link Connection ─────────────────────────────────────────────────────

function createWLController() {
  if (wlController) {
    try { wlController.removeAllListeners(); } catch (_) {}
  }
  wlController = new WaveLinkController();

  wlController.on('ready', () => {
    console.log('Connected to Wave Link.');
    wlConnected = true;
    wlConnecting = false;
    wlRetryCount = 0;
    broadcastConnectionStatus();
    broadcastWaveData();
    updateContextMenu();
    // Re-attach hotkey manager with fresh controller
    setupHotkeys();
  });

  wlController.on('disconnected', () => {
    console.log('Disconnected from Wave Link.');
    wlConnected = false;
    wlConnecting = false;
    broadcastConnectionStatus();
    updateContextMenu();
    // Schedule a silent reconnect attempt
    scheduleReconnect(WL_RETRY_DELAY_MS);
  });

  wlController.on('channelsChanged', broadcastWaveData);
  wlController.on('outputDevicesChanged', broadcastWaveData);
}

function connectWaveLink() {
  if (wlConnecting || wlConnected) return;
  wlConnecting = true;
  console.log(`Wave Link connect attempt ${wlRetryCount + 1}/${WL_MAX_RETRIES}...`);
  createWLController();
  wlController.connect();
}

function scheduleReconnect(delayMs) {
  if (wlConnected || wlConnecting) return;
  setTimeout(() => {
    if (wlConnected || wlConnecting) return;
    wlRetryCount++;
    if (wlRetryCount <= WL_MAX_RETRIES) {
      connectWaveLink();
    } else {
      // Give up — notify user
      console.warn('Wave Link: Max retries reached. Showing notification.');
      if (tray) {
        tray.displayBalloon({
          iconType: 'warning',
          title: 'Wave Link Hotkey Manager',
          content: 'Could not connect to Wave Link. Make sure Wave Link is running, then click "Reconnect to Wave Link" in the tray menu.'
        });
      }
      broadcastConnectionStatus();
      updateContextMenu();
    }
  }, delayMs);
}

function manualReconnect() {
  wlConnected = false;
  wlConnecting = false;
  wlRetryCount = 0;
  connectWaveLink();
  broadcastConnectionStatus();
  updateContextMenu();
}

function broadcastConnectionStatus() {
  if (configWindow) {
    configWindow.webContents.send('connection-status', { connected: wlConnected });
  }
}

function broadcastWaveData() {
  if (configWindow) {
    configWindow.webContents.send('wave-data', {
      channels: wlController ? wlController.getChannels() : [],
      outputDevices: wlController ? wlController.getOutputDevices() : []
    });
  }
}

// ─── App Ready ────────────────────────────────────────────────────────────────

app.on('ready', async () => {
  loadConfig();
  applyLoginItemSettings();

  // Auto-elevation check (Windows only)
  if (process.platform === 'win32' && config.autoElevate && !isRunningAsAdmin()) {
    relaunchAsAdmin();
    return;
  }

  if (app.dock) app.dock.hide();

  const iconPath = path.join(__dirname, '../assets/WLHK.ico');
  try {
    tray = new Tray(iconPath);
  } catch (e) {
    tray = new Tray(nativeImage.createEmpty());
  }

  updateContextMenu();
  tray.on('double-click', openConfigWindow);

  // Start connecting to Wave Link with retry logic
  connectWaveLink();
  // If not connected in first 3s, retry in background
  scheduleReconnect(WL_RETRY_DELAY_MS);

  setupOSDWindow();
  setupHotkeys();

  // Reconnect after system wake from sleep
  powerMonitor.on('resume', () => {
    console.log('System resumed from sleep. Reconnecting to Wave Link...');
    wlRetryCount = 0;
    wlConnected = false;
    wlConnecting = false;
    connectWaveLink();
  });
});

// ─── Tray Context Menu ────────────────────────────────────────────────────────

function updateContextMenu() {
  const statusLabel = wlConnected ? '✓ Wave Link Connected' : '✗ Wave Link Disconnected';
  const contextMenu = Menu.buildFromTemplate([
    { label: statusLabel, enabled: false },
    { label: 'Reconnect to Wave Link', click: manualReconnect },
    { type: 'separator' },
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

// ─── OSD ──────────────────────────────────────────────────────────────────────

function setupOSDWindow() {
  osdWindow = new BrowserWindow({
    width: 300,
    height: 100,
    frame: false,
    transparent: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    show: false,
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
  else x += Math.round((workArea.width - bounds.width) / 2);

  if (osdPos.includes('bottom')) y += workArea.height - bounds.height - padding;
  else if (osdPos.includes('top')) y += padding;
  else y += Math.round((workArea.height - bounds.height) / 2);

  osdWindow.setBounds({ x, y, width: bounds.width, height: bounds.height });
}

function showOSD(title, value, type = 'text') {
  if (!config.osdEnabled) return;
  if (!osdWindow) return;
  positionOSD();
  osdWindow.webContents.send('show-osd', { title, value, type });
  osdWindow.showInactive();

  if (osdWindow.hideTimeout) clearTimeout(osdWindow.hideTimeout);
  osdWindow.hideTimeout = setTimeout(() => {
    osdWindow.webContents.send('hide-osd');
    setTimeout(() => {
      if (osdWindow) osdWindow.hide();
    }, 250);
  }, 2000);
}

// ─── Config Window ────────────────────────────────────────────────────────────

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

// ─── IPC Handlers ─────────────────────────────────────────────────────────────

ipcMain.on('request-data', (event) => {
  event.reply('config-data', config);
  event.reply('wave-data', {
    channels: wlController ? wlController.getChannels() : [],
    outputDevices: wlController ? wlController.getOutputDevices() : []
  });
  event.reply('connection-status', { connected: wlConnected });
  event.reply('admin-status', { isAdmin: isRunningAsAdmin() });
});

ipcMain.on('save-config', (event, newConfig) => {
  config = newConfig;
  saveConfig();
  applyLoginItemSettings();
  event.reply('config-data', config);
  updateContextMenu();
  setupHotkeys();
});

ipcMain.on('manual-reconnect', () => {
  manualReconnect();
});

ipcMain.on('relaunch-as-admin', () => {
  relaunchAsAdmin();
});

// ─── Hotkey Recording ─────────────────────────────────────────────────────────

let isRecording = false;
let recordingListener = null;

ipcMain.on('start-recording', (event) => {
  if (isRecording) return;
  isRecording = true;

  recordingListener = new GlobalKeyboardListener();
  recordingListener.addListener((e, down) => {
    if (e.state === 'DOWN' || e.state === 'UP') {
      const combo = getComboString(e, down);
      if (combo) {
        event.reply('recording-result', combo);
        isRecording = false;
        recordingListener.kill();
        recordingListener = null;
        return true;
      }
    }
    return false;
  });
});

// ─── Hotkeys ──────────────────────────────────────────────────────────────────

function setupHotkeys() {
  if (hotkeyManager) {
    try { hotkeyManager.listener.kill(); } catch (_) {}
    hotkeyManager = null;
  }
  // Short delay to allow the listener server to fully exit before spawning a new one.
  // This fixes the "unresponsive dropdown after delete+add" bug.
  setTimeout(() => {
    if (!wlController) return;
    hotkeyManager = new HotkeyManager(wlController, config, showOSD);
    hotkeyManager.enabled = config.hotkeysEnabled;
  }, 200);
}

// ─── Lifecycle ────────────────────────────────────────────────────────────────

app.on('window-all-closed', (e) => {
  e.preventDefault();
});
