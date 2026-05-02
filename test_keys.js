const fs = require('fs');
const { GlobalKeyboardListener } = require('node-global-key-listener');

const listener = new GlobalKeyboardListener();
const logStream = fs.createWriteStream('keys.log', { flags: 'a' });

console.log("Listening for keys... Press Media Mute and other keys. Press Ctrl+C to exit.");
logStream.write(`\n--- Started ${new Date().toISOString()} ---\n`);

listener.addListener((e, down) => {
    const logLine = `[${e.state}] Key: ${e.name} (Raw: ${e.rawKey._nameRaw || e.rawKey.name})\n`;
    console.log(logLine.trim());
    logStream.write(logLine);
});
