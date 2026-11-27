const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { realpathSync } = require('node:fs');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const os = require('node:os');
const { setTimeout: delay } = require('node:timers/promises');
const { OrbitaApi } = require('../src/Orbita.Hermes/native/orbita-api.cjs');

const repo = path.resolve(__dirname, '..');
const [previewPath, backendPath, fixturePath, screenshotPath] = process.argv.slice(2).map((item) => path.resolve(item));
const playwrightRoot = realpathSync(path.join(repo, '.orbita-hermes', 'upstream', 'apps', 'desktop', 'node_modules', '@playwright', 'test'));
const { _electron: electron } = require(require.resolve('playwright', { paths: [playwrightRoot] }));

function listen(handler) {
  const server = http.createServer(handler);
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function verify() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'orbita-hermes-qa-'));
  const pageServer = await listen((_req, response) => {
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end('<!doctype html><title>Página de teste</title><h1>Prévia local</h1>');
  });
  const reservation = await listen();
  const port = reservation.address().port;
  await new Promise((resolve) => reservation.close(resolve));
  const env = { ...process.env, CENTRAL_DATA: path.join(root, 'dados'), ORBITA_NO_BROWSER: '1', ORBITA_PORT: String(port), ORBITA_AGY_PATH: fixturePath,
    ORBITA_PREVIEW_TEST_PROFILE: path.join(root, 'desktop') };
  const backend = spawn(backendPath, ['--sem-navegador'], { cwd: path.dirname(backendPath), env, windowsHide: true });
  let logs = '';
  backend.stdout.on('data', (chunk) => { logs += chunk.toString(); });
  backend.stderr.on('data', (chunk) => { logs += chunk.toString(); });
  let client;
  let window;
  const errors = [];
  try {
    const api = new OrbitaApi(port);
    let ready;
    for (let index = 0; index < 80 && !ready && backend.exitCode === null; index++) {
      ready = await api.call('state').catch(() => undefined);
      if (!ready) await delay(200);
    }
    assert.equal(ready?.version, '0.3.9', logs);
    const project = await api.call('project', { name: 'Projeto de ensaio' });
    const old = await api.call('mission', { projectId: project.id, name: 'Histórico anterior' });
    const other = await api.call('mission', { projectId: project.id, name: 'Outra missão' });
    const original = 'Mensagem anterior do Orbita';
    const reply = 'Proponho criar uma função soma e testar o resultado.';
    await api.call('send', { missionId: old.id, text: original });
    const readyAbort = new AbortController();
    const stopReading = setTimeout(() => readyAbort.abort(), 15_000);
    let sawReply = false;
    try {
      await api.watch(old.id, 0, readyAbort.signal, (items) => {
        sawReply ||= items.some((item) => item.kind === 'mensagem' && item.text === reply);
        if (sawReply) readyAbort.abort();
      });
    } catch (error) { if (error.name !== 'AbortError') throw error; }
    finally { clearTimeout(stopReading); }
    assert.ok(sawReply, 'O CLI de teste concluiu a conversa inicial.');

    const binary = path.join(previewPath, 'Orbita.Studio.Preview.exe');
    client = await electron.launch({ executablePath: binary, env, timeout: 30_000 });
    window = await client.firstWindow();
    window.setDefaultTimeout(12_000);
    window.on('pageerror', (error) => errors.push(error.message));
    await window.getByRole('button', { name: /Histórico anterior/ }).click();
    await window.getByText(original, { exact: true }).waitFor();
    await window.getByText(reply, { exact: true }).waitFor();
    const input = window.getByRole('textbox', { name: 'Mensagem para o Antigravity' });
    const conversation = window.getByLabel('Conversa', { exact: true });
    await input.fill('Rascunho preservado');
    await window.getByRole('button', { name: /Outra missão/ }).click();
    assert.equal(await input.inputValue(), '');
    await window.getByRole('button', { name: /Histórico anterior/ }).click();
    assert.equal(await input.inputValue(), 'Rascunho preservado');
    await input.fill('Continue o pedido de ensaio');
    await window.getByRole('button', { name: 'Enviar', exact: true }).click();
    await conversation.getByText('Continue o pedido de ensaio', { exact: true }).waitFor();
    await window.waitForFunction((text) => Array.from(document.querySelectorAll('[aria-label="Conversa"] p')).filter((item) => item.textContent === text).length === 2, reply);
    assert.equal(await conversation.getByText('Continue o pedido de ensaio', { exact: true }).count(), 1);

    await window.getByRole('button', { name: 'Simples', exact: true }).click();
    await window.getByRole('button', { name: 'Atividade', exact: true }).click();
    await window.getByText('Conversa atualizada.', { exact: true }).first().waitFor();
    await window.getByRole('button', { name: 'Conversa', exact: true }).click();
    await window.getByRole('button', { name: 'Navegador do projeto' }).click();
    const url = `http://127.0.0.1:${pageServer.address().port}/`;
    await window.getByRole('textbox', { name: 'Endereço do projeto' }).fill(url);
    await window.getByRole('button', { name: 'Abrir página' }).click();
    let opened = false;
    for (let index = 0; index < 25 && !opened; index++) {
      opened = await client.evaluate(({ webContents }, address) => webContents.getAllWebContents().some((page) => page.getURL() === address && page.getTitle() === 'Página de teste'), url);
      if (!opened) await delay(200);
    }
    if (!opened) console.error('Páginas da janela:', await client.evaluate(({ webContents }) => webContents.getAllWebContents().map((page) => [page.getURL(), page.getTitle()])));
    assert.ok(opened, 'O navegador interno abriu a página do projeto.');
    await window.getByRole('button', { name: 'Fechar navegador' }).click();
    await window.getByRole('button', { name: 'Navegador do projeto' }).click();
    assert.equal(await window.getByRole('textbox', { name: 'Endereço do projeto' }).inputValue(), url);
    await window.getByRole('button', { name: 'Fechar navegador' }).click();
    assert.equal((await api.call('state')).missions.find(({ id }) => id === other.id).state, 'pronta');
    assert.deepEqual(errors, []);
    if (screenshotPath) await window.screenshot({ path: screenshotPath });
    console.log('Orbita Desktop: histórico, continuação, rascunhos, atividade e navegador interno verificados com o backend real e CLI de ensaio.');
  } catch (error) {
    if (window) {
      console.error('Interface:', (await window.locator('body').innerText().catch(() => '')).slice(0, 1500));
      console.error('Erros da interface:', errors);
    }
    throw error;
  } finally {
    await client?.close().catch(() => {});
    backend.kill();
    pageServer.close();
  }
}

verify().catch((error) => { console.error(error); process.exitCode = 1; });
