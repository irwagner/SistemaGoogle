(() => {
  'use strict';
  const $ = (selector) => document.querySelector(selector);
  const all = (selector) => [...document.querySelectorAll(selector)];
  const token = $('meta[name="orbita-token"]').content;
  const modal = $('#modal');
  const content = $('#modal-content');
  const chatScroll = $('.workbench-body');
  const chat = new window.OrbitaChatRenderer($('#conversation'), text => handle(async () => {
    await navigator.clipboard.writeText(text);
    message('Resposta copiada.');
  }), attachment => handle(() => openAttachment(attachment)));
  const draftFiles = new Map();
  const currentFiles = () => draftFiles.get(state.mission) || [];
  const filePicker = $('#file-picker');
  const composer = $('#composer');
  let chatPosition = { top: 0, follow: true };
  let planSequence = null;
  const state = { projects: [], missions: [], tools: {}, pool: null, project: '', mission: '', records: [],
    tab: 'plan', layout: 'simples', accounts: [], after: 0, feed: null, diff: null, useSkill: false, posting: false, toast: 0, fullAccess: false };
  const labels = { pronta: 'Pronta', conversando: 'Conversando', executando: 'Executando', concluida: 'Concluída',
    pendente: 'Pendente', interrompida: 'Interrompida', 'aguardando-cota': 'Aguardando cota', 'precisa-de-voce': 'Precisa de você',
    'aguardando-verificacao': 'Aguardando verificação do Google',
    aceita: 'Diff aceito', revertida: 'Revertida' };
  const active = () => state.missions.find(item => item.id === state.mission);
  const project = () => state.projects.find(item => item.id === state.project);
  const isWorking = (item) => ['conversando', 'executando'].includes(item?.state);
  const working = () => state.missions.some(isWorking);
  const currentWorking = () => isWorking(active());
  const directLogin = () => state.missions.some(item => isWorking(item) && !item.accountIds?.length);
  const clock = (at) => new Date(at).toLocaleTimeString('pt-BR');
  const moment = (at) => new Date(at).toLocaleString('pt-BR');

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }
  function button(label, action, className = 'btn') {
    const node = element('button', className, label);
    node.type = 'button';
    node.addEventListener('click', () => handle(action));
    return node;
  }
  async function api(path, method = 'GET', body) {
    const form = body instanceof FormData;
    const headers = { 'X-Orbita-Token': token };
    if (!form) headers['Content-Type'] = 'application/json';
    const reply = await fetch('/api' + path, { method, headers, body: form || body === undefined ? body : JSON.stringify(body) });
    if (!reply.ok) {
      const result = await reply.json().catch(() => ({}));
      throw new Error(result.error || `O Orbita respondeu com HTTP ${reply.status}.`);
    }
    return reply.status === 204 ? null : reply.json();
  }
  function message(text, error = false) {
    const toast = $('#toast');
    toast.textContent = text;
    toast.classList.toggle('error', error);
    toast.hidden = false;
    clearTimeout(state.toast);
    state.toast = setTimeout(() => { toast.hidden = true; }, 6000);
  }
  function handle(action) {
    Promise.resolve().then(action).catch(error => message(error.message || String(error), true));
  }
  function readChatPosition() {
    const top = chatScroll.scrollTop;
    return { top, follow: chatScroll.scrollHeight - top - chatScroll.clientHeight <= 56 };
  }
  function syncChatJump() {
    $('#chat-jump').hidden = state.tab !== 'plan' || chatPosition.follow || !$('#conversation').childElementCount;
  }
  function restoreChatPosition(position) {
    if (state.tab !== 'plan') return;
    chatScroll.scrollTop = position.follow ? chatScroll.scrollHeight : position.top;
    chatPosition = readChatPosition();
    syncChatJump();
  }
  function dialog(title, ...nodes) {
    $('#modal-title').textContent = title;
    content.replaceChildren(...nodes);
    if (!modal.open) modal.showModal();
  }
  function formDialog(title, markup, submit) {
    const form = element('form');
    form.innerHTML = markup;
    form.addEventListener('submit', event => {
      event.preventDefault();
      const values = new FormData(form);
      const action = form.querySelector('button[type="submit"]');
      handle(async () => {
        action.disabled = true;
        try { await submit(values); }
        finally { action.disabled = false; }
      });
    });
    dialog(title, form);
    return form;
  }

  function renderAttachmentDraft() {
    const files = currentFiles();
    const draft = $('#attachment-draft');
    draft.hidden = !files.length;
    $('#prompt').required = !files.length;
    draft.replaceChildren(...files.map((file, index) => {
      const item = element('span', 'file-chip');
      const remove = button('×', () => {
        draftFiles.set(state.mission, currentFiles().filter((_, position) => position !== index));
        renderAttachmentDraft();
      }, '');
      remove.setAttribute('aria-label', `Remover ${file.name}`);
      remove.disabled = state.posting;
      item.append(element('span', 'file-name', file.name), remove);
      return item;
    }));
  }

  function addAttachments(selected) {
    if (!active() || working() || state.posting) return;
    const files = [...currentFiles(), ...selected];
    const mb = 1024 * 1024;
    if (files.some(file => !file.size || file.size > 5 * mb)) return message('Cada anexo deve conter dados e ter até 5 MB.', true);
    if (files.length > 6 || files.reduce((sum, file) => sum + file.size, 0) > 10 * mb)
      return message('Anexe até 6 arquivos, com até 10 MB no total.', true);
    draftFiles.set(state.mission, files);
    renderAttachmentDraft();
  }

  async function openAttachment(attachment) {
    const reply = await fetch(`/api/missions/${state.mission}/attachments/${attachment.id}`, { headers: { 'X-Orbita-Token': token } });
    if (!reply.ok) throw new Error('Não foi possível abrir o anexo desta missão.');
    const url = URL.createObjectURL(await reply.blob());
    const download = () => {
      const link = element('a');
      link.href = url;
      link.download = attachment.name;
      link.click();
    };
    if (attachment.mimeType.startsWith('image/')) {
      const preview = element('img', 'attachment-preview');
      preview.src = url;
      preview.alt = attachment.name;
      dialog(attachment.name, preview, button('Baixar imagem', download));
      modal.addEventListener('close', () => URL.revokeObjectURL(url), { once: true });
    } else {
      download();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    }
  }

  async function refreshState() {
    const data = await api('/state');
    state.projects = data.projects;
    state.missions = data.missions;
    state.tools = data.tools;
    $('#app-version').textContent = `v${data.version}`;
    renderWorkspace();
    renderHeading();
  }
  function renderWorkspace() {
    const select = $('#project-select');
    select.replaceChildren(new Option('Selecione um projeto', ''), ...state.projects.map(item => new Option(item.name, item.id)));
    select.value = state.project;
    $('#new-mission').disabled = !project() || working();
    all('[data-context]').forEach(node => { node.disabled = !project(); });
    const missions = state.missions.filter(item => item.projectId === state.project);
    $('#missions').replaceChildren(...(missions.length ? missions.map(item => {
      const node = button('', () => selectMission(item.id), item.id === state.mission ? 'current' : '');
      node.title = `${item.name} — ${labels[item.state] || item.state}`;
      node.setAttribute('aria-current', item.id === state.mission ? 'true' : 'false');
      node.append(element('span', 'branch', '⑂'), element('span', '', item.name));
      return node;
    }) : [element('p', 'muted small', 'Nenhuma missão neste projeto.')]));
  }
  function renderHeading() {
    const mission = active();
    const needsUser = mission?.state === 'precisa-de-voce';
    $('#mission-title').textContent = mission?.name || 'Crie sua primeira missão';
    $('#mission-path').textContent = `Programação / ${project()?.name || 'Workspace'}`;
    const tag = $('#mission-state');
    tag.textContent = mission ? labels[mission.state] || mission.state : 'Workspace';
    tag.classList.toggle('running', currentWorking() || mission?.state === 'concluida');
    tag.classList.toggle('paused', needsUser || ['pendente', 'interrompida', 'aguardando-cota', 'aguardando-verificacao'].includes(mission?.state));
    $('#worktree-label').textContent = mission ? mission.branch : 'Selecione uma missão';
    $('#session-status').textContent = currentWorking() ? 'Sessão ativa' : needsUser ? 'Aguardando sua resposta' : mission?.sessionId ? 'Sessão salva' : 'Aguardando missão';
    $('#empty-plan').hidden = Boolean(mission);
    $('#session').hidden = !mission;
    $('#open-project-app').hidden = !mission || !state.tools.desktop;
    $('#begin-project').textContent = project() ? 'Criar missão' : 'Criar projeto';
    all('[data-cancel-run]').forEach(node => { node.hidden = !currentWorking(); });
    const ready = Boolean(mission && !working() && !state.posting);
    all('#composer button:not([data-cancel-run])').forEach(node => { node.disabled = !ready; });
    all('[data-full]').forEach(node => { node.setAttribute('aria-pressed', String(state.fullAccess === (node.dataset.full === 'true'))); });
    $('#prompt').disabled = !mission;
    $('#prompt').placeholder = needsUser ? 'Diga o que mudou para a missão continuar…' : 'Escreva o pedido ou diga ‘pode fazer’…';
    $('#intervention').hidden = !needsUser;
    $('#intervention').textContent = needsUser ? state.records.findLast(entry => entry.kind === 'estado')?.text || 'Confira a última resposta para retomar.' : '';
    const hasDiff = Boolean(state.diff?.diff || state.diff?.status);
    const diffAccepted = hasDiff && !state.diff.status && mission?.accepted === state.diff.revision;
    $('#accept').hidden = diffAccepted;
    $('#accept').disabled = !ready || !hasDiff;
    if (state.diff) $('#diff-status').textContent = state.diff.status || (state.diff.diff
      ? `Revisão ${state.diff.revision.slice(0, 10)}${diffAccepted ? ' · Diff aceito' : ''}` : 'Worktree limpo.');
    $('#revert').disabled = !ready || !hasDiff;
    $('#refresh-diff').disabled = !mission;
    renderConnection();
  }
  function openProject() {
    formDialog('Novo projeto ou pasta', '<label>Nome do projeto<input name="name" required maxlength="100" autofocus></label><label>Pasta Git existente <small class="muted">Opcional. Deixe em branco para criar um projeto local.</small><input name="workspace" placeholder="C:\\Projetos\\meu-sistema"></label><button class="btn primary" type="submit">Criar projeto</button>', async fields => {
      const item = await api('/projects', 'POST', Object.fromEntries(fields));
      modal.close();
      state.project = item.id;
      await refreshState();
      setTab('plan');
      await selectMission('');
      openMission();
    });
  }
  function openMission() {
    const target = project();
    if (!target) { openProject(); return; }
    const form = formDialog('Nova missão', '<p class="muted small" data-mission-project></p><label>Nome da missão<input name="name" required maxlength="100" autofocus></label><p class="muted small">A missão usa o último commit ou, em um repositório novo, os arquivos já preparados no Git.</p><p class="intervention" role="alert" hidden></p><button class="btn primary" type="submit">Criar worktree</button>', async fields => {
      const notice = form.querySelector('[role="alert"]');
      notice.hidden = true;
      let item;
      try { item = await api(`/projects/${target.id}/missions`, 'POST', Object.fromEntries(fields)); }
      catch (error) {
        notice.textContent = error.message;
        notice.hidden = false;
        return;
      }
      modal.close();
      await refreshState();
      setTab('plan');
      await selectMission(item.id);
      $('#prompt').focus();
    });
    form.querySelector('[data-mission-project]').textContent = `Projeto ${target.name}`;
  }
  async function openContext(name) {
    if (!project()) { openProject(); return; }
    const id = state.project;
    const path = `/projects/${id}/context/${name}`;
    const data = await api(path);
    const form = formDialog(name, '<label>Conteúdo do arquivo<textarea name="text" rows="10" maxlength="64000"></textarea></label><button class="btn primary" type="submit">Salvar</button>', async fields => {
      await api(path, 'PUT', Object.fromEntries(fields));
      modal.close();
      message(`${name} salvo neste projeto.`);
    });
    form.elements.text.value = data.text;
  }

  function openProjectApp() {
    const id = state.mission;
    if (!id) return;
    formDialog('Abrir aplicativo da missão', '<label>Caminho do executável (.exe)<input name="path" required maxlength="2048" autofocus></label><p class="muted small">Use o caminho do .exe dentro do worktree. O Orbita vai abrir o aplicativo na sua área de trabalho.</p><button class="btn primary" type="submit">Abrir no Windows</button>', async fields => {
      const result = await api(`/missions/${id}/open-app`, 'POST', Object.fromEntries(fields));
      modal.close();
      message(result.message);
    });
  }

  async function selectMission(id) {
    state.feed?.abort();
    state.mission = id;
    renderAttachmentDraft();
    state.records = [];
    state.after = 0;
    state.diff = null;
    chat.render([]);
    chatPosition = { top: 0, follow: true };
    planSequence = null;
    if (state.tab === 'plan') chatScroll.scrollTop = 0;
    const item = active();
    if (item) state.project = item.projectId;
    state.fullAccess = item?.fullAccess === true;
    if (item?.accountIds?.length) state.accounts = [...item.accountIds];
    $('#model').value = item?.requestedModel || '';
    $('#diff-status').textContent = '';
    $('#diff-code').replaceChildren();
    localStorage.setItem('orbita-mission', id);
    localStorage.setItem('orbita-project', state.project);
    renderWorkspace();
    renderHeading();
    renderEvents();
    if (!id) return;
    const controller = new AbortController();
    state.feed = controller;
    void stream(id, controller.signal);
    await refreshDiff();
  }
  async function stream(id, signal) {
    while (!signal.aborted) {
      try {
        const reply = await fetch(`/api/missions/${id}/events?after=${state.after}`, { signal, headers: { 'X-Orbita-Token': token } });
        if (reply.status === 401) { message('Sessão local expirada. Recarregue a página.', true); return; }
        if (!reply.ok || !reply.body) throw new Error('Eventos da missão indisponíveis.');
        let buffer = '';
        for await (const piece of reply.body.pipeThrough(new TextDecoderStream())) {
          if (signal.aborted || state.mission !== id) return;
          buffer += piece;
          const frames = buffer.split('\n\n');
          buffer = frames.pop();
          let updated = false;
          let reconcile = false;
          for (const frame of frames) {
            const raw = frame.split('\n').find(line => line.startsWith('data: '));
            if (!raw) continue;
            const entry = JSON.parse(raw.slice(6));
            if (entry.sequence <= state.after) continue;
            state.after = entry.sequence;
            state.records.push(entry);
            updated = true;
            reconcile ||= ['estado', 'git', 'sessao'].includes(entry.kind);
          }
          if (updated) {
            if (state.records.length > 1200) state.records.splice(0, state.records.length - 1200);
            renderEvents();
            if (reconcile) handle(async () => { await refreshState(); if (state.mission === id && !currentWorking()) await refreshDiff(); });
          }
        }
      } catch (error) {
        if (signal.aborted) return;
        if (error.name !== 'AbortError' && state.after === 0) message('Reconectando aos eventos da missão.');
      }
      if (!signal.aborted) await new Promise(resolve => setTimeout(resolve, 950));
    }
  }
  function renderEvents() {
    const reading = state.tab === 'plan' ? readChatPosition() : null;
    const plan = state.records.findLast(item => item.kind === 'plano');
    if (Array.isArray(plan?.data) && plan.sequence !== planSequence) {
      const list = element('ol');
      for (const entry of plan.data) list.append(element('li', '', `${entry.status === 'completed' ? '✓ ' : ''}${entry.content || ''}`));
      $('#plan-list').replaceChildren(list);
      planSequence = plan.sequence;
    } else if (!Array.isArray(plan?.data) && (planSequence !== null || !state.records.length)) {
      $('#plan-list').textContent = 'O plano aparece conforme o Antigravity organiza o trabalho.';
      planSequence = null;
    }
    const dialogue = [];
    const logs = [];
    for (const item of state.records) {
      if (item.kind === 'usuario') dialogue.push({ id: item.sequence, at: item.at, agent: false, text: item.text, attachments: item.data?.attachments });
      else if (item.kind === 'mensagem') {
        if (dialogue.at(-1)?.agent) dialogue.at(-1).text += item.text;
        else dialogue.push({ id: item.sequence, at: item.at, agent: true, text: item.text });
      } else logs.push(item);
    }
    chat.render(dialogue);
    const rows = (items) => items.map(entry => {
      const row = element('div', `log-row ${entry.kind}`);
      row.append(element('time', '', clock(entry.at)), element('span', '', entry.text));
      return row;
    });
    const consoleList = $('#console-log');
    const consoleTop = consoleList.scrollTop;
    const tail = consoleList.scrollHeight - consoleTop - consoleList.clientHeight < 44;
    consoleList.replaceChildren(...(logs.length ? rows(logs.slice(-80)) : [element('div', 'log-empty', 'Os eventos do Antigravity aparecem quando a missão iniciar.')]));
    consoleList.scrollTop = tail ? consoleList.scrollHeight : consoleTop;
    $('#live-log').replaceChildren(...rows(logs.slice(-160)));
    const evidence = logs.filter(item => ['ferramenta', 'git', 'estado'].includes(item.kind)).slice(-14);
    $('#evidence-list').replaceChildren(...(evidence.length ? rows(evidence) : [element('p', 'muted small', 'Aguardando eventos do Antigravity.')]));
    renderHeading();
    if (reading) restoreChatPosition(reading);
  }

  async function refreshDiff() {
    const id = state.mission;
    if (!id) return;
    const data = await api(`/missions/${id}/diff`);
    if (state.mission !== id) return;
    state.diff = data;
    const target = $('#diff-code');
    if (!data.diff) target.replaceChildren(element('p', 'muted', 'As diferenças em relação ao ponto inicial aparecem aqui.'));
    else renderDiff(target, data.diff);
    renderHeading();
  }
  function renderDiff(target, patch) {
    const fragment = document.createDocumentFragment();
    const head = element('div', 'diff-head');
    head.append(element('span', '', 'Antes'), element('span', '', 'Worktree da missão'));
    fragment.append(head);
    let removed = [];
    let added = [];
    function row(before, after, beforeClass, afterClass) {
      const item = element('div', 'diff-line');
      item.append(element('span', beforeClass, before), element('span', afterClass, after));
      fragment.append(item);
    }
    function flush() {
      for (let index = 0; index < Math.max(removed.length, added.length); index++)
        row(removed[index], added[index], removed[index] ? 'removed' : '', added[index] ? 'added' : '');
      removed = [];
      added = [];
    }
    for (const line of patch.split('\n').slice(0, 1800)) {
      if (line.startsWith('-') && !line.startsWith('--- ')) removed.push(line);
      else if (line.startsWith('+') && !line.startsWith('+++ ')) added.push(line);
      else {
        flush();
        if (line.startsWith(' ')) row(line, line);
        else if (line !== '') fragment.append(element('div', 'diff-meta', line));
      }
    }
    flush();
    target.replaceChildren(fragment);
  }
  function setTab(tab) {
    const previous = state.tab;
    if (previous === 'plan' && tab !== 'plan') chatPosition = readChatPosition();
    state.tab = tab;
    document.body.dataset.view = tab;
    for (const item of all('[data-tab]')) {
      const on = item.dataset.tab === tab;
      item.classList.toggle('selected', on);
      item.setAttribute('aria-selected', String(on));
      $(`#view-${item.dataset.tab}`).hidden = !on;
    }
    if (previous !== 'plan' && tab === 'plan') restoreChatPosition(chatPosition);
    else syncChatJump();
    if (tab === 'diff') handle(refreshDiff);
  }
  async function submit(text, execute, resume = false) {
    if (!active() || state.posting) return;
    const id = state.mission;
    const selected = resume ? [] : currentFiles();
    if (!resume && !selected.length && !text.trim()) return;
    state.posting = true;
    renderHeading();
    try {
      const input = resume ? { fullAccess: state.fullAccess } :
        { text, execute, model: $('#model').value, accounts: state.accounts, skill: state.useSkill, fullAccess: state.fullAccess };
      let body = input;
      if (selected.length) {
        body = new FormData();
        body.append('turn', JSON.stringify(input));
        for (const file of selected) body.append('files', file, file.name);
      }
      const route = resume ? 'resume' : selected.length ? 'turns/attachments' : 'turns';
      const result = await api(`/missions/${id}/${route}`, 'POST', body);
      state.missions = state.missions.map(item => item.id === result.id ? result : item);
      if (!resume) draftFiles.delete(id);
      if (state.mission === id) {
        $('#prompt').value = '';
        renderAttachmentDraft();
        state.useSkill = false;
        $('#skill span').textContent = 'Usar SKILL.md';
        setTab('plan');
        restoreChatPosition({ top: 0, follow: true });
      }
      renderWorkspace();
    } finally { state.posting = false; renderHeading(); }
  }
  async function review(revert) {
    if (!state.diff || !active()) return;
    const id = state.mission;
    const revision = state.diff.revision;
    const apply = async () => {
      await api(`/missions/${id}/${revert ? 'revert' : 'accept'}`, 'POST', { revision });
      await refreshState();
      await refreshDiff();
    };
    if (!revert) { await apply(); return; }
    const actions = element('div', 'modal-actions');
    actions.append(button('Cancelar', () => modal.close()), button('Reverter missão', async () => {
      await apply();
      modal.close();
    }, 'btn danger'));
    dialog('Reverter alterações', element('p', 'muted', 'O worktree volta ao ponto inicial. O estado atual fica no histórico Git.'), actions);
  }
  async function history(mission = active()) {
    const box = element('div', 'modal-stack');
    if (mission) {
      const entries = await api(`/missions/${mission.id}/checkpoints`);
      box.append(element('p', 'muted', mission.name));
      if (mission.pending && !working()) box.append(button('Retomar pedido pendente', async () => {
        if (state.mission !== mission.id) await selectMission(mission.id);
        modal.close();
        await submit('', false, true);
      }, 'btn primary'));
      for (const entry of entries) box.append(element('p', 'muted small', `${moment(entry.updated)} · ${labels[entry.state] || entry.state}${entry.model ? ` · ${entry.model}` : ''}`));
    }
    for (const item of state.missions.filter(item => item.id !== mission?.id).slice(0, 12)) {
      const row = button(item.name, async () => { await selectMission(item.id); await history(item); }, 'history-row');
      row.append(element('small', '', `${labels[item.state] || item.state} · ${moment(item.updated)}`));
      box.append(row);
    }
    if (!box.children.length) box.append(element('p', 'muted', 'Os checkpoints aparecem após criar uma missão.'));
    dialog('Continuar de onde parei', box);
  }

  async function refreshPool() {
    state.pool = await api('/pool');
    const pool = state.pool;
    state.accounts = state.accounts.filter(id => pool.accounts.some(item => item.id === id));
    const saved = pool.accounts.length;
    $('#pool-summary').textContent = pool.running ? `Pool: ${pool.ready}/${saved} prontas${pool.mode === 'alternar' ? ' · Auto-Switch' : ''}`
      : saved ? `Pool em espera · ${saved} ${saved === 1 ? 'conta salva' : 'contas salvas'}` : 'Pool em espera';
    $('#open-pool .dot').classList.toggle('on', pool.running);
    renderConnection();
  }
  function renderConnection() {
    const direct = directLogin();
    const account = direct ? null : state.pool?.accounts.find(item => item.id === state.pool.active);
    const chip = $('.account-chip');
    const label = $('#active-account');
    const text = direct ? 'Login direto do CLI' : account ? `Pool: ${account.label}` : working() ? 'Conectando ao pool' : 'Sem execução ativa';
    if (label.textContent !== text) label.textContent = text;
    chip.title = direct ? 'O Antigravity usa o login do CLI para esta missão. A troca de contas pelo pool fica disponível em Contas.' : text;
    chip.querySelector('.dot').classList.toggle('on', direct || Boolean(account));
    renderQuota('#gemini-quota', account?.gemini);
    renderQuota('#opus-quota', account?.claude);
  }
  function renderQuota(selector, family) {
    const root = $(selector);
    [...root.querySelectorAll('label')].forEach((label, index) => {
      const window = index === 0 ? family?.fiveHour : family?.weekly;
      label.querySelector('progress').value = window?.percent ?? 0;
      label.querySelector('b').textContent = window ? `${window.percent}%` : '—';
      label.title = window?.resetTime ? `Renova ${moment(window.resetTime)}` : '';
    });
  }
  async function openPool() {
    await refreshPool();
    const box = element('div', 'modal-stack');
    const pool = state.pool;
    if (directLogin()) box.append(element('p', 'muted', 'A missão em andamento usa o login direto do Antigravity CLI. Selecione contas do pool para usar no próximo pedido.'));
    if (!pool.running) box.append(element('p', 'muted', pool.accounts.length
      ? 'Suas contas estão salvas. Selecione as contas para o próximo pedido e o Orbita inicia o pool quando precisar.'
      : 'Inicie o pool para adicionar contas Google e consultar as cotas do Antigravity.'));
    if (pool.enrollment) box.append(element('p', 'muted', 'Conclua o login na janela do Antigravity CLI e depois atualize esta lista.'));
    const actions = element('div', 'modal-actions');
    const update = (path) => async () => { await api(path, 'POST', {}); await openPool(); };
    if (!pool.running) actions.append(button('Iniciar pool Google', update('/pool/start'), 'btn primary'));
    else {
      actions.append(button(pool.accounts.length ? 'Entrar com outra conta Google' : 'Entrar com a primeira conta Google', update('/pool/accounts'), 'btn primary'));
      actions.append(button('Atualizar cotas', update('/pool/refresh')));
      if (pool.enrollment) actions.append(button('Cancelar login', update('/pool/cancel')));
    }
    box.append(actions);
    const percent = (window) => window ? `${window.percent}%` : '—';
    for (const account of pool.accounts) {
      const row = element('label', 'check-account');
      const input = element('input');
      input.type = 'checkbox';
      input.checked = state.accounts.includes(account.id);
      input.addEventListener('change', () => {
        state.accounts = input.checked ? [...state.accounts, account.id] : state.accounts.filter(id => id !== account.id);
        order.textContent = selection();
      });
      row.append(input, element('span', '', `${account.label} · ${account.email}`), element('small', '',
        `Gemini ${percent(account.gemini.fiveHour)} / ${percent(account.gemini.weekly)} · Claude/GPT ${percent(account.claude.fiveHour)} / ${percent(account.claude.weekly)}`));
      box.append(row);
    }
    function selection() {
      return state.accounts.length ? `Ordem da próxima sessão: ${state.accounts.map(id => pool.accounts.find(row => row.id === id)?.label).join(' → ')}` : 'Sem seleção, o chat usa o login atual do Antigravity CLI.';
    }
    const order = element('p', 'muted small', selection());
    box.append(order);
    if (pool.running) {
      const reserve = element('label');
      reserve.append(element('span', '', 'Margem de reserva (%)'));
      const input = element('input');
      input.type = 'number'; input.min = '0'; input.max = '100'; input.value = pool.reserve;
      reserve.append(input);
      box.append(reserve, button('Salvar margem', async () => { await api('/pool/reserve', 'PUT', { percent: Number(input.value) }); message('Margem salva.'); }));
    }
    dialog('Contas do Antigravity', box);
  }

  function showModule(module) {
    if (module === 'programacao') { setTab('plan'); return; }
    if (module === 'contas') return openPool();
    if (module === 'config') {
      const status = (flag) => flag ? 'Disponível' : 'Indisponível';
      dialog('Configurações', ...[['Antigravity CLI', state.tools.agy], ['Git', state.tools.git], ['FFmpeg', state.tools.ffmpeg], ['FFprobe', state.tools.ffprobe]]
        .map(([name, available]) => element('p', 'log-row', `${name} · ${status(available)}`)));
      return;
    }
    const names = { video: 'Vídeo', pesquisa: 'Pesquisa', audio: 'Áudio' };
    dialog(names[module], element('p', 'muted', module === 'video' ? 'FFmpeg e FFprobe podem ser verificados em Configurações. A edição de vídeo está prevista para a próxima etapa.' : 'Este módulo será conectado após o percurso de Programação.'));
  }
  function toggleSkill() {
    if (!active()) { message('Selecione uma missão antes de invocar SKILL.md.'); return; }
    state.useSkill = !state.useSkill;
    $('#skill span').textContent = state.useSkill ? 'SKILL.md no próximo pedido' : 'Usar SKILL.md';
    $('#prompt').focus();
  }

  function setLayout(mode) {
    const reading = state.tab === 'plan' ? readChatPosition() : null;
    const advanced = mode === 'avancado';
    const changed = state.layout !== mode;
    state.layout = mode;
    document.body.dataset.layout = mode;
    all('[data-layout-choice]').forEach(node => node.setAttribute('aria-pressed', String(node.dataset.layoutChoice === mode)));
    $('#mission-list-title').textContent = advanced ? 'Missões' : 'Conversas';
    $('#tab-plan .tab-label').textContent = advanced ? 'Plano' : 'Conversa';
    $('#tab-diff .tab-label').textContent = advanced ? 'Diff & Evidências' : 'Alterações';
    $('#tab-live .tab-label').textContent = advanced ? 'Ao Vivo' : 'Atividade';
    if (changed) $('#mission-plan').open = advanced;
    localStorage.setItem('orbita-layout', mode);
    if (reading) restoreChatPosition(reading);
  }

  $('#close-modal').onclick = () => modal.close();
  $('#new-project').onclick = openProject;
  $('#new-mission').onclick = openMission;
  $('#open-project-app').onclick = openProjectApp;
  $('#begin-project').onclick = () => project() ? openMission() : openProject();
  $('#project-select').onchange = (event) => {
    state.project = event.target.value;
    setTab('plan');
    handle(() => selectMission(''));
  };
  $('#open-pool').onclick = () => handle(openPool);
  $('#open-history').onclick = () => handle(() => history());
  all('[data-layout-choice]').forEach(node => { node.onclick = () => setLayout(node.dataset.layoutChoice); });
  $('#refresh-diff').onclick = () => handle(refreshDiff);
  $('#chat-jump').onclick = () => restoreChatPosition({ top: 0, follow: true });
  chatScroll.addEventListener('scroll', () => {
    if (state.tab !== 'plan') return;
    chatPosition = readChatPosition();
    syncChatJump();
  }, { passive: true });
  $('#accept').onclick = () => handle(() => review(false));
  $('#revert').onclick = () => handle(() => review(true));
  all('[data-cancel-run]').forEach(node => { node.onclick = () => handle(() => api(`/missions/${state.mission}/cancel`, 'POST', {})); });
  $('#skill').onclick = toggleSkill;
  composer.onsubmit = (event) => { event.preventDefault(); handle(() => submit($('#prompt').value, event.submitter?.value === 'execute')); };
  $('#attach-files').onclick = () => filePicker.click();
  filePicker.onchange = () => { addAttachments(filePicker.files); filePicker.value = ''; };
  $('#prompt').addEventListener('paste', event => {
    const files = event.clipboardData?.files;
    if (!files?.length) return;
    addAttachments(files);
    if (!event.clipboardData.getData('text/plain')) event.preventDefault();
  });
  document.addEventListener('dragover', event => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    composer.classList.toggle('dragging', composer.contains(event.target) && Boolean(active()) && !working() && !state.posting);
  });
  composer.addEventListener('dragleave', event => {
    if (!composer.contains(event.relatedTarget)) composer.classList.remove('dragging');
  });
  document.addEventListener('drop', event => {
    const files = event.dataTransfer?.files;
    if (!files?.length) return;
    event.preventDefault();
    composer.classList.remove('dragging');
    if (composer.contains(event.target)) addAttachments(files);
  });
  all('[data-tab]').forEach(node => { node.onclick = () => setTab(node.dataset.tab); });
  all('[data-full]').forEach(node => { node.onclick = () => { state.fullAccess = node.dataset.full === 'true'; renderHeading(); }; });
  all('[data-context]').forEach(node => { node.onclick = () => handle(() => openContext(node.dataset.context)); });
  all('[data-module]').forEach(node => { node.onclick = () => handle(() => showModule(node.dataset.module)); });
  all('[data-tool]').forEach(node => { node.onclick = () => {
    const descriptions = { hermes: 'Conexão MCP prevista para executar ferramentas pontuais. O Antigravity gerencia a missão.',
      browser: 'O painel registra as chamadas do Antigravity. Um worker externo de navegador ainda não está configurado.',
      media: `FFmpeg ${state.tools.ffmpeg ? 'disponível' : 'indisponível'} · FFprobe ${state.tools.ffprobe ? 'disponível' : 'indisponível'}.`,
      git: state.tools.git ? 'Cada execução salva checkpoints no Git do worktree. Confira as mudanças em Diff & Evidências.' : 'Instale o Git para usar snapshots.' };
    dialog(node.textContent.trim(), element('p', 'muted', descriptions[node.dataset.tool]));
  }; });
  document.addEventListener('keydown', event => {
    if (!event.ctrlKey && !event.metaKey) return;
    const key = event.key.toLowerCase();
    if (key === 'k') { event.preventDefault(); toggleSkill(); }
    if (key === 'j') { event.preventDefault(); setLayout(state.layout === 'simples' ? 'avancado' : 'simples'); }
  });

  handle(async () => {
    const savedLayout = localStorage.getItem('orbita-layout');
    const previousFocus = localStorage.getItem('orbita-chat-focus');
    setLayout(savedLayout === 'simples' || savedLayout === 'avancado' ? savedLayout : previousFocus === '0' ? 'avancado' : 'simples');
    await refreshState();
    const saved = localStorage.getItem('orbita-mission');
    const savedProject = localStorage.getItem('orbita-project');
    state.project = state.projects.some(item => item.id === savedProject) ? savedProject : '';
    await selectMission(state.missions.some(item => item.id === saved) ? saved : '');
    await refreshPool();
    setInterval(() => { refreshPool().catch(() => {}); }, 5000);
  });
})();
