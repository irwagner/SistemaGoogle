const assert = require('node:assert/strict');
const { spawn, execFile } = require('node:child_process');
const { realpathSync } = require('node:fs');
const fs = require('node:fs/promises');
const http = require('node:http');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');
const { promisify } = require('node:util');
const { OrbitaApi } = require('../src/Orbita.Hermes/native/orbita-api.cjs');

const exec = promisify(execFile);
const workspace = path.resolve(__dirname, '..');
const cache = path.join(workspace, '.orbita-hermes');
const upstream = path.join(cache, 'upstream');
const desktop = path.join(upstream, 'apps', 'desktop');
const runtime = path.join(cache, 'runtime-venv', 'Scripts');
const playwright = realpathSync(path.join(desktop, 'node_modules', '@playwright', 'test'));
const { _electron } = require(require.resolve('playwright', { paths: [playwright] }));

async function poolFixture(backendPort) {
  const accounts = ['A', 'B'].map((letter, index) => ({
    id: `test-${letter.toLowerCase()}`, label: `Conta ${letter}`, email: `${letter.toLowerCase()}@example.test`, observed: true,
    gemini: { fiveHour: { percent: 72 - index * 10 }, weekly: { percent: 43 } },
    claude: { fiveHour: { percent: 62 }, weekly: { percent: 51 } },
  }));
  const pool = { running: false, active: null, ready: 2, mode: null, enrollment: null, reserve: 15, accounts };
  const operations = [];
  const turns = [];
  const tested = [];
  const verificationUrl = 'https://accounts.google.com/v3/signin/continue?flowName=orbita-ensaio';
  let verified = false;
  const fixture = http.createServer(async (request, response) => {
    const route = request.url.split('?')[0];
    const headers = { ...request.headers, host: `127.0.0.1:${backendPort}` };
    let body = request;
    if (route.startsWith('/api/pool') || (request.method === 'POST' && route.endsWith('/turns'))) {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const text = Buffer.concat(chunks).toString('utf8');
      const data = text ? JSON.parse(text) : {};
      if (route.startsWith('/api/pool')) {
        if (request.method !== 'GET') operations.push(route);
        if (route.endsWith('/start')) pool.running = true;
        if (route.endsWith('/accounts')) pool.enrollment = { message: 'Ensaio', deadline: Date.now() + 60_000 };
        if (route.endsWith('/cancel')) pool.enrollment = null;
        if (route.endsWith('/reserve')) pool.reserve = data.percent;
        let result = pool;
        if (route.endsWith('/test')) {
          tested.push(data.accounts);
          accounts[1].verificationRequired = !verified;
          const checks = data.accounts.map(id => ({ id, state: !verified && id === 'test-b' ? 'verification-required' : 'ready' }));
          result = { checks, rotation: verified ? 'passed' : 'blocked', from: 'Conta B', to: 'Conta A' };
        }
        if (route.endsWith('/verification')) {
          assert.equal(data.id, 'test-b');
          result = { url: verificationUrl };
        }
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify(result));
        return;
      }
      turns.push(data);
      // The local ACP fixture runs without Google or the Windows credential store.
      body = Buffer.from(JSON.stringify({ ...data, accounts: [] }));
      headers['content-length'] = String(body.length);
    }
    const forwarded = http.request({ host: '127.0.0.1', port: backendPort, method: request.method, path: request.url, headers }, upstream => {
      response.writeHead(upstream.statusCode, upstream.headers);
      response.flushHeaders();
      upstream.pipe(response);
    });
    forwarded.on('error', () => response.destroy());
    response.on('close', () => forwarded.destroy());
    if (Buffer.isBuffer(body)) forwarded.end(body);
    else body.pipe(forwarded);
  });
  await new Promise(resolve => fixture.listen(0, '127.0.0.1', resolve));
  return { fixture, port: fixture.address().port, operations, turns, pool, tested, verificationUrl,
    finishVerification: () => { verified = true; } };
}

async function verify() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'orbita-hermes-native-'));
  const profile = path.join(root, 'hermes');
  const server = net.createServer();
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  const api = new OrbitaApi(port);
  const windows = path.join(workspace, 'entrega', 'Windows');
  const release = path.join(windows, 'OrbitaStudio-0.3.17-win-x64');
  const backendPath = path.join(release, 'studio', 'Orbita.Studio.exe');
  const fixturePath = path.join(workspace, 'tests', 'Orbita.Tests', 'bin', 'Release', 'net10.0', 'Orbita.Tests.exe');
  const backend = spawn(backendPath, ['--sem-navegador'], { cwd: path.dirname(backendPath), windowsHide: true, stdio: 'ignore',
    env: { ...process.env, CENTRAL_DATA: path.join(root, 'orbita'), ORBITA_PORT: String(port), ORBITA_NO_BROWSER: '1', ORBITA_AGY_PATH: fixturePath } });
  let app;
  let page;
  let mock;
  const errors = [];
  try {
    let ready;
    for (let index = 0; index < 60 && !ready; index++) {
      ready = await api.call('state').catch(() => undefined);
      if (!ready) await delay(200);
    }
    assert.equal(ready?.version, '0.3.17');
    const project = await api.call('project', { name: 'Projeto de teste' });
    const otherProject = await api.call('project', { name: 'Segundo projeto de teste' });
    const mission = await api.call('mission', { projectId: project.id, name: 'Conversa do Orbita' });
    await api.call('send', { missionId: mission.id, text: 'Histórico vindo do Orbita' });
    const cancel = new AbortController();
    let observed = false;
    try {
      await api.watch(mission.id, 0, AbortSignal.any([cancel.signal, AbortSignal.timeout(15_000)]), (items) => {
        if (items.some((item) => item.text === 'Conversa atualizada.')) { observed = true; cancel.abort(); }
      });
    } catch (error) { if (!cancel.signal.aborted) throw error; }
    assert.ok(observed);
    mock = await poolFixture(port);
    await exec('powershell.exe', ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'Abrir-OrbitaHermesNativo.ps1'),
      '-Porta', String(mock.port), '-Perfil', profile, '-PrepararSomente'], { timeout: 45_000, windowsHide: true });

    app = await _electron.launch({ executablePath: path.join(desktop, 'node_modules', 'electron', 'dist', 'electron.exe'), args: [desktop], cwd: desktop,
      env: { ...process.env, ORBITA_STUDIO_DESKTOP: '1', HERMES_HOME: profile, CENTRAL_DATA: path.join(root, 'producao'),
        ORBITA_CENTRAL_EXE: path.join(windows, 'OrbitaStudio-0.3.10-win-x64', 'producao', 'Central.exe'),
        ORBITA_SIGNAL_SETUP: path.join(workspace, 'Vincular-SignalOrbita.cmd'), ORBITA_MANAGE_MESSAGING: '1',
        HERMES_DESKTOP_USER_DATA_DIR: path.join(profile, 'desktop'), HERMES_DESKTOP_HERMES_ROOT: upstream,
        HERMES_DESKTOP_HERMES: path.join(runtime, 'hermes.exe'), HERMES_DESKTOP_APP_NAME: 'Orbita Studio',
        HERMES_DESKTOP_ISOLATED_BACKEND: '1', HERMES_DESKTOP_IGNORE_EXISTING: '1', HERMES_DESKTOP_SKIP_PROTOCOL_REGISTRATION: '1',
        HERMES_DESKTOP_SKIP_QUIT_CONFIRM: '1', OPENAI_API_KEY: 'orbita-desktop-local', PATH: `${runtime};${process.env.PATH}` } });
    page = await app.firstWindow();
    page.setDefaultTimeout(70_000);
    page.on('pageerror', (error) => errors.push(error.message));
    const accountsButton = page.getByRole('button', { name: 'Contas e cotas do Antigravity' });
    await page.getByText('Orbita Studio', { exact: true }).first().waitFor();
    await page.locator('[aria-label="ORBITA STUDIO"]').waitFor();
    await page.getByRole('button', { name: 'Recursos do agente' }).waitFor();
    await page.locator('[data-slot="orbita-antigravity-status"]').first().waitFor();
    const usage = page.locator('[data-slot="orbita-system-usage"]');
    for (const label of ['CPU', 'RAM', 'SSD']) await usage.getByText(new RegExp(`^${label} \\d{1,3}%$`)).waitFor();
    for (const value of (await usage.innerText()).matchAll(/(\d+)%/g)) assert.ok(Number(value[1]) <= 100);
    assert.match(await usage.getAttribute('title'), /Espaço ocupado no disco do Windows/);
    assert.match(await usage.evaluate(node => node.nextElementSibling?.getAttribute('aria-label')), /settings|configuraç/i);
    await accountsButton.click();
    assert.equal(await page.title(), 'Orbita Studio');
    const dialog = page.getByRole('dialog', { name: 'Contas e cotas do Antigravity' });
    await dialog.getByText('a@example.test', { exact: false }).waitFor();
    await dialog.getByText('b@example.test', { exact: false }).waitFor();
    assert.equal(await dialog.locator('meter').count(), 8);
    await dialog.getByRole('button', { name: 'Conectar conta Google' }).click();
    await dialog.getByText('Conclua o login na janela do Antigravity CLI.', { exact: false }).waitFor();
    await dialog.getByRole('button', { name: 'Cancelar login' }).click();
    await dialog.getByRole('button', { name: 'Cancelar login' }).waitFor({ state: 'hidden' });
    await dialog.getByRole('button', { name: 'Atualizar cotas' }).click();
    await dialog.getByLabel('Reserva (%)').fill('21');
    await dialog.getByRole('button', { name: 'Salvar margem' }).click();
    await dialog.getByText('Margem de reserva salva.').waitFor();
    assert.equal(mock.pool.reserve, 21);
    assert.ok(mock.operations.includes('/api/pool/refresh'));
    await dialog.getByLabel('Conexão nos próximos pedidos').selectOption('pool');
    await dialog.getByRole('article', { name: 'Conta B' }).getByRole('checkbox').check();
    await dialog.getByRole('article', { name: 'Conta A' }).getByRole('checkbox').check();
    await dialog.getByLabel('Modelo nos próximos pedidos').selectOption('neon');
    await dialog.getByLabel('Esforço nos próximos pedidos').selectOption('xhigh');
    await dialog.getByRole('button', { name: 'Salvar preferências' }).click();
    await dialog.getByText('Preferências salvas para os próximos pedidos.').waitFor();
    await app.evaluate(({ shell }) => {
      shell.openExternal = async url => { globalThis.orbitaVerificationOpened = url; };
    });
    await dialog.getByRole('button', { name: 'Testar rotação' }).click();
    await dialog.getByText('A troca simulada não encontrou duas contas disponíveis na ordem testada.').waitFor();
    await dialog.getByRole('article', { name: 'Conta B' }).getByRole('button', { name: 'Verificar no Google' }).click();
    await dialog.getByText('Conclua a verificação na página do Google.', { exact: false }).waitFor();
    assert.equal(await app.evaluate(() => globalThis.orbitaVerificationOpened), mock.verificationUrl);
    assert.equal(mock.turns.length, 0);
    mock.finishVerification();
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await dialog.getByText('Troca simulada: Conta B → Conta A.').waitFor();
    assert.deepEqual(mock.tested, [['test-b', 'test-a'], ['test-b', 'test-a']]);
    if (process.argv[2]) await page.screenshot({ path: path.resolve(process.argv[2]) });
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden' });

    await page.getByText('Conversa do Orbita', { exact: true }).first().click();
    await page.getByText('Histórico vindo do Orbita', { exact: true }).first().waitFor();
    const reply = 'Proponho criar uma função soma e testar o resultado.';
    await page.getByText(reply, { exact: true }).filter({ visible: true }).first().waitFor();
    const composer = page.locator('[data-slot="composer-root"] [contenteditable="true"]').filter({ visible: true }).first();
    const permission = page.getByRole('button', { name: 'Permissão total', exact: true }).filter({ visible: true }).first();
    assert.equal(await permission.getAttribute('aria-pressed'), 'false');
    await permission.click();
    await page.getByRole('button', { name: 'Permissão total', exact: true, pressed: true }).first().waitFor();
    assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'orbita-permission.json'), 'utf8')), true);
    await composer.fill(`Implemente a soma pelo cliente nativo ${'detalhe '.repeat(4100)}ORB-SENTINELA-PEDIDO-LONGO`);
    await composer.press('Enter');
    await page.getByText('Criei soma.mjs e o teste passou.', { exact: true }).first().waitFor();
    let saved;
    for (let index = 0; index < 50; index++) {
      saved = (await api.call('state')).missions.find((item) => item.id === mission.id);
      if (saved.state === 'concluida') break;
      await delay(150);
    }
    assert.equal(saved.state, 'concluida');
    assert.equal(saved.sessionId, 'orbita-sessao-ensaio');
    assert.equal(saved.effort, 'xhigh');
    assert.equal(saved.fullAccess, true);
    assert.equal(saved.accepted, saved.revision);
    assert.notEqual(saved.accepted, saved.baseline);
    assert.match(await fs.readFile(path.join(saved.workspace, 'soma.mjs'), 'utf8'), /export const soma/);
    assert.equal(mock.turns[0].execute, true);
    assert.equal(mock.turns[0].fullAccess, true);
    assert.deepEqual(mock.turns[0].accounts, ['test-b', 'test-a']);
    assert.equal(mock.turns[0].model, 'neon');
    assert.equal(mock.turns[0].effort, 'xhigh');
    assert.ok(mock.turns[0].text.endsWith('ORB-SENTINELA-PEDIDO-LONGO'));
    const log = await fs.readFile(path.join(root, 'orbita', 'orbita', 'missoes', mission.id, 'eventos.jsonl'), 'utf8');
    const events = log.trim().split('\n').map(line => JSON.parse(line).text);
    assert.ok(events.includes('CLI de ensaio · permissão total'));
    assert.ok(events.includes('Ferramenta autorizada pelo modo Permissão total.'));
    await permission.click();
    await page.getByRole('button', { name: 'Permissão total', exact: true, pressed: false }).first().waitFor();

    const projectTree = page.getByRole('region', { name: 'Projetos do Orbita' });
    await projectTree.getByRole('button', { name: otherProject.name, exact: true }).waitFor();
    await projectTree.getByRole('button', { name: 'Novo projeto', exact: true }).click();
    const projectDialog = page.getByRole('dialog', { name: 'Novo projeto' });
    await projectDialog.getByLabel('Nome do projeto').fill('Projeto com pasta padrão');
    await projectDialog.getByText('Pasta padrão do Orbita', { exact: true }).waitFor();
    await projectDialog.getByRole('button', { name: 'Criar projeto' }).click();
    await projectDialog.waitFor({ state: 'hidden' });
    const managed = (await api.call('state')).projects.find(item => item.name === 'Projeto com pasta padrão');
    assert.equal(managed?.managed, true);
    assert.ok((await fs.stat(managed.workspace)).isDirectory());

    await projectTree.getByRole('button', { name: 'Novo projeto', exact: true }).click();
    await projectDialog.getByLabel('Nome do projeto').fill('Projeto com chats');
    const chosenFolder = path.join(root, 'Pasta escolhida no teste');
    await fs.mkdir(chosenFolder);
    const idea = path.join(chosenFolder, 'ideia.md');
    await fs.writeFile(idea, 'versão inicial');
    await fs.writeFile(path.join(chosenFolder, '.env'), 'FIXTURE=true');
    await app.evaluate(({ dialog }, folder) => {
      dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [folder] });
    }, chosenFolder);
    await projectDialog.getByRole('button', { name: 'Vincular pasta…' }).click();
    await projectDialog.getByText(chosenFolder, { exact: true }).waitFor();
    assert.ok(!(await api.call('state')).projects.some(item => item.workspace === chosenFolder));
    await projectDialog.getByRole('button', { name: 'Criar projeto' }).click();
    await projectDialog.waitFor({ state: 'hidden' });
    const linked = (await api.call('state')).projects.find(item => item.workspace === chosenFolder);
    assert.equal(linked?.name, 'Projeto com chats');
    assert.equal(linked.managed, false);

    const projectChats = projectTree.getByRole('list', { name: 'Chats de Projeto com chats' });
    const addChat = projectTree.getByRole('button', { name: 'Novo chat em Projeto com chats' });
    await addChat.click();
    const newSession = page.getByRole('dialog', { name: 'Novo chat' });
    await newSession.waitFor();
    assert.equal(await newSession.getByLabel('Projeto', { exact: true }).inputValue(), linked.id);
    await newSession.getByLabel('Nome do chat (opcional)').fill('Segunda missão nativa');
    await newSession.getByRole('button', { name: 'Abrir chat' }).click();
    await newSession.waitFor({ state: 'hidden' });
    await projectChats.getByRole('button', { name: 'Segunda missão nativa', exact: true }).waitFor();
    await page.getByText('Histórico vindo do Orbita', { exact: true }).first().waitFor({ state: 'hidden' });
    const newComposer = page.locator('[data-slot="composer-root"] [contenteditable="true"]').filter({ visible: true }).first();
    await newComposer.fill('Planeje a soma no segundo projeto');
    await newComposer.press('Enter');
    await page.getByText('Planeje a soma no segundo projeto', { exact: true }).first().waitFor();
    await page.getByText(reply, { exact: true }).filter({ visible: true }).first().waitFor();
    const afterNew = await api.call('state');
    const newMission = afterNew.missions.find((item) => item.name === 'Segunda missão nativa');
    assert.equal(newMission?.projectId, linked.id);
    assert.notEqual(newMission.workspace, saved.workspace);
    const dirs = await fs.readdir(newMission.workspace);
    assert.ok(dirs.includes('.git'));
    assert.ok(!dirs.includes('.env'));
    assert.equal(await fs.readFile(path.join(newMission.workspace, 'ideia.md'), 'utf8'), 'versão inicial');
    assert.deepEqual((await fs.readdir(chosenFolder)).sort(), ['.env', 'ideia.md']);
    const chats = mock.turns.filter(turn => turn.text === 'Planeje a soma no segundo projeto');
    assert.equal(chats.length, 1);
    assert.equal(chats[0].execute, false);
    assert.equal(chats[0].fullAccess, false);
    assert.equal(afterNew.missions.length, 2);

    const accountBlocked = 'Ensaio de verificação do Google';
    await newComposer.fill(`Executar: ${accountBlocked}`);
    await newComposer.press('Enter');
    await page.getByText('O Google pediu verificação da conta usada no pedido.', { exact: false }).first().waitFor();
    let blockedMission;
    for (let index = 0; index < 50; index++) {
      blockedMission = (await api.call('state')).missions.find(item => item.id === newMission.id);
      if (blockedMission.state === 'aguardando-verificacao') break;
      await delay(150);
    }
    assert.equal(blockedMission.state, 'aguardando-verificacao');
    assert.equal(blockedMission.pending, accountBlocked);
    assert.equal(blockedMission.accepted, newMission.accepted);
    assert.equal((await api.call('diff', { missionId: newMission.id })).diff, '');

    await newComposer.fill('Planeje a soma depois da verificação');
    await newComposer.press('Enter');
    await page.getByText(reply, { exact: true }).filter({ visible: true }).nth(1).waitFor();
    const nextChat = mock.turns.find(turn => turn.text === 'Planeje a soma depois da verificação');
    assert.ok(nextChat);
    assert.equal(nextChat.execute, false);

    await fs.writeFile(idea, 'versão seguinte');
    await addChat.click();
    await newSession.getByRole('button', { name: 'Abrir chat' }).click();
    const numbered = projectChats.getByRole('button', { name: 'Chat 1', exact: true });
    await numbered.waitFor({ state: 'visible' });
    const sameProject = (await api.call('state')).missions.filter(item => item.projectId === linked.id);
    assert.equal(sameProject.length, 2);
    assert.equal(new Set(sameProject.map(item => item.workspace)).size, 2);
    const numberedMission = sameProject.find(item => item.name === 'Chat 1');
    assert.equal(await fs.readFile(path.join(numberedMission.workspace, 'ideia.md'), 'utf8'), 'versão seguinte');
    assert.equal(await fs.readFile(path.join(newMission.workspace, 'ideia.md'), 'utf8'), 'versão inicial');
    await projectChats.getByRole('button', { name: 'Segunda missão nativa', exact: true }).click();
    await page.getByText('Planeje a soma depois da verificação', { exact: true }).filter({ visible: true }).first().waitFor();
    await numbered.click();
    await page.getByText('Planeje a soma depois da verificação', { exact: true }).first().waitFor({ state: 'hidden' });
    const externalText = 'Ensaio de leitura do chat pelo Signal';
    const incoming = new FormData();
    const receipt = 'a97b0ad0618c41278645736a78062c3d';
    const csv = 'produto,quantidade\nSIGNAL-CSV-SENTINELA,12\n';
    const filename = 'extrato do Signal.csv';
    incoming.append('turn', JSON.stringify({ text: externalText, execute: true, source: 'signal', requestId: receipt }));
    incoming.append('files', new Blob([csv], { type: 'text/csv' }), filename);
    await api.request(`/missions/${numberedMission.id}/turns/attachments`, { method: 'POST', body: incoming });
    const signalActivity = page.locator('[data-slot="orbita-signal-progress"]').filter({ visible: true });
    await signalActivity.getByText('Executando · Signal', { exact: true }).waitFor();
    await signalActivity.getByText(/^[1-9]\d?s$/).waitFor();
    await page.getByText(externalText, { exact: true }).filter({ visible: true }).waitFor();
    await page.getByText('Fim do streaming.', { exact: true }).filter({ visible: true }).waitFor();
    await signalActivity.waitFor({ state: 'hidden' });
    assert.equal(await page.getByText(externalText, { exact: true }).filter({ visible: true }).count(), 1);
    const downloaded = path.join(root, 'anexo recuperado.csv');
    await app.evaluate(({ dialog }, filePath) => {
      dialog.showSaveDialog = async () => ({ canceled: false, filePath });
    }, downloaded);
    await page.getByRole('button', { name: filename, exact: true }).click();
    let recovered;
    for (let index = 0; index < 40 && !recovered; index++) {
      recovered = await fs.readFile(downloaded, 'utf8').catch(() => '');
      if (!recovered) await delay(100);
    }
    assert.equal(recovered, csv);
    const snapshot = await (await api.request(`/missions/${numberedMission.id}/signal-activity?requestId=${receipt}`)).json();
    assert.equal(snapshot.turns.length, 1);
    assert.equal(snapshot.turns[0].terminalState, 'concluida');
    assert.match(snapshot.turns[0].reply, /Fim do streaming\./);

    const discordText = 'Ensaio de leitura do chat pelo Discord';
    const discordReceipt = 'c4fd3659cb6445e2ac174036b0377941';
    await api.request(`/missions/${numberedMission.id}/turns`, {
      method: 'POST', body: { text: discordText, execute: true, source: 'discord', requestId: discordReceipt },
    });
    const discordActivity = page.locator('[data-slot="orbita-signal-progress"]').filter({ visible: true });
    await discordActivity.getByText('Executando · Discord', { exact: true }).waitFor();
    await page.getByText(discordText, { exact: true }).filter({ visible: true }).waitFor();
    await discordActivity.waitFor({ state: 'hidden' });
    const discordSnapshot = await (await api.request(`/missions/${numberedMission.id}/signal-activity?requestId=${discordReceipt}`)).json();
    assert.equal(discordSnapshot.turns.length, 1);
    assert.equal(discordSnapshot.turns[0].source, 'discord');
    assert.equal(discordSnapshot.turns[0].terminalState, 'concluida');

    await page.getByRole('button', { name: 'Mensagens', exact: true }).first().click();
    await page.getByRole('button', { name: 'Discord', exact: true }).first().click();
    await page.getByText('Crie o bot Orbita Studio no portal do Discord', { exact: false }).waitFor();
    for (const field of ['Token do bot', 'IDs de usuário autorizados']) {
      await page.getByText(field, { exact: true }).waitFor();
    }
    assert.equal(await page.locator('#messaging-field-DISCORD_BOT_TOKEN').getAttribute('type'), 'password');
    await page.getByRole('link', { name: 'Abrir portal do Discord' }).waitFor();
    assert.equal(await page.getByText('IDs de canais autorizados', { exact: true }).count(), 0);
    await page.getByRole('button', { name: /Opções avançadas/ }).click();
    await page.locator('#messaging-field-DISCORD_ALLOWED_USERS').fill('173829405617283901');
    await page.locator('#messaging-field-DISCORD_ALLOWED_CHANNELS').fill('273829405617283901');
    await page.locator('#messaging-field-DISCORD_BOT_TOKEN').fill('token-do-ensaio');
    await page.getByRole('button', { name: 'Salvar e conectar', exact: true }).waitFor();
    await page.locator('#messaging-field-DISCORD_BOT_TOKEN').fill('');
    await page.getByRole('button', { name: 'Salvar alterações', exact: true }).click();
    await page.waitForFunction(() => document.body.innerText.includes('IDs de canais autorizados') &&
      document.querySelector('label[for="messaging-field-DISCORD_ALLOWED_CHANNELS"]')?.parentElement?.textContent.includes('Salvo'));
    const discordSettings = await fs.readFile(path.join(profile, '.env'), 'utf8');
    assert.match(discordSettings, /^DISCORD_ALLOWED_USERS=173829405617283901\r?$/m);
    assert.match(discordSettings, /^DISCORD_ALLOWED_CHANNELS=273829405617283901\r?$/m);
    await page.getByRole('button', { name: 'Telegram', exact: true }).first().click();
    await page.getByRole('button', { name: 'Criar com QR', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Signal', exact: true }).first().click();
    await app.evaluate(({ shell }) => {
      shell.openPath = async file => { globalThis.orbitaSignalLauncher = file; return ''; };
    });
    await page.getByRole('button', { name: 'Vincular aparelho Signal', exact: true }).click();
    await page.getByText('Escaneie o QR na janela que abriu.', { exact: false }).waitFor();
    assert.equal(await app.evaluate(() => globalThis.orbitaSignalLauncher), path.join(workspace, 'Vincular-SignalOrbita.cmd'));

    for (const [label, route] of [['Vídeo e minisséries', 'video'], ['Pesquisa', 'pesquisa'], ['Áudio', 'audio']]) {
      await page.getByRole('button', { name: label, exact: true }).click();
      let heading;
      for (let attempt = 0; attempt < 120 && heading !== label; attempt++) {
        heading = await app.evaluate(async ({ webContents }, area) => {
          const guest = webContents.getAllWebContents().find(web => web.getURL().endsWith(`/area/${area}`));
          return guest?.executeJavaScript('document.querySelector("h1")?.textContent').catch(() => null);
        }, route);
        if (heading !== label) await delay(250);
      }
      assert.equal(heading, label);
    }

    assert.deepEqual(errors, []);
    const gatewayPid = path.join(profile, 'gateway.pid');
    assert.ok(await fs.stat(gatewayPid), 'a abertura do Orbita deve iniciar o gateway do perfil');
    const trayState = await app.evaluate(({ BrowserWindow }) => {
      const window = BrowserWindow.getAllWindows().find(item => item.getTitle() === 'Orbita Studio');
      window.close();
      return { open: !window.isDestroyed(), visible: !window.isDestroyed() && window.isVisible() };
    });
    assert.deepEqual(trayState, { open: true, visible: false });
    assert.ok(await fs.stat(gatewayPid), 'manter o Orbita na bandeja deve preservar a conexão');
    await app.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows().find(item => item.getTitle() === 'Orbita Studio').show();
    });
    await page.locator('[data-slot="orbita-system-usage"]').waitFor();
    await app.close();
    app = null;
    await assert.rejects(fs.stat(gatewayPid), { code: 'ENOENT' });
    console.log('Orbita Studio: bandeja, uso do computador, permissões, cotas, worktrees, produção e mensagens verificados.');
  } catch (error) {
    if (page) console.error((await page.locator('body').innerText({ timeout: 2000 }).catch(() => '')).slice(0, 2000), errors);
    const log = await fs.readFile(path.join(profile, 'logs', 'desktop.log'), 'utf8').catch(() => '');
    console.error(log.split('\n').slice(-20).join('\n'));
    throw error;
  } finally {
    await app?.close().catch(() => {});
    mock?.fixture.closeAllConnections();
    mock?.fixture.close();
    backend.kill();
    if (await fs.stat(path.join(profile, 'config.yaml')).catch(() => null)) {
      await exec(path.join(runtime, 'hermes.exe'), ['gateway', 'stop'], { timeout: 10_000, windowsHide: true, env: { ...process.env, HERMES_HOME: profile } }).catch(() => {});
    }
  }
}

verify().catch((error) => { console.error(error); process.exitCode = 1; });
