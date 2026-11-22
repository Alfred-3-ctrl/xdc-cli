const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('xdc', {
  invoke(method, payload) {
    return ipcRenderer.invoke('xdc:invoke', method, payload);
  },
  onEvent(handler) {
    if (typeof handler !== 'function') return () => {};
    const callback = (_, event) => handler(event);
    ipcRenderer.on('xdc:event', callback);
    return () => ipcRenderer.removeListener('xdc:event', callback);
  },
});
