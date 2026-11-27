const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('orbita', {
  call: (action, payload) => ipcRenderer.invoke('orbita:call', action, payload),
  watch: (missionId, after) => ipcRenderer.send('orbita:watch', missionId, after),
  onEvents: (callback) => {
    const listener = (_event, packet) => callback(packet);
    ipcRenderer.on('orbita:events', listener);
    return () => ipcRenderer.removeListener('orbita:events', listener);
  },
  preview: (action, value) => ipcRenderer.invoke('orbita:preview', action, value),
});
