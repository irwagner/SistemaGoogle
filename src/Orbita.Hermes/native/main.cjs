const { app, BrowserWindow, WebContentsView, ipcMain, net, protocol, session } = require('electron');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { OrbitaApi } = require('./orbita-api.cjs');

const api = new OrbitaApi(Number(process.env.ORBITA_PORT || 7784));
const renderer = path.join(__dirname, 'renderer');
const trustedPage = 'orbita://app/orbita.html';
protocol.registerSchemesAsPrivileged([{ scheme: 'orbita', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);
let window;
let preview;
let watcher;

function localUrl(value) {
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol) || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.username || url.password)
    throw new Error('Nesta prévia, use uma página local do projeto, como http://127.0.0.1:3000.');
  return url.href;
}

function trusted(event) {
  if (event.sender !== window?.webContents || event.senderFrame?.url.split('#')[0] !== trustedPage)
    throw new Error('Janela do Orbita inválida.');
}

function showPreview() {
  if (preview) return preview;
  const browserSession = session.fromPartition('orbita-project-preview');
  browserSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  browserSession.setPermissionCheckHandler(() => false);
  browserSession.on('will-download', (event) => event.preventDefault());
  preview = new WebContentsView({ webPreferences: { session: browserSession, sandbox: true, contextIsolation: true, nodeIntegration: false, devTools: false } });
  preview.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  for (const name of ['will-navigate', 'will-redirect']) preview.webContents.on(name, (event, url) => {
    try { localUrl(url); } catch { event.preventDefault(); }
  });
  window.contentView.addChildView(preview);
  return preview;
}

ipcMain.handle('orbita:call', (event, action, payload) => {
  trusted(event);
  return api.call(action, payload);
});

ipcMain.on('orbita:watch', (event, id, after) => {
  try { trusted(event); } catch { return; }
  watcher?.abort();
  if (!id) return;
  const controller = new AbortController();
  watcher = controller;
  const publish = (packet) => {
    if (!controller.signal.aborted && !event.sender.isDestroyed()) event.sender.send('orbita:events', { missionId: id, ...packet });
  };
  void api.watch(id, after, controller.signal, (items) => publish({ items }))
    .then(() => publish({ disconnected: true }), (error) => publish({ disconnected: true, error: String(error.message || error) }));
});

ipcMain.handle('orbita:preview', async (event, action, value) => {
  trusted(event);
  if (action === 'hide') { preview?.setVisible(false); return; }
  if (action === 'bounds' && preview) {
    const [width, height] = window.getContentSize();
    const x = Math.max(0, Math.min(width, Math.round(value.x)));
    const y = Math.max(0, Math.min(height, Math.round(value.y)));
    preview.setBounds({ x, y, width: Math.max(0, Math.min(width - x, Math.round(value.width))), height: Math.max(0, Math.min(height - y, Math.round(value.height))) });
    preview.setVisible(true);
    return;
  }
  if (action === 'open') {
    const url = localUrl(value);
    await showPreview().webContents.loadURL(url);
    return preview?.webContents.getTitle();
  }
  if (!preview) return;
  if (action === 'back' && preview.webContents.navigationHistory.canGoBack()) preview.webContents.navigationHistory.goBack();
  if (action === 'reload') preview.webContents.reload();
});

app.setName('Orbita Studio Preview');
app.setPath('userData', process.env.ORBITA_PREVIEW_TEST_PROFILE || path.join(app.getPath('appData'), 'OrbitaStudioHermesPreview'));
app.whenReady().then(() => {
  protocol.handle('orbita', (request) => {
    const url = new URL(request.url);
    const relative = decodeURIComponent(url.pathname).replace(/^\/+/, '');
    const target = path.resolve(renderer, relative);
    if (url.hostname !== 'app' || !target.startsWith(renderer + path.sep)) return new Response(null, { status: 404 });
    return net.fetch(pathToFileURL(target).href);
  });
  window = new BrowserWindow({ width: 1350, height: 860, minWidth: 700, minHeight: 480, show: false,
    title: 'Orbita Studio', backgroundColor: '#101522', autoHideMenuBar: true,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, sandbox: true, nodeIntegration: false } });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => { if (url.split('#')[0] !== trustedPage) event.preventDefault(); });
  window.once('ready-to-show', () => window.show());
  window.on('closed', () => {
    watcher?.abort();
    if (preview && !preview.webContents.isDestroyed()) preview.webContents.close();
    preview = undefined;
    window = undefined;
  });
  void window.loadURL(trustedPage);
});
app.on('window-all-closed', () => app.quit());
