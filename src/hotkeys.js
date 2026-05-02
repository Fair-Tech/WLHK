const { GlobalKeyboardListener } = require('node-global-key-listener');

function getComboString(e, down) {
    const modifiers = [];
    if (down["LEFT CTRL"] || down["RIGHT CTRL"]) modifiers.push("CTRL");
    if (down["LEFT ALT"] || down["RIGHT ALT"]) modifiers.push("ALT");
    if (down["LEFT SHIFT"] || down["RIGHT SHIFT"]) modifiers.push("SHIFT");
    if (down["LEFT META"] || down["RIGHT META"]) modifiers.push("WIN");

    // Some keys (like Media Mute) have an empty standardName, so e.name is "".
    // Fallback to the raw platform key name (e.g. "VOLUME_MUTE").
    let baseName = e.name;
    if (!baseName && e.rawKey && e.rawKey.name) {
        baseName = e.rawKey.name;
    }
    if (!baseName) return null;

    // Check if the key pressed is itself a modifier
    const isModifier = ["LEFT CTRL", "RIGHT CTRL", "LEFT ALT", "RIGHT ALT", "LEFT SHIFT", "RIGHT SHIFT", "LEFT META", "RIGHT META", "CTRL", "ALT", "SHIFT", "WIN"].includes(baseName);

    if (isModifier) return null; // We don't trigger actions on pure modifier presses

    if (modifiers.length > 0) {
        return modifiers.join("+") + "+" + baseName;
    }
    return baseName;
}

class HotkeyManager {
    constructor(wlController, config, showOSD) {
        this.wlController = wlController;
        this.config = config;
        this.showOSD = showOSD;
        this.listener = new GlobalKeyboardListener();

        // Track state for each key
        this.keyStates = {};

        this.HOLD_THRESHOLD_MS = 500;
        this.DOUBLE_PRESS_THRESHOLD_MS = 300;

        this.listener.addListener((e, down) => {
            const keyString = getComboString(e, down);
            if (!keyString) return false; // ignore pure modifiers

            if (e.state === "DOWN") {
                this.handleKeyDown(keyString);
            } else if (e.state === "UP") {
                this.handleKeyUp(keyString);
            }

            // If this key is mapped in our config, return true to block default OS behavior
            if (this.config.hotkeys[keyString]) {
                return true;
            }
            return false;
        });
    }

    handleKeyDown(key) {
        const now = Date.now();
        if (!this.keyStates[key]) {
            this.keyStates[key] = {
                downTime: 0,
                lastUpTime: 0,
                holdTimer: null,
                isHeld: false
            };
        }

        const state = this.keyStates[key];

        // Ignore auto-repeat
        if (state.downTime > state.lastUpTime) return;

        state.downTime = now;
        state.isHeld = false;

        const actionConfig = this.config.hotkeys[key];
        if (!actionConfig) return;

        // Check double press
        if (actionConfig.doublePressAction && now - state.lastUpTime < this.DOUBLE_PRESS_THRESHOLD_MS) {
            if (state.holdTimer) clearTimeout(state.holdTimer);
            this.executeAction(actionConfig.doublePressAction);
            // Reset to prevent triple-press triggering double again
            state.lastUpTime = 0;
            return;
        }

        // Setup hold timer
        if (actionConfig.holdAction) {
            const holdDuration = actionConfig.holdAction.duration || this.HOLD_THRESHOLD_MS;
            state.holdTimer = setTimeout(() => {
                state.isHeld = true;
                this.executeAction(actionConfig.holdAction);
            }, holdDuration);
        } else if (actionConfig.normalAction && !actionConfig.doublePressAction) {
            // If no hold and no double press, trigger immediately on down
            this.executeAction(actionConfig.normalAction);
        }
    }

    handleKeyUp(key) {
        let state = this.keyStates[key];

        // Some media keys might only fire an UP event. 
        // If we didn't track a DOWN event for this key, simulate one instantly.
        if (!state) {
            this.handleKeyDown(key);
            state = this.keyStates[key];
        }

        const now = Date.now();
        state.lastUpTime = now;

        const actionConfig = this.config.hotkeys[key];
        if (!actionConfig) return;

        if (state.holdTimer) {
            clearTimeout(state.holdTimer);
            state.holdTimer = null;
        }

        // If it wasn't held, and it has a normal action, and it has a double press action or hold action 
        // (meaning we delayed the normal action until key up)
        if (!state.isHeld && actionConfig.normalAction && (actionConfig.doublePressAction || actionConfig.holdAction)) {
            // Wait a tiny bit to see if they double press, before executing normal action
            if (actionConfig.doublePressAction) {
                setTimeout(() => {
                    // If a double press happened, lastUpTime would have been reset to 0
                    if (this.keyStates[key].lastUpTime === now) {
                        this.executeAction(actionConfig.normalAction);
                    }
                }, this.DOUBLE_PRESS_THRESHOLD_MS);
            } else {
                this.executeAction(actionConfig.normalAction);
            }
        }
    }

    executeAction(action) {
        if (!this.wlController.isConnected()) {
            this.showOSD("Wave Link Disconnected", "Failed", "text");
            return;
        }

        switch (action.type) {
            case "mute_channel":
                this.muteChannel(action);
                break;
            case "volume_up_channel":
                this.adjustVolume(action, 5);
                break;
            case "volume_down_channel":
                this.adjustVolume(action, -5);
                break;
            case "switch_output":
                this.switchOutput(action);
                break;
            case "cycle_output":
                this.cycleOutput(action);
                break;
        }
    }

    muteChannel(action) {
        // action.channelId
        const channel = this.wlController.getChannelById(action.channelId);
        if (channel) {
            const newMuteState = !channel.isMuted;
            this.wlController.setChannel({ id: channel.id, isMuted: newMuteState });
            this.showOSD(`Channel: ${channel.name}`, newMuteState ? "Muted" : "Unmuted", "text");
        } else {
            // Might be an input device id? We only support channels for now
            const device = this.wlController.getInputDeviceById(action.channelId);
            if (device && device.inputs.length > 0) {
                const newMuteState = !device.inputs[0].isMuted;
                this.wlController.setInputDevice({
                    id: device.id,
                    inputs: [{ id: device.inputs[0].id, isMuted: newMuteState }]
                });
                this.showOSD(`Input: ${device.name}`, newMuteState ? "Muted" : "Unmuted", "text");
            }
        }
    }

    adjustVolume(action, delta) {
        const channel = this.wlController.getChannelById(action.channelId);
        if (channel) {
            let newVolume = Math.round(channel.level * 100) + delta;
            newVolume = Math.max(0, Math.min(100, newVolume));
            this.wlController.setChannel({ id: channel.id, level: newVolume / 100 });
            this.showOSD(`🔊: ${channel.name}`, newVolume, "slider");
        }
    }

    switchOutput(action) {
        const deviceId = action.deviceId;
        const target = this.wlController.getOutputDeviceById(deviceId);
        if (target) {
            this.wlController.setOutputDevice({
                mainOutput: { outputDeviceId: target.id }
            });
            this.showOSD(`Output Device:`, target.name, "text");
        }
    }

    cycleOutput(action) {
        // action.deviceIds is an array of IDs to cycle through
        const currentMain = this.wlController.getMainOutput();
        const currentIndex = action.deviceIds.indexOf(currentMain.outputDeviceId);
        let nextIndex = currentIndex + 1;
        if (nextIndex >= action.deviceIds.length) nextIndex = 0;

        const nextDeviceId = action.deviceIds[nextIndex];
        const target = this.wlController.getOutputDeviceById(nextDeviceId);
        if (target) {
            this.wlController.setOutputDevice({
                mainOutput: { outputDeviceId: target.id }
            });
            this.showOSD(`Output Device:`, target.name, "text");
        }
    }
}

module.exports = { HotkeyManager, getComboString };
