class OrbitaApi {
  #token;

  constructor(port) {
    if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Porta do Orbita inválida.');
    this.base = `http://127.0.0.1:${port}`;
  }

  async token(signal) {
    if (this.#token) return this.#token;
    const page = await fetch(`${this.base}/`, { redirect: 'error', signal });
    const token = (await page.text()).match(/name="orbita-token" content="([a-f0-9]{64})"/)?.[1];
    if (!page.ok || !token) throw new Error('A porta configurada não está servindo o Orbita Studio.');
    this.#token = token;
    return token;
  }

  async request(path, { method = 'GET', body, signal, retry = true } = {}) {
    const headers = { 'X-Orbita-Token': await this.token(signal) };
    const form = body instanceof FormData;
    if (body && !form) headers['Content-Type'] = 'application/json';
    const reply = await fetch(`${this.base}/api${path}`, { method, headers, redirect: 'error', signal, body: form || !body ? body : JSON.stringify(body) });
    if (reply.status === 401 && retry) {
      this.#token = undefined;
      return this.request(path, { method, body, signal, retry: false });
    }
    if (!reply.ok) {
      const { error } = await reply.json().catch(() => ({}));
      throw new Error(error || `O Orbita respondeu com HTTP ${reply.status}.`);
    }
    return reply;
  }

  async call(action, payload = {}) {
    const id = (value) => {
      if (typeof value !== 'string' || !/^[a-z0-9]{6,40}$/.test(value)) throw new Error('Identificador do Orbita inválido.');
      return value;
    };
    const mission = () => `/missions/${id(payload.missionId)}`;
    switch (action) {
      case 'state': return (await this.request('/state')).json();
      case 'pool': return (await this.request('/pool')).json();
      case 'pool-start': return this.post('/pool/start', {});
      case 'pool-add': return this.post('/pool/accounts', {});
      case 'pool-cancel': return this.post('/pool/cancel', {});
      case 'pool-refresh': return this.post('/pool/refresh', {});
      case 'pool-test': return this.post('/pool/test', { accounts: payload.accounts, model: payload.model });
      case 'pool-verification': return this.post('/pool/verification', { id: payload.id });
      case 'pool-reserve': {
        if (!Number.isInteger(payload.percent) || payload.percent < 0 || payload.percent > 100) throw new Error('Informe uma margem de 0 a 100%.');
        return (await this.request('/pool/reserve', { method: 'PUT', body: { percent: payload.percent } })).json();
      }
      case 'project': return this.post('/projects', { name: payload.name, workspace: payload.workspace });
      case 'mission': return this.post(`/projects/${id(payload.projectId)}/missions`, { name: payload.name });
      case 'signal-activity': {
        const query = new URLSearchParams({ cursor: typeof payload.cursor === 'string' ? payload.cursor : '' });
        return (await this.request(`${mission()}/signal-activity?${query}`)).json();
      }
      case 'signal-attachment': return this.request(`${mission()}/attachments/${id(payload.attachmentId)}`);
      case 'resume': return this.post(`${mission()}/resume`, { fullAccess: payload.fullAccess });
      case 'cancel': return this.post(`${mission()}/cancel`, {});
      case 'diff': return (await this.request(`${mission()}/diff`)).json();
      case 'review': return this.post(`${mission()}/${payload.revert ? 'revert' : 'accept'}`, { revision: payload.revision });
      case 'open-app': return this.post(`${mission()}/open-app`, { path: payload.path });
      case 'send': {
        const turn = { text: payload.text, execute: !!payload.execute, model: payload.model, accounts: payload.accounts,
          fullAccess: !!payload.fullAccess, skill: !!payload.skill };
        if (!Array.isArray(payload.files) || !payload.files.length) return this.post(`${mission()}/turns`, turn);
        const files = payload.files;
        if (files.length > 6 || files.some(({ bytes }) => !(bytes instanceof Uint8Array) || !bytes.length || bytes.length > 5 * 1024 * 1024) ||
            files.reduce((total, { bytes }) => total + bytes.length, 0) > 10 * 1024 * 1024) throw new Error('Use até 6 anexos, 5 MB por arquivo e 10 MB no total.');
        const form = new FormData();
        form.append('turn', JSON.stringify(turn));
        for (const file of files) form.append('files', new Blob([file.bytes]), file.name);
        return this.post(`${mission()}/turns/attachments`, form);
      }
      default: throw new Error('Ação indisponível nesta versão do cliente.');
    }
  }

  async post(path, body) {
    const reply = await this.request(path, { method: 'POST', body });
    return reply.status === 204 ? null : reply.json();
  }

  async watch(id, after, signal, batch) {
    if (!/^[a-z0-9]{6,40}$/.test(id)) throw new Error('Missão inválida.');
    const reply = await this.request(`/missions/${id}/events?after=${Math.max(0, Math.trunc(after || 0))}`, { signal });
    let buffer = '';
    for await (const chunk of reply.body.pipeThrough(new TextDecoderStream())) {
      buffer += chunk;
      const frames = buffer.split('\n\n');
      buffer = frames.pop();
      const items = [];
      for (const frame of frames) {
        const data = frame.split('\n').find((line) => line.startsWith('data: '));
        if (data) items.push(JSON.parse(data.slice(6)));
      }
      if (items.length) batch(items);
    }
  }
}

module.exports = { OrbitaApi };
