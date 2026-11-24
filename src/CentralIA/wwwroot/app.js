(() => {
  const { token, preview: PREVIEW_ORIGIN } = window.__CENTRAL__;

  const state = {
    view: "home", // home | thread | flow | settings
    projects: [],
    projectCache: {},
    expanded: new Set(),
    recent: [],
    settings: null,
    cooldowns: [],
    pool: [],
    discovered: null,
    ffmpeg: false,
    dataDir: "",
    // Active thread
    pid: null,
    tid: null,
    thread: null,
    live: { running: false },
    showPanel: true,
    panelTab: "preview", // preview | files | diff | git
    files: [],
    selectedFile: null,
    git: null,
    // Active flow
    fid: null,
    flow: null,
    flowSubtitles: true,
    // Home composer
    homeMode: "agente", // agente | equipe | conversa | flow
    homeProject: "",
    homeExecution: "revisao",
    homeAccounts: [],
    homeRoutes: [],
    confirmApis: false,
    agyPool: null,
    agyHydrated: false,
    agyProject: "",
    agyAccounts: [],
    agyMode: "alternar",
    agyResume: false,
    agyTimer: null,
    agyBusy: false,
    // Settings
    chainRole: "codigo",
    es: null,
  };

  const PRESETS = [
    { kind: "anthropic", label: "Claude API", color: "#d97757", name: "Claude API", baseUrl: "https://api.anthropic.com/v1",
      defaultModel: "claude-opus-5-5", models: ["claude-opus-5-5", "claude-sonnet-5-5"], hint: "Use uma chave do Claude Console. Esta conexão usa a cota da API." },
    { kind: "gemini", label: "Google Gemini", color: "#4285f4", name: "Gemini", baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai", defaultModel: "gemini-2.5-flash", models: ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite"], hint: "Pegue sua chave gratuita ou paga em aistudio.google.com/apikey. Você pode adicionar quantas contas quiser (Gemini 1, 2, 3...)." },
    { kind: "openrouter", label: "OpenRouter", color: "#a855f7", name: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1", defaultModel: "anthropic/claude-sonnet-4", models: ["anthropic/claude-sonnet-4", "openai/gpt-4.1", "google/gemini-2.5-pro", "deepseek/deepseek-chat-v3-0324:free"], hint: "Uma única chave acessa Claude, GPT, Gemini, DeepSeek e modelos gratuitos." },
    { kind: "openai", label: "OpenAI", color: "#10b981", name: "OpenAI", baseUrl: "https://api.openai.com/v1", defaultModel: "gpt-4.1", models: ["gpt-4.1", "gpt-4.1-mini", "o4-mini"], hint: "Chave da API em platform.openai.com." },
    { kind: "groq", label: "Groq", color: "#f97316", name: "Groq", baseUrl: "https://api.groq.com/openai/v1", defaultModel: "llama-3.3-70b-versatile", models: ["llama-3.3-70b-versatile", "qwen/qwen3-32b"], hint: "Inferência ultrarrápida com cota gratuita em console.groq.com." },
    { kind: "deepseek", label: "DeepSeek", color: "#3b82f6", name: "DeepSeek", baseUrl: "https://api.deepseek.com/v1", defaultModel: "deepseek-chat", models: ["deepseek-chat", "deepseek-reasoner"], hint: "Chave da API em platform.deepseek.com." },
    { kind: "ollama", label: "Ollama Local", color: "#e5e7eb", name: "Ollama Local", baseUrl: "http://127.0.0.1:11434/v1", defaultModel: "gemma3:4b", models: ["gemma3:4b", "qwen2.5-coder:7b", "llama3.1:8b"], noKey: true, hint: "Roda offline na sua máquina sem custo. Precisa do Ollama aberto." },
    { kind: "openai", label: "Gateway / Custom", color: "#94a3b8", name: "Gateway Local", baseUrl: "http://127.0.0.1:8080/v1", defaultModel: "", models: [], hint: "Conecte qualquer gateway local ou endpoint compatível com OpenAI (/chat/completions)." },
  ];

  // ---------- API & Helpers ----------

  async function api(path, opts = {}) {
    const headers = { "X-Central-Token": token, ...(opts.headers || {}) };
    if (opts.body && !(opts.body instanceof FormData)) {
      headers["Content-Type"] = "application/json";
      opts.body = JSON.stringify(opts.body);
    }
    const res = await fetch("/api" + path, { ...opts, headers });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!res.ok) throw new Error((data && data.error) || `Erro ${res.status}`);
    return data;
  }

  function mediaUrl(pid, fid, rel) {
    return `/api/projects/${pid}/flows/${fid}/media/${rel.split("/").map(encodeURIComponent).join("/")}?t=${encodeURIComponent(token)}`;
  }

  function esc(s) {
    return String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }

  function fmtSize(b) {
    if (b < 1024) return b + " B";
    if (b < 1048576) return (b / 1024).toFixed(1) + " KB";
    return (b / 1048576).toFixed(1) + " MB";
  }

  function relTime(iso) {
    if (!iso) return "";
    const diff = (Date.now() - new Date(iso).getTime()) / 1000;
    if (diff < 60) return "agora";
    if (diff < 3600) return Math.floor(diff / 60) + " min";
    if (diff < 86400) return Math.floor(diff / 3600) + " h";
    return new Date(iso).toLocaleDateString("pt-BR");
  }

  function toast(msg, kind = "") {
    const el = document.createElement("div");
    el.className = "toast " + kind;
    el.textContent = msg;
    document.getElementById("toasts").appendChild(el);
    setTimeout(() => el.remove(), 4200);
  }

  function openModal(html, onBind) {
    const bd = document.getElementById("modal-backdrop");
    const box = document.getElementById("modal");
    box.innerHTML = html;
    bd.classList.remove("hidden");
    const close = () => bd.classList.add("hidden");
    bd.onclick = (e) => { if (e.target === bd) close(); };
    if (onBind) onBind(box, close);
  }

  function closeStream() {
    if (state.es) { state.es.close(); state.es = null; }
    clearInterval(state.agyTimer);
    state.agyTimer = null;
  }

  // ---------- Markdown Renderer ----------

  function renderMd(raw) {
    if (!raw) return "";
    const blocks = [];
    let text = raw.replace(/```([\w-]*)\n([\s\S]*?)```/g, (_, lang, code) => {
      const i = blocks.length;
      blocks.push({ lang: lang || "texto", code: code.replace(/\n$/, "") });
      return `\u0000BLOCK${i}\u0000`;
    });
    text = esc(text);
    const lines = text.split("\n");
    const out = [];
    let inUl = false, inOl = false;
    const closeLists = () => {
      if (inUl) { out.push("</ul>"); inUl = false; }
      if (inOl) { out.push("</ol>"); inOl = false; }
    };
    const inline = (s) => s
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>")
      .replace(/_([^_\s][^_]*)_/g, "<em>$1</em>")
      .replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');

    for (const line of lines) {
      const bm = line.match(/^\u0000BLOCK(\d+)\u0000$/);
      if (bm) { closeLists(); out.push(line); continue; }
      const h = line.match(/^(#{1,3})\s+(.*)$/);
      if (h) { closeLists(); out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`); continue; }
      const ul = line.match(/^\s*[-*•]\s+(.*)$/);
      if (ul) {
        if (inOl) { out.push("</ol>"); inOl = false; }
        if (!inUl) { out.push("<ul>"); inUl = true; }
        out.push(`<li>${inline(ul[1])}</li>`); continue;
      }
      const ol = line.match(/^\s*\d+\.\s+(.*)$/);
      if (ol) {
        if (inUl) { out.push("</ul>"); inUl = false; }
        if (!inOl) { out.push("<ol>"); inOl = true; }
        out.push(`<li>${inline(ol[1])}</li>`); continue;
      }
      closeLists();
      if (line.trim() === "") out.push("");
      else out.push(`<p>${inline(line)}</p>`);
    }
    closeLists();
    return out.join("\n").replace(/\u0000BLOCK(\d+)\u0000/g, (_, idx) => {
      const b = blocks[Number(idx)];
      return `<div class="code-block"><div class="code-head"><span>${esc(b.lang)}</span><button class="btn btn-ghost btn-sm copy-code" data-code="${esc(b.code)}">Copiar</button></div><pre><code>${esc(b.code)}</code></pre></div>`;
    });
  }

  // ---------- Line Diff ----------

  function computeDiff(before, after) {
    const a = (before ?? "").split("\n");
    const b = (after ?? "").split("\n");
    if (before == null) return b.map((l) => ({ type: "add", text: "+ " + l }));
    if (a.length * b.length > 250000) {
      return [...a.slice(0, 60).map((l) => ({ type: "del", text: "- " + l })), ...b.slice(0, 120).map((l) => ({ type: "add", text: "+ " + l }))];
    }
    const m = a.length, n = b.length;
    const dp = Array.from({ length: m + 1 }, () => new Uint16Array(n + 1));
    for (let i = m - 1; i >= 0; i--)
      for (let j = n - 1; j >= 0; j--)
        dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    const res = [];
    let i = 0, j = 0;
    while (i < m && j < n) {
      if (a[i] === b[j]) { res.push({ type: "ctx", text: "  " + a[i] }); i++; j++; }
      else if (dp[i + 1][j] >= dp[i][j + 1]) { res.push({ type: "del", text: "- " + a[i++] }); }
      else { res.push({ type: "add", text: "+ " + b[j++] }); }
    }
    while (i < m) res.push({ type: "del", text: "- " + a[i++] });
    while (j < n) res.push({ type: "add", text: "+ " + b[j++] });
    return res;
  }

  // ---------- Bootstrap & State Sync ----------

  async function refreshState() {
    const [st, rec] = await Promise.all([api("/state"), api("/recent")]);
    state.projects = st.projects || [];
    state.settings = st.settings;
    state.cooldowns = st.cooldowns || [];
    state.pool = st.pool || [];
    state.ffmpeg = st.ffmpeg;
    state.version = st.version;
    state.lab = st.laboratorio;
    state.dataDir = st.dataDir;
    state.recent = rec || [];
    if (state.projects.length > 0 && state.expanded.size === 0) state.expanded.add(state.projects[0].id);
    await Promise.all(state.projects.slice(0, 12).map((p) => loadProjectDetails(p.id, true)));
    renderSidebar();
  }

  async function refreshPool() {
    try {
      const r = await api("/pool");
      state.cooldowns = r.cooldowns || [];
      state.pool = r.pool || [];
      renderSidebar();
      if (state.view === "settings") renderSettings();
    } catch { /* ignore */ }
  }

  async function loadProjectDetails(pid, silent = false) {
    try {
      const data = await api(`/projects/${pid}`);
      state.projectCache[pid] = data;
      if (!silent) renderSidebar();
    } catch { /* ignored */ }
  }

  function activeProviders() {
    return (state.settings?.providers || []).filter((p) => p.enabled && !["ensaio", "google-personal"].includes(p.kind) &&
      !["cli://agy", "cli://gemini"].includes(p.baseUrl) &&
      (p.kind === "cli" || p.kind === "ollama" || p.hasKey || p.baseUrl.includes("127.0.0.1") || p.baseUrl.includes("localhost")));
  }

  function modeIcon(mode) {
    return mode === "equipe" ? "👥" : mode === "agente" ? "💻" : "💬";
  }

  // ---------- Sidebar ----------

  function renderSidebar() {
    document.querySelectorAll(".nav-item").forEach((el) => el.classList.toggle("active", el.dataset.nav === state.view));
    const activeCount = activeProviders().length;
    const badge = document.getElementById("accounts-badge");
    if (activeCount === 0) {
      badge.className = "nav-badge warn"; badge.textContent = "Conectar";
    } else if (state.cooldowns.length > 0) {
      badge.className = "nav-badge warn"; badge.textContent = `${activeCount} IA · ${state.cooldowns.length} pausa`;
    } else {
      badge.className = "nav-badge ok"; badge.textContent = activeCount + (activeCount === 1 ? " IA" : " IAs");
    }

    const tree = document.getElementById("project-tree");
    if (state.projects.length === 0) {
      tree.innerHTML = `<div class="empty-state" style="padding:20px 10px">Nenhum projeto ainda.<br>Comece pela tela inicial!</div>`;
    } else {
      tree.innerHTML = state.projects.map((p) => {
        const open = state.expanded.has(p.id) || state.pid === p.id;
        const details = state.projectCache[p.id];
        const threads = details?.threads || [];
        const flows = details?.flows || [];
        return `
          <div class="proj-group ${open ? "open" : ""} ${state.pid === p.id ? "active-proj" : ""}" data-pid="${p.id}">
            <div class="proj-row" data-toggle-proj="${p.id}">
              <svg class="chev" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"><polyline points="9 18 15 12 9 6"/></svg>
              <span class="proj-name" title="${esc(p.customPath ? p.name + " (" + p.customPath + ")" : p.name)}">${p.customPath ? "📂 " : ""}${esc(p.name)}</span>
              <div class="proj-actions">
                <button class="mini-act" data-new-chat="${p.id}" title="Nova conversa / equipe">💬</button>
                <button class="mini-act" data-new-flow="${p.id}" title="Novo vídeo / minissérie (Flow)">🎬</button>
                <button class="mini-act" data-del-proj="${p.id}" title="Apagar projeto">✕</button>
              </div>
            </div>
            <div class="proj-children">
              ${threads.map((t) => `
                <button class="tree-item ${state.view === "thread" && state.tid === t.id ? "active" : ""}" data-open-thread="${p.id}:${t.id}">
                  ${t.running ? '<span class="pulse-dot"></span>' : `<span>${modeIcon(t.mode)}</span>`}
                  <span class="tree-title" title="${esc(t.title)}">${esc(t.title)}</span>
                  <span class="tree-del" data-del-thread="${p.id}:${t.id}" title="Apagar conversa">✕</span>
                </button>`).join("")}
              ${flows.map((f) => `
                <button class="tree-item flow-item ${state.view === "flow" && state.fid === f.id ? "active" : ""}" data-open-flow="${p.id}:${f.id}">
                  <span>🎬</span>
                  <span class="tree-title" title="${esc(f.title)}">${esc(f.title)}</span>
                  <span class="tree-del" data-del-flow="${p.id}:${f.id}" title="Apagar Flow">✕</span>
                </button>`).join("")}
              ${threads.length === 0 && flows.length === 0 ? `<div style="padding:4px 8px;font-size:12px;color:var(--text-3)">Sem itens ainda</div>` : ""}
            </div>
          </div>`;
      }).join("");
    }

    const inFlight = state.pool.reduce((a, x) => a + (x.inFlight || 0), 0);
    const foot = document.getElementById("sidebar-foot");
    foot.innerHTML = `
      <span>${state.ffmpeg ? "FFmpeg OK" : "Sem FFmpeg"}</span>
      <span>${inFlight > 0 ? `⚡ ${inFlight} rodando` : state.cooldowns.length > 0 ? `⏳ ${state.cooldowns.length} pausa` : `v${esc(state.version || "")}`}</span>`;

    tree.querySelectorAll("[data-toggle-proj]").forEach((el) => {
      el.onclick = (e) => {
        if (e.target.closest(".proj-actions")) return;
        const pid = el.dataset.toggleProj;
        if (state.expanded.has(pid)) state.expanded.delete(pid);
        else { state.expanded.add(pid); loadProjectDetails(pid); }
        renderSidebar();
      };
    });
    tree.querySelectorAll("[data-new-chat]").forEach((el) => {
      el.onclick = async (e) => {
        e.stopPropagation();
        const pid = el.dataset.newChat;
        const t = await api(`/projects/${pid}/threads`, { method: "POST", body: { mode: "agente" } });
        state.expanded.add(pid);
        await loadProjectDetails(pid, true);
        openThread(pid, t.id);
      };
    });
    tree.querySelectorAll("[data-new-flow]").forEach((el) => {
      el.onclick = (e) => { e.stopPropagation(); showNewFlowModal(el.dataset.newFlow); };
    });
    tree.querySelectorAll("[data-del-proj]").forEach((el) => {
      el.onclick = async (e) => {
        e.stopPropagation();
        const pid = el.dataset.delProj;
        const p = state.projects.find((x) => x.id === pid);
        if (!confirm(`Mover o projeto “${p?.name}” para a lixeira?`)) return;
        try {
          await api(`/projects/${pid}`, { method: "DELETE" });
          if (state.pid === pid) goHome();
          await refreshState();
          toast("Projeto movido para a lixeira.");
        } catch (err) { toast(err.message, "err"); }
      };
    });
    tree.querySelectorAll("[data-open-thread]").forEach((el) => {
      el.onclick = (e) => {
        if (e.target.closest(".tree-del")) return;
        const [pid, tid] = el.dataset.openThread.split(":");
        openThread(pid, tid);
      };
    });
    tree.querySelectorAll("[data-del-thread]").forEach((el) => {
      el.onclick = async (e) => {
        e.stopPropagation();
        const [pid, tid] = el.dataset.delThread.split(":");
        if (!confirm("Apagar esta conversa?")) return;
        await api(`/projects/${pid}/threads/${tid}`, { method: "DELETE" });
        if (state.tid === tid) goHome();
        await loadProjectDetails(pid, true);
        renderSidebar();
      };
    });
    tree.querySelectorAll("[data-open-flow]").forEach((el) => {
      el.onclick = (e) => {
        if (e.target.closest(".tree-del")) return;
        const [pid, fid] = el.dataset.openFlow.split(":");
        openFlow(pid, fid);
      };
    });
    tree.querySelectorAll("[data-del-flow]").forEach((el) => {
      el.onclick = async (e) => {
        e.stopPropagation();
        const [pid, fid] = el.dataset.delFlow.split(":");
        if (!confirm("Apagar este storyboard do Flow?")) return;
        await api(`/projects/${pid}/flows/${fid}`, { method: "DELETE" });
        if (state.fid === fid) goHome();
        await loadProjectDetails(pid, true);
        renderSidebar();
      };
    });
  }

  // ---------- Navigation ----------

  function setHash(h) { if (location.hash !== h) history.replaceState(null, "", h); }

  function goHome() {
    closeStream();
    state.view = "home"; state.tid = null; state.fid = null;
    setHash("#/");
    renderSidebar();
    renderHome();
  }

  async function goSettings() {
    closeStream();
    state.view = "settings"; state.tid = null; state.fid = null;
    setHash("#/contas");
    renderSidebar();
    renderSettings();
    await Promise.all([refreshPool(), runDiscover(), refreshAgyPool()]);
    if (state.view === "settings" && !state.agyTimer)
      state.agyTimer = setInterval(() => {
        if (state.agyPool?.ready && !state.agyBusy && !document.activeElement?.closest("#antigravity-panel input, #antigravity-panel select"))
          refreshAgyPool();
      }, 2500);
  }

  async function runDiscover() {
    try {
      state.discovered = await api("/discover");
      if (state.view === "settings") renderSettings();
    } catch { /* ignore */ }
  }

  async function openThread(pid, tid) {
    closeStream();
    state.view = "thread"; state.pid = pid; state.tid = tid; state.fid = null;
    state.expanded.add(pid);
    setHash(`#/p/${pid}/c/${tid}`);
    renderSidebar();
    const main = document.getElementById("main");
    main.innerHTML = `<div class="empty-state"><div class="spinner" style="margin:0 auto 10px"></div>Carregando conversa…</div>`;
    try {
      const [data, files] = await Promise.all([api(`/projects/${pid}/threads/${tid}`), api(`/projects/${pid}/files`)]);
      state.thread = data.thread;
      state.live = data.live || { running: false };
      state.files = files || [];
      renderThread();
      connectThreadStream(pid, tid);
    } catch (err) {
      toast(err.message, "err");
      goHome();
    }
  }

  async function openFlow(pid, fid) {
    closeStream();
    state.view = "flow"; state.pid = pid; state.fid = fid; state.tid = null;
    state.expanded.add(pid);
    setHash(`#/p/${pid}/f/${fid}`);
    renderSidebar();
    const main = document.getElementById("main");
    main.innerHTML = `<div class="empty-state"><div class="spinner" style="margin:0 auto 10px"></div>Abrindo estúdio Flow…</div>`;
    try {
      state.flow = await api(`/projects/${pid}/flows/${fid}`);
      renderFlow();
      connectFlowStream(pid, fid);
    } catch (err) {
      toast(err.message, "err");
      goHome();
    }
  }

  // ---------- Home View ----------

  function renderHome() {
    const main = document.getElementById("main");
    const hasAi = activeProviders().length > 0;
    const isFlow = state.homeMode === "flow";
    const isTeam = state.homeMode === "equipe";

    main.innerHTML = `
      <div class="home-wrap">
        <div class="home-inner">
          <div class="home-hero">
            <h1>O que vamos criar agora?</h1>
            <p>Desenvolva sistemas com um agente ou equipe em paralelo, ou produza minisséries no Flow — distribuindo tudo pelo seu Pool de contas.</p>
          </div>

          ${!hasAi ? `
            <div class="setup-banner">
              <div>
                <strong>Conecte suas contas de IA em 30 segundos</strong>
                <span>Adicione várias chaves Gemini, OpenRouter, OpenAI, Groq, Ollama ou um gateway local compatível com OpenAI.</span>
              </div>
              <button class="btn btn-primary" id="banner-setup">Conectar contas →</button>
            </div>` : ""}

          <div class="hero-box">
            <div class="mode-pills">
              <button class="mode-pill ${state.homeMode === "agente" ? "active" : ""}" data-hmode="agente">💻 Programação</button>
              <button class="mode-pill team ${isTeam ? "active" : ""}" data-hmode="equipe">👥 Equipe (Paralelo)</button>
              <button class="mode-pill ${state.homeMode === "conversa" ? "active" : ""}" data-hmode="conversa">💬 Conversa</button>
              <button class="mode-pill flow ${isFlow ? "active" : ""}" data-hmode="flow">🎬 Flow · Vídeo e Série</button>
            </div>

            ${state.homeMode === "agente" ? '<section id="home-execution" class="continuity-options"></section>' : ""}
            <textarea id="home-prompt" class="hero-textarea" placeholder="${isFlow
              ? "Descreva a ideia do vídeo ou minissérie… Ex.: Minissérie cyberpunk de 3 episódios em São Paulo chuvosa sobre um entregador que encontra um chip secreto."
              : isTeam
                ? "Peça um projeto grande… O Coordenador quebra em subtarefas e dispara vários workers em paralelo usando suas contas do Pool!"
                : state.homeMode === "agente"
                  ? "Descreva o sistema, site ou tarefa… Ex.: Crie um sistema completo de orçamentos com cadastro de clientes, itens, cálculo automático e prévia ao vivo."
                  : "Pergunte qualquer coisa, peça brainstorm ou planeje um projeto…"}"></textarea>

            ${isFlow ? `
              <div class="flow-inline-opts">
                <label>Formato
                  <select id="hf-format"><option value="video">Vídeo único</option><option value="serie">Minissérie</option></select>
                </label>
                <label id="hf-ep-wrap" class="hidden">Episódios
                  <input id="hf-episodes" type="number" min="2" max="12" value="3">
                </label>
                <label>Cenas / ep.
                  <input id="hf-scenes" type="number" min="1" max="12" value="4">
                </label>
                <label>Duração da cena
                  <select id="hf-seconds"><option value="4">4 s</option><option value="6">6 s</option><option value="8" selected>8 s</option></select>
                </label>
                <label>Proporção
                  <select id="hf-aspect"><option value="16:9">16:9 (Horizontal)</option><option value="9:16">9:16 (Vertical / Reels)</option></select>
                </label>
                <label>Estilo visual
                  <input id="hf-style" placeholder="Ex.: cinematográfico 35mm">
                </label>
              </div>` : ""}

            <div class="hero-bar">
              <div class="hero-proj">
                <span>Projeto:</span>
                <select id="home-project">
                  <option value="">+ Criar projeto automático</option>
                  ${state.projects.map((p) => `<option value="${p.id}" ${state.homeProject === p.id ? "selected" : ""}>${esc(p.name)}</option>`).join("")}
                </select>
                <button class="btn btn-sm btn-ghost" id="home-link-folder" title="Abrir uma pasta existente do seu computador">📂 Pasta local</button>
              </div>
              <button class="btn ${isFlow ? "btn-flow" : "btn-primary"}" id="home-send">
                ${isFlow ? "Criar storyboard →" : isTeam ? "Lançar equipe →" : state.homeMode === "agente" ? "Criar sistema →" : "Enviar →"}
              </button>
            </div>
          </div>

          <div class="suggestions">
            <button class="sugg-card" data-sugg="equipe" data-text="Crie um sistema web completo de gestão de oficina e orçamentos em arquivos separados (index.html, styles.css, store.js, app.js): dashboard com métricas, cadastro de clientes, construtor de orçamentos com itens e desconto, status (aberto/aprovado/recusado), busca e exportação de proposta pronta para impressão.">
              <span class="sugg-tag" style="color:#d8b4fe">Equipe · Paralelo</span>
              <strong>Sistema Completo em Equipe</strong>
              <span>Coordenador divide HTML, CSS, dados e lógica entre workers paralelos.</span>
            </button>
            <button class="sugg-card" data-sugg="agente" data-text="Crie uma landing page de alta conversão para uma cafeteria artesanal com animações suaves, cardápio filtrável e carrinho de pedidos.">
              <span class="sugg-tag">Agente · Rápido</span>
              <strong>Site Interativo</strong>
              <span>Prévia rodando ao vivo ao lado do chat + diff e Git integrados.</span>
            </button>
            <button class="sugg-card" data-sugg="flow" data-text="Um faroleiro solitário em uma ilha tempestuosa recebe uma transmissão de rádio misteriosa vinda do fundo do mar. Clima de suspense cinematográfico.">
              <span class="sugg-tag flow">Flow · Minissérie</span>
              <strong>Minissérie com Continuidade</strong>
              <span>Bíblia de personagens, múltiplas tomadas, trilha com ducking e xfade.</span>
            </button>
          </div>

          ${state.recent.length > 0 ? `
            <section class="recent-section">
              <h2>Continuar de onde parei</h2>
              <div class="recent-grid">
                ${state.recent.map((r) => `
                  <button class="recent-card" data-rec="${r.kind}:${r.projectId}:${r.id}">
                    <div class="recent-icon ${r.kind === "flow" ? "flow" : ""}">${r.kind === "flow" ? "🎬" : modeIcon(r.mode)}</div>
                    <div class="recent-meta">
                      <strong>${esc(r.title)}</strong>
                      <small>${esc(r.project)} · ${relTime(r.updated)}</small>
                    </div>
                    ${r.running ? '<span class="pulse-dot"></span>' : ""}
                  </button>`).join("")}
              </div>
            </section>` : ""}
        </div>
      </div>`;

    document.getElementById("banner-setup")?.addEventListener("click", goSettings);
    document.getElementById("home-link-folder")?.addEventListener("click", () => showNewProjectModal(true));
    renderExecutionOptions();
    main.querySelectorAll("[data-hmode]").forEach((btn) => {
      btn.onclick = () => { state.homeMode = btn.dataset.hmode; renderHome(); };
    });
    const fmtSel = document.getElementById("hf-format");
    if (fmtSel) fmtSel.onchange = () => document.getElementById("hf-ep-wrap").classList.toggle("hidden", fmtSel.value !== "serie");
    const promptEl = document.getElementById("home-prompt");
    promptEl.focus();
    promptEl.onkeydown = (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submitHome(); } };
    document.getElementById("home-send").onclick = submitHome;

    main.querySelectorAll(".sugg-card").forEach((c) => {
      c.onclick = () => {
        state.homeMode = c.dataset.sugg;
        renderHome();
        const ta = document.getElementById("home-prompt");
        ta.value = c.dataset.text;
        ta.focus();
      };
    });
    main.querySelectorAll("[data-rec]").forEach((c) => {
      c.onclick = () => {
        const [kind, pid, id] = c.dataset.rec.split(":");
        if (kind === "flow") openFlow(pid, id);
        else openThread(pid, id);
      };
    });
  }

  function renderExecutionOptions() {
    const slot = document.getElementById("home-execution");
    if (!slot) return;
    const mode = state.homeExecution;
    const kind = mode === "assistido" ? "google-personal" : "ensaio";
    const profiles = (state.settings?.providers || []).filter((p) => p.enabled && p.kind === kind);
    const ids = state.homeAccounts.filter((id) => profiles.some((p) => p.id === id));
    state.homeAccounts = ids;
    const models = activeProviders().filter((p) => p.kind !== "cli" &&
      (p.baseUrl.startsWith("https://") && p.hasKey || /^https?:\/\/(?:localhost|127\.0\.0\.1)(?::\d+)?(?:\/|$)/i.test(p.baseUrl)))
      .flatMap((p) => [...new Set([p.defaultModel, ...(p.models || [])])].filter(Boolean)
        .map((model) => ({ providerId: p.id, name: p.name, model })));
    const sameRoute = (a, b) => a.providerId === b.providerId && a.model === b.model;
    state.homeRoutes = state.homeRoutes.filter((route) => models.some((model) => sameRoute(route, model)));
    const routes = state.homeRoutes;
    slot.innerHTML = `
      <label>Execução
        <select class="select" id="execution-mode">
          <option value="revisao" ${mode === "revisao" ? "selected" : ""}>Planejar, revisar e executar</option>
          <option value="" ${!mode ? "selected" : ""}>Pool de modelos configurado</option>
          <option value="assistido" ${mode === "assistido" ? "selected" : ""}>Google pessoal · assistido</option>
          <option value="fixa" ${mode === "fixa" ? "selected" : ""} ${state.lab ? "" : "disabled"}>Fixar uma conta · ensaio</option>
          <option value="alternar" ${mode === "alternar" ? "selected" : ""} ${state.lab ? "" : "disabled"}>Alternar ao atingir limite · ensaio</option>
        </select>
      </label>
      <button type="button" class="btn btn-sm btn-ghost" id="home-agy-pool">Usar Antigravity Account Pool</button>
      ${mode === "revisao" ? `
        <div class="continuity-accounts">
          ${routes.length ? `<ol aria-label="Ordem dos modelos">${routes.map((route, index) => `
            <li>${esc(route.name)} · ${esc(route.model)} <button class="btn btn-sm" type="button" data-remove-route="${index}">Remover</button></li>`).join("")}</ol>` : ""}
          <label>${routes.length ? "Próximo modelo na ordem" : "Primeiro modelo"}
            <select class="select" id="execution-route" ${models.length ? "" : "disabled"}>
              <option value="">Selecione conexão e modelo</option>
              ${models.map((r, index) => routes.some((selected) => sameRoute(selected, r)) ? "" :
                `<option value="${index}">${esc(r.name)} · ${esc(r.model)}</option>`).join("")}
            </select>
          </label>
          ${models.length ? "" : '<button type="button" class="btn btn-sm" id="connect-api-plan">Configurar API</button>'}
        </div>
        <label class="api-consent"><input type="checkbox" id="confirm-apis" ${state.confirmApis ? "checked" : ""}>
          Autorizo o uso e eventuais custos das conexões escolhidas.</label>
        <small>A Central propõe um plano antes de editar arquivos. A troca de modelo usa essa ordem após um limite confirmado.</small>` : mode ? `
        <div class="continuity-accounts">
          ${mode === "alternar" && ids.length ? `
            <ol aria-label="Ordem das contas">${ids.map((id) => `<li>${esc(profiles.find((p) => p.id === id).name)}
              <button class="btn btn-sm" type="button" data-remove-account="${esc(id)}">Remover</button></li>`).join("")}</ol>` : ""}
          <label>${mode === "alternar" ? "Próxima conta na ordem" : "Conta"}
            <select class="select" id="execution-account">
              <option value="">Selecione um perfil</option>
              ${profiles.filter((p) => mode !== "alternar" || !ids.includes(p.id)).map((p) =>
                `<option value="${esc(p.id)}" ${mode !== "alternar" && ids[0] === p.id ? "selected" : ""}>${esc(p.name)}</option>`).join("")}
            </select>
          </label>
          ${mode === "assistido" ? '<button class="btn btn-sm" id="home-add-google" type="button">Adicionar conta Google</button>' : ""}
        </div>` : ""}
      ${mode === "revisao" ? "" : `<small>${mode === "assistido" ? "Cadastrar perfil salva o nome e o e-mail. Entre com o mesmo e-mail no Antigravity oficial e envie o pedido por lá." :
        state.lab ? "Ensaio local com identidades simuladas." : "Use o modo Google pessoal para cadastrar perfis e continuar pelo Antigravity oficial."}</small>`}`;
    slot.querySelector("#execution-mode").onchange = (event) => {
      state.homeExecution = event.target.value;
      state.homeAccounts = [];
      renderExecutionOptions();
    };
    const select = slot.querySelector("#execution-account");
    if (select) select.onchange = (event) => {
      const id = event.target.value;
      state.homeAccounts = mode === "alternar" ? (id ? [...ids, id] : ids) : (id ? [id] : []);
      renderExecutionOptions();
    };
    slot.querySelectorAll("[data-remove-account]").forEach((button) => {
      button.onclick = () => {
        state.homeAccounts = ids.filter((id) => id !== button.dataset.removeAccount);
        renderExecutionOptions();
      };
    });
    slot.querySelector("#execution-route")?.addEventListener("change", (event) => {
      if (event.target.value !== "" && routes.length < 10) routes.push(models[Number(event.target.value)]);
      renderExecutionOptions();
    });
    slot.querySelectorAll("[data-remove-route]").forEach((button) => {
      button.onclick = () => { routes.splice(Number(button.dataset.removeRoute), 1); renderExecutionOptions(); };
    });
    slot.querySelector("#confirm-apis")?.addEventListener("change", (event) => { state.confirmApis = event.target.checked; });
    slot.querySelector("#connect-api-plan")?.addEventListener("click", goSettings);
    slot.querySelector("#home-agy-pool").onclick = goSettings;
    slot.querySelector("#home-add-google")?.addEventListener("click", showGoogleAccountModal);
  }

  async function submitHome() {
    const prompt = document.getElementById("home-prompt").value.trim();
    if (!prompt) { toast("Escreva o que você quer criar.", "err"); return; }
    const sendBtn = document.getElementById("home-send");
    const execution = state.homeMode === "agente" ? state.homeExecution : "";
    const accounts = execution ? state.homeAccounts : [];
    if (execution === "revisao" && (!state.homeRoutes.length || !state.confirmApis)) {
      toast("Selecione os modelos e confirme o uso das conexões.", "err");
      return;
    }
    if (execution && execution !== "revisao" && accounts.length < (execution === "alternar" ? 2 : 1)) {
      toast("Selecione as contas antes de enviar o pedido.", "err");
      return;
    }
    sendBtn.disabled = true;
    try {
      let pid = document.getElementById("home-project").value;
      state.homeProject = pid;
      if (!pid) {
        const name = prompt.split("\n")[0].trim().slice(0, 40) || "Novo projeto";
        const p = await api("/projects", { method: "POST", body: { name, kind: state.homeMode === "flow" ? "flow" : "codigo" } });
        pid = p.id;
      }
      if (state.homeMode === "flow") {
        const f = await api(`/projects/${pid}/flows`, {
          method: "POST",
          body: {
            prompt,
            format: document.getElementById("hf-format")?.value || "video",
            episodes: Number(document.getElementById("hf-episodes")?.value || 3),
            scenes: Number(document.getElementById("hf-scenes")?.value || 4),
            seconds: Number(document.getElementById("hf-seconds")?.value || 8),
            aspect: document.getElementById("hf-aspect")?.value || "16:9",
            style: document.getElementById("hf-style")?.value || "",
          },
        });
        await refreshState();
        openFlow(pid, f.id);
      } else {
        const t = await api(`/projects/${pid}/threads`, { method: "POST", body: { text: prompt, mode: state.homeMode, execution, accounts,
          routes: execution === "revisao" ? state.homeRoutes : [], confirmApis: state.confirmApis } });
        if (execution === "revisao") state.confirmApis = false;
        await refreshState();
        openThread(pid, t.id);
      }
    } catch (err) {
      toast(err.message, "err");
      sendBtn.disabled = false;
    }
  }

  // ---------- Thread / Codex / Equipe View ----------

  function renderThread() {
    const t = state.thread;
    const proj = state.projects.find((p) => p.id === state.pid);
    const main = document.getElementById("main");
    const route = state.live.route;
    const uniqueChanges = groupedChanges(t.changes || []);
    main.innerHTML = `
      <header class="topbar">
        <div class="topbar-left">
          <span class="crumb-pill" title="${esc(proj?.customPath || "Workspace gerenciado")}">${proj?.customPath ? "📂 " : ""}${esc(proj?.name || "Projeto")}</span>
          <span class="topbar-title" id="thread-title" title="Clique para renomear">${esc(t.title)}</span>
        </div>
        <div class="topbar-right">
          ${t.continuation || t.reviewedWork ? "" : `<select id="pin-account" class="pin-select ${t.pinnedProviderId ? "pinned" : ""}" title="Seleção do pool ou de uma conexão fixa">
            <option value="">⚡ Auto (Pool)</option>
            ${activeProviders().map((p) => `<option value="${p.id}" ${t.pinnedProviderId === p.id ? "selected" : ""}>🔒 ${esc(p.name)}</option>`).join("")}
          </select>
          <span class="route-pill ${state.live.running ? "live" : ""}" id="route-pill">
            ${route ? `⚡ ${esc(route.providerName)} · ${esc(route.model)}` : (lastModelLabel(t) || "Pronto")}
          </span>
          <div class="seg" title="Modo da conversa">
            <button data-set-mode="agente" class="${t.mode === "agente" ? "active" : ""}">Agente</button>
            <button data-set-mode="equipe" class="${t.mode === "equipe" ? "active" : ""}">Equipe</button>
            <button data-set-mode="conversa" class="${t.mode === "conversa" ? "active" : ""}">Conversa</button>
          </div>`}
          <button class="btn btn-sm ${state.showPanel ? "btn-primary" : ""}" id="toggle-panel">
            ${state.showPanel ? "Ocultar" : "Painel"}
          </button>
        </div>
      </header>

      <div class="codex-split">
        <section class="chat-pane">
          <div id="team-board-slot"></div>
          <div id="continuity-slot"></div>
          <div id="reviewed-slot"></div>
          <div class="messages" id="messages">
            <div class="messages-inner" id="messages-inner"></div>
          </div>
          ${t.continuation ? "" : `<div class="composer-wrap">
            <div class="composer">
              <textarea id="chat-input" placeholder="${t.mode === "equipe"
                ? "Descreva a entrega grande para a equipe dividir e executar em paralelo…"
                : t.mode === "agente"
                  ? "Peça para criar, alterar, corrigir ou testar algo no projeto…"
                  : "Escreva sua mensagem…"}"></textarea>
              <div class="composer-bar">
                <span class="composer-hint">${t.reviewedWork ? "Envie uma instrução para propor ou revisar o plano." :
                  t.pinnedProviderId ? "🔒 Conta travada (sem troca automática)" : "⚡ Pool ativo · Enter envia · Shift+Enter quebra linha"}</span>
                <div style="display:flex;gap:8px">
                  <button class="btn btn-danger btn-sm ${state.live.running ? "" : "hidden"}" id="btn-stop">■ Parar</button>
                  ${t.reviewedWork ? `<button class="btn btn-sm ${state.live.running ? "hidden" : ""}" id="btn-ask">Perguntar</button>` : ""}
                  <button class="btn btn-primary btn-sm ${state.live.running ? "hidden" : ""}" id="btn-send">Enviar ↑</button>
                </div>
              </div>
            </div>
          </div>
          `}
        </section>

        <aside class="result-pane ${state.showPanel ? "" : "hidden"}" id="result-pane">
          <div class="panel-tabs">
            <button class="panel-tab ${state.panelTab === "preview" ? "active" : ""}" data-ptab="preview">Prévia</button>
            <button class="panel-tab ${state.panelTab === "files" ? "active" : ""}" data-ptab="files">Arquivos (${state.files.length})</button>
            <button class="panel-tab ${state.panelTab === "diff" ? "active" : ""}" data-ptab="diff">Diff (${uniqueChanges.length})</button>
            <button class="panel-tab ${state.panelTab === "git" ? "active" : ""}" data-ptab="git">Git</button>
            <div class="panel-tools">
              <button class="icon-btn" id="btn-open-folder" title="Abrir pasta no Windows Explorer">📁</button>
              <button class="icon-btn" id="btn-download-zip" title="Baixar projeto (.zip)">⬇</button>
            </div>
          </div>
          <div class="panel-body" id="panel-body"></div>
        </aside>
      </div>`;

    document.getElementById("thread-title").onclick = async () => {
      const name = prompt("Novo título da conversa:", t.title);
      if (!name || !name.trim()) return;
      await api(`/projects/${state.pid}/threads/${state.tid}`, { method: "PATCH", body: { title: name.trim() } });
      t.title = name.trim();
      document.getElementById("thread-title").textContent = t.title;
      loadProjectDetails(state.pid);
    };

    const pin = document.getElementById("pin-account");
    if (pin) pin.onchange = async (e) => {
      const pidVal = e.target.value;
      await api(`/projects/${state.pid}/threads/${state.tid}`, { method: "PATCH", body: { pinnedProviderId: pidVal, pinnedModel: "" } });
      t.pinnedProviderId = pidVal || null;
      renderThread();
      toast(pidVal ? "Conta travada para esta conversa (sem troca silenciosa)." : "Voltou para rotação automática no Pool.", "ok");
    };

    main.querySelectorAll("[data-set-mode]").forEach((btn) => {
      btn.onclick = async () => {
        const mode = btn.dataset.setMode;
        await api(`/projects/${state.pid}/threads/${state.tid}`, { method: "PATCH", body: { mode } });
        t.mode = mode;
        renderThread();
      };
    });

    document.getElementById("toggle-panel").onclick = () => { state.showPanel = !state.showPanel; renderThread(); };

    main.querySelectorAll("[data-ptab]").forEach((btn) => {
      btn.onclick = () => {
        state.panelTab = btn.dataset.ptab;
        main.querySelectorAll("[data-ptab]").forEach((x) => x.classList.toggle("active", x.dataset.ptab === state.panelTab));
        renderRightPanel();
      };
    });

    document.getElementById("btn-open-folder").onclick = () =>
      api(`/projects/${state.pid}/open-folder`, { method: "POST", body: {} }).catch((e) => toast(e.message, "err"));
    document.getElementById("btn-download-zip").onclick = () =>
      window.open(`/api/projects/${state.pid}/download?t=${encodeURIComponent(token)}`, "_blank");

    const input = document.getElementById("chat-input");
    if (input) {
      input.onkeydown = (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendChat(); } };
      document.getElementById("btn-send").onclick = () => sendChat();
      document.getElementById("btn-ask")?.addEventListener("click", () => sendChat(true));
      document.getElementById("btn-stop").onclick = () => api(`/projects/${state.pid}/threads/${state.tid}/stop`, { method: "POST" });
    }

    renderContinuity();
    renderReviewed();
    renderTeamBoard();
    renderMessages(true);
    if (state.showPanel) renderRightPanel();
  }

  function renderReviewed() {
    const w = state.thread?.reviewedWork;
    const slot = document.getElementById("reviewed-slot");
    if (!w || !slot) return;
    const selected = w.routes[w.active];
    const status = { planejando: "Preparando plano", consultando: "Respondendo pergunta", revisao: "Aguardando revisão", executando: "Executando plano",
      "aguardando-cota": "Aguardando cota", pausado: "Pausado", conciliar: "Conferir efeito", concluido: "Concluído" };
    const path = `/projects/${state.pid}/threads/${state.tid}/plan`;
    const opened = slot.querySelector("details")?.open ?? w.state === "revisao";
    slot.innerHTML = `
      <section class="continuity-checkpoint" aria-label="Plano de trabalho">
        <div class="section-head"><strong>Plano de trabalho</strong><span class="pill" id="reviewed-status">${esc(status[w.state] || w.state)}</span></div>
        <p><b>Modelo ativo</b> ${esc(selected.name)} · ${esc(selected.model)}</p>
        <p><b>Ordem</b> ${esc(w.routes.map((r) => r.model).join(" → "))}</p>
        ${w.wake ? `<p><b>Próxima verificação</b> ${esc(new Date(w.wake).toLocaleString("pt-BR"))}</p>` : ""}
        ${w.issue ? `<p role="status">${esc(w.issue)}</p>` : ""}
        ${w.plan ? `<details ${opened ? "open" : ""}><summary>${w.approved ? "Plano aprovado" : "Proposta de plano"}</summary>
          <div class="msg-body">${renderMd(w.plan)}</div></details>` : ""}
        <div class="continuity-actions">
          ${w.state === "revisao" ? `<button type="button" class="btn btn-sm btn-primary" id="approve-plan" ${state.live.running ? "disabled" : ""}>Aprovar e executar</button>` : ""}
          ${w.state === "pausado" ? '<button type="button" class="btn btn-sm" id="resume-plan">Retomar do ponto salvo</button>' : ""}
          ${w.state === "conciliar" ? '<button type="button" class="btn btn-sm" id="review-plan-effect">Registrar conferência</button>' : ""}
        </div>
      </section>`;
    const send = async (suffix, body) => {
      try { await api(`${path}/${suffix}`, { method: "POST", body }); return true; }
      catch (err) { toast(err.message, "err"); return false; }
    };
    slot.querySelector("#approve-plan")?.addEventListener("click", () => send("approve", { id: w.planId }));
    slot.querySelector("#resume-plan")?.addEventListener("click", () => send("resume", {}));
    slot.querySelector("#review-plan-effect")?.addEventListener("click", () => {
      openModal(`<h2>Conferir ação interrompida</h2><p class="modal-sub">Abra os arquivos e confira se o comando terminou antes de continuar.</p>
        <form><label for="effect-note">O que ficou gravado ou em execução?</label><textarea class="input" id="effect-note" required></textarea>
        <div class="modal-foot"><button class="btn btn-primary">Salvar conferência</button></div></form>`, (box, close) => {
        box.querySelector("form").onsubmit = async (event) => {
          event.preventDefault();
          if (await send("reconcile", { note: box.querySelector("textarea").value })) close();
        };
      });
    });
  }

  function renderContinuity() {
    const c = state.thread?.continuation;
    const slot = document.getElementById("continuity-slot");
    if (!c || !slot) return;
    const url = `/projects/${state.pid}/threads/${state.tid}`;
    const profiles = state.settings?.providers || [];
    const name = (id) => profiles.find((p) => p.id === id)?.name || id || "—";
    const statuses = { operador: "Aguardando operador", fila: "Na fila", limite: "Em pausa por cota",
      executando: "Executando", concluido: "Concluído", conciliar: "Conciliação pendente", autenticar: "Autenticação necessária", pausado: "Pausado" };
    const mode = { assistido: "Google pessoal · assistido", fixa: "Conta fixada · ensaio", alternar: "Alternância · ensaio" }[c.mode];
    const pauses = Object.values(c.pauses || {}).filter((p) => new Date(p.recheck) > new Date());
    const events = c.history || [];
    const audit = ["intenção persistida", "identidade conferida"];
    const receipt = (r) => `
      <details>
        <summary>${r.step + 1} · ${esc(r.status)} · ${esc(name(r.requested))}${r.effective ? ` · ${esc(r.effective)}` : ""}</summary>
        <p>${esc(r.next)} · Tentativa ${esc(r.attempt)}</p>
        ${r.test ? `<code>${esc(r.test)}</code>` : ""}
        ${r.result ? `<pre>${esc(r.result)}</pre>` : ""}
        ${r.after ? `<p>Arquivos alterados · ${esc([...new Set([...Object.keys(r.before || {}), ...Object.keys(r.after)])]
          .filter((file) => r.before?.[file] !== r.after[file]).join(", ") || "nenhum")}</p>` : ""}
      </details>`;
    slot.innerHTML = `
      <section class="continuity-checkpoint">
        <div class="section-head"><strong>${esc(mode)}</strong><span class="pill" id="continuity-status">${esc(statuses[c.state] || c.state)}</span></div>
        <p><b>Perfil ${c.mode === "assistido" ? "informado" : "ativo"}</b> ${esc(name(c.active))}</p>
        <p><b>Ordem</b> ${esc(c.selected.map(name).join(" → "))}</p>
        <p><b>Próximo passo</b> ${esc(c.next)}</p>
        ${c.issue ? `<p role="status">${esc(c.issue)}</p>` : ""}
        ${c.wake ? `<p><b>Próxima verificação</b> ${esc(new Date(c.wake).toLocaleString("pt-BR"))}</p>` : ""}
        ${pauses.length ? `<ul>${pauses.map((p) =>
          `<li>${esc(p.subject)} · ${esc(p.group)} · ${esc(p.window)} · ${p.reset ? esc(new Date(p.reset).toLocaleString("pt-BR")) : "reset desconhecido"}</li>`).join("")}</ul>` : ""}
        ${c.mode === "assistido" && c.state === "operador" ? `
           <div class="continuity-actions">
             <a class="btn btn-sm" href="https://antigravity.google/" target="_blank" rel="noreferrer">Abrir cliente oficial</a>
             <button class="btn btn-sm" id="download-checkpoint">Baixar contexto</button>
             <button class="btn btn-sm btn-primary" id="record-checkpoint">Registrar progresso</button>
           </div>` : ""}
        ${c.state === "conciliar" ? '<button class="btn btn-sm" id="inspect-checkpoint">Conferir arquivos da tentativa</button>' : ""}
        ${["autenticar", "pausado"].includes(c.state) ? '<button class="btn btn-sm" id="resume-checkpoint">Verificar conexão e retomar</button>' : ""}
        ${["fila", "limite", "executando"].includes(c.state) ? '<button class="btn btn-sm" id="pause-checkpoint">Parar</button>' : ""}
        ${events.length ? `
          <div class="continuity-history" aria-label="Histórico de etapas">
            ${events.filter((r) => !audit.includes(r.status)).map(receipt).join("")}
            ${events.some((r) => audit.includes(r.status)) ? `<details><summary>Registro das tentativas</summary>
              ${events.filter((r) => audit.includes(r.status)).map(receipt).join("")}</details>` : ""}
          </div>` : ""}
      </section>`;
    const run = (suffix) => api(`${url}/${suffix}`, { method: "POST" }).catch((err) => toast(err.message, "err"));
    slot.querySelector("#resume-checkpoint")?.addEventListener("click", () => run("resume"));
    slot.querySelector("#pause-checkpoint")?.addEventListener("click", () => run("stop"));
    slot.querySelector("#download-checkpoint")?.addEventListener("click", async () => {
      try {
        const data = await api(`${url}/checkpoint`);
        const blob = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
        const link = document.createElement("a");
        link.href = blob;
        link.download = `central-${data.task}.json`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(blob), 1000);
      } catch (err) { toast(err.message, "err"); }
    });
    slot.querySelector("#inspect-checkpoint")?.addEventListener("click", async () => {
      try {
        const data = await api(`${url}/inspect`);
        const names = new Set([...Object.keys(data.before), ...Object.keys(data.current)]);
        const changed = [...names].filter((file) => data.before[file] !== data.current[file]);
        openModal(`<h2>Efeito da tentativa</h2><p class="modal-sub">Confira a pasta do projeto e a tentativa ${esc(data.pending)}.</p>
          <ul>${changed.map((file) => `<li>${esc(file)} · ${!data.before[file] ? "criado" : !data.current[file] ? "removido" : "alterado"}</li>`).join("") || "<li>Nenhuma alteração de arquivo encontrada.</li>"}</ul>`);
      } catch (err) { toast(err.message, "err"); }
    });
    slot.querySelector("#record-checkpoint")?.addEventListener("click", () => {
      openModal(`
        <h2>Registrar progresso assistido</h2>
        <form id="receipt-form">
          <div class="field"><label for="receipt-tests">Decisões, testes e resultados</label><textarea class="input" id="receipt-tests" required></textarea></div>
          <div class="field"><label for="receipt-next">Próximo passo</label><input class="input" id="receipt-next" required></div>
          <div class="field"><label for="receipt-profile">Perfil para continuar</label>
            <select class="select" id="receipt-profile"><option value="">Manter o perfil atual</option>${profiles.filter((p) =>
              p.kind === "google-personal" && p.enabled && p.id !== c.active).map((p) => `<option value="${esc(p.id)}">${esc(p.name)}</option>`).join("")}</select>
          </div>
          <label><input type="checkbox" id="receipt-done"> Trabalho concluído</label>
          <div class="modal-foot"><button class="btn btn-primary">Salvar progresso</button></div>
        </form>`, (box, close) => {
        box.querySelector("form").onsubmit = async (event) => {
          event.preventDefault();
          try {
            await api(`${url}/receipt`, { method: "POST", body: { tests: box.querySelector("#receipt-tests").value,
              next: box.querySelector("#receipt-next").value, profile: box.querySelector("#receipt-profile").value,
              done: box.querySelector("#receipt-done").checked } });
            close();
          } catch (err) { toast(err.message, "err"); }
        };
      });
    });
  }

  function renderTeamBoard() {
    const slot = document.getElementById("team-board-slot");
    if (!slot) return;
    const tasks = state.thread?.tasks || [];
    if (tasks.length === 0) { slot.innerHTML = ""; return; }
    const done = tasks.filter((x) => x.status === "concluida").length;
    slot.innerHTML = `
      <div class="team-board">
        <div class="team-board-head">
          <span>👥 Equipe em ação · ${done}/${tasks.length} tarefas concluídas</span>
          <span style="color:var(--text-3);font-weight:500">${tasks.filter((x) => x.status === "rodando").length} rodando agora</span>
        </div>
        <div class="team-grid">
          ${tasks.map((tk) => `
            <div class="task-chip ${esc(tk.status)}">
              <div class="task-chip-top">
                ${tk.status === "rodando" ? '<div class="spinner" style="width:11px;height:11px"></div>'
                  : tk.status === "concluida" ? "✅" : tk.status === "falhou" ? "❌" : "⏳"}
                <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(tk.id)} · ${esc(tk.title)}</span>
              </div>
              <div class="task-chip-meta">${tk.provider ? `${esc(tk.provider)} · ${esc(tk.model || "")}` : (tk.dependsOn?.length ? `aguarda ${tk.dependsOn.join(", ")}` : "na fila")}</div>
              ${tk.files?.length ? `<div class="task-chip-meta" style="color:#6ee7b7">${esc(tk.files.join(", "))}</div>` : ""}
              ${tk.error ? `<div style="color:#fca5a5;font-size:11px">${esc(tk.error)}</div>` : ""}
            </div>`).join("")}
        </div>
      </div>`;
  }

  function lastModelLabel(t) {
    const m = [...(t.messages || [])].reverse().find((x) => x.role === "assistant" && x.model);
    return m ? `${m.provider || "IA"} · ${m.model}` : null;
  }

  function groupedChanges(changes) {
    const map = new Map();
    for (const c of changes) {
      if (!map.has(c.path)) map.set(c.path, { path: c.path, before: c.before, after: c.after, time: c.time });
      else { const e = map.get(c.path); e.after = c.after; e.time = c.time; }
    }
    return [...map.values()];
  }

  function renderMessages(forceScroll = false) {
    const scroller = document.getElementById("messages");
    const inner = document.getElementById("messages-inner");
    if (!inner) return;
    const nearBottom = forceScroll || (scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 140);
    const msgs = state.thread?.messages || [];
    const toolResults = new Map();
    for (const m of msgs) if (m.role === "tool" && m.toolCallId) toolResults.set(m.toolCallId, m);

    const parts = [];
    if (msgs.length === 0 && !state.live.running) {
      parts.push(`<div class="empty-state">Descreva o que você quer construir neste projeto.<br>No modo <strong>Equipe</strong>, vários workers dividem e executam as tarefas em paralelo pelo seu Pool de contas.</div>`);
    }

    for (const m of msgs) {
      if (m.role === "user") {
        parts.push(`<div class="msg-user">${esc(m.content)}</div>`);
      } else if (m.role === "notice") {
        const icon = m.toolName === "fallback" ? "⚡" : m.toolName === "erro" ? "⚠️" : "ℹ️";
        parts.push(`<div class="notice-bar ${esc(m.toolName || "")}"><span>${icon}</span><div>${esc(m.content)}</div></div>`);
      } else if (m.role === "assistant") {
        const hasText = (m.content || "").trim().length > 0;
        const calls = m.toolCalls || [];
        parts.push(`<div class="msg-ai">
          ${m.model ? `<div class="msg-ai-head"><span class="model-tag">${esc(m.provider || "IA")} · ${esc(m.model)}</span></div>` : ""}
          ${hasText ? `<div class="msg-body">${renderMd(m.content)}</div>` : ""}
          ${calls.map((c) => renderToolCall(c, toolResults.get(c.id))).join("")}
        </div>`);
      }
    }

    if (state.live.running) {
      if (state.live.streaming) {
        parts.push(`<div class="msg-ai" id="live-stream-box">
          ${state.live.route ? `<div class="msg-ai-head"><span class="model-tag">⚡ ${esc(state.live.route.providerName)} · ${esc(state.live.route.model)}</span></div>` : ""}
          <div class="msg-body" id="live-stream-body">${renderMd(state.live.streaming)}</div>
        </div>`);
      }
      if (state.live.pending) {
        parts.push(`<div class="approval-card" id="approval-box">
          <strong>⚠️ O agente quer executar este comando no PowerShell:</strong>
          <pre>${esc(state.live.pending.command)}</pre>
          <div style="display:flex;gap:8px">
            <button class="btn btn-primary btn-sm" data-approve="true">Permitir comando</button>
            <button class="btn btn-danger btn-sm" data-approve="false">Recusar</button>
          </div>
        </div>`);
      }
      parts.push(`<div class="status-line" id="live-status"><div class="spinner"></div><span>${esc(state.live.status || "trabalhando…")}</span></div>`);
    }

    inner.innerHTML = parts.join("");

    inner.querySelectorAll(".copy-code").forEach((btn) => {
      btn.onclick = () => { navigator.clipboard.writeText(btn.dataset.code); btn.textContent = "Copiado!"; setTimeout(() => (btn.textContent = "Copiar"), 1400); };
    });
    inner.querySelectorAll("[data-approve]").forEach((btn) => {
      btn.onclick = async () => {
        await api(`/projects/${state.pid}/threads/${state.tid}/approval`, { method: "POST", body: { id: state.live.pending.id, approve: btn.dataset.approve === "true" } });
      };
    });

    if (nearBottom) scroller.scrollTop = scroller.scrollHeight;
  }

  function renderToolCall(c, res) {
    let label = c.name;
    try {
      const a = JSON.parse(c.arguments || "{}");
      if (c.name === "write_file") label = `Criar/salvar ${a.path || ""}`;
      else if (c.name === "edit_file") label = `Editar ${a.path || ""}`;
      else if (c.name === "read_file") label = `Ler ${a.path || ""}`;
      else if (c.name === "list_files") label = `Listar arquivos ${a.path || ""}`;
      else if (c.name === "search_files") label = `Procurar “${a.text || ""}”`;
      else if (c.name === "run_command") label = `Terminal: ${(a.command || "").slice(0, 70)}`;
    } catch { /* ignore */ }
    const dotClass = !res ? "run" : res.ok ? "ok" : "err";
    const statusText = !res ? "executando…" : res.ok ? "ok" : "falhou";
    return `<details class="tool-card">
      <summary><span class="tool-dot ${dotClass}"></span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(label)}</span><span style="font-size:11px;color:var(--text-3)">${statusText}</span></summary>
      <pre>${esc(res ? res.content : c.arguments)}</pre>
    </details>`;
  }

  async function sendChat(ask = false) {
    const input = document.getElementById("chat-input");
    const text = input.value.trim();
    if (!text || state.live.running) return;
    input.value = "";
    try { await api(`/projects/${state.pid}/threads/${state.tid}/messages`, { method: "POST", body: { text, ask } }); }
    catch (err) { toast(err.message, "err"); input.value = text; }
  }

  function connectThreadStream(pid, tid) {
    const es = new EventSource(`/api/projects/${pid}/threads/${tid}/events?t=${encodeURIComponent(token)}`);
    state.es = es;
    let refreshVersion = 0;
    async function refreshContinuity() {
      const sent = ++refreshVersion;
      try {
        const [data, files] = await Promise.all([api(`/projects/${pid}/threads/${tid}`), api(`/projects/${pid}/files`)]);
        if (sent !== refreshVersion || state.view !== "thread" || state.tid !== tid) return;
        state.thread = data.thread;
        state.files = files;
        if (data.thread.reviewedWork) updateThreadRunning(data.live.running);
        renderContinuity();
        renderReviewed();
        if (data.thread.reviewedWork) renderMessages();
        const fileTab = document.querySelector('[data-ptab="files"]');
        if (fileTab) fileTab.textContent = `Arquivos (${files.length})`;
        if (state.showPanel && state.panelTab === "files") renderRightPanel();
      } catch (err) { toast(err.message, "err"); }
    }
    es.onopen = () => { if (state.thread?.continuation || state.thread?.reviewedWork) refreshContinuity(); };
    es.onmessage = async (ev) => {
      const e = JSON.parse(ev.data);
      if (e.type === "continuity" || e.type === "reviewed") {
        await refreshContinuity();
      } else if (e.type === "run") {
        updateThreadRunning(e.running);
        if (e.title && state.thread) {
          state.thread.title = e.title;
          const el = document.getElementById("thread-title");
          if (el) el.textContent = e.title;
        }
        if (state.thread?.reviewedWork) {
          renderReviewed();
          if (!e.running) await refreshContinuity();
        }
        renderMessages();
        loadProjectDetails(pid);
        refreshPool();
      } else if (e.type === "tasks") {
        if (state.thread) state.thread.tasks = e.tasks || [];
        renderTeamBoard();
      } else if (e.type === "route") {
        state.live.route = e.route;
        state.live.streaming = "";
        const pill = document.getElementById("route-pill");
        if (pill) { pill.textContent = `⚡ ${e.route.providerName} · ${e.route.model}`; pill.classList.add("live"); }
      } else if (e.type === "status") {
        state.live.status = e.text;
        const st = document.getElementById("live-status");
        if (st) st.querySelector("span").textContent = e.text;
        else renderMessages();
      } else if (e.type === "delta") {
        state.live.streaming = (state.live.streaming || "") + e.text;
        const body = document.getElementById("live-stream-body");
        const scroller = document.getElementById("messages");
        if (body) {
          const nearBottom = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 140;
          body.innerHTML = renderMd(state.live.streaming);
          if (nearBottom) scroller.scrollTop = scroller.scrollHeight;
        } else renderMessages();
      } else if (e.type === "message") {
        state.live.streaming = "";
        if (!state.thread.messages.some((m) => m.id === e.message.id)) state.thread.messages.push(e.message);
        renderMessages();
      } else if (e.type === "approval") {
        state.live.pending = { id: e.id, command: e.command };
        renderMessages(true);
      } else if (e.type === "approval-done") {
        state.live.pending = null;
        renderMessages();
      } else if (e.type === "files") {
        const [files, fresh] = await Promise.all([api(`/projects/${pid}/files`), api(`/projects/${pid}/threads/${tid}`)]);
        state.files = files || [];
        state.thread.changes = fresh.thread.changes || [];
        updatePanelTabCounts();
        if (state.showPanel) {
          if (state.panelTab === "preview") reloadPreviewFrame();
          else renderRightPanel();
        }
      }
    };
  }

  function updateThreadRunning(running) {
    state.live.running = running;
    if (!running) { state.live.streaming = ""; state.live.pending = null; state.live.status = null; }
    document.getElementById("btn-stop")?.classList.toggle("hidden", !running);
    document.getElementById("btn-send")?.classList.toggle("hidden", running);
    document.getElementById("btn-ask")?.classList.toggle("hidden", running);
    document.getElementById("route-pill")?.classList.toggle("live", running);
  }

  function updatePanelTabCounts() {
    const ft = document.querySelector('[data-ptab="files"]');
    if (ft) ft.textContent = `Arquivos (${state.files.length})`;
    const dt = document.querySelector('[data-ptab="diff"]');
    if (dt) dt.textContent = `Diff (${groupedChanges(state.thread?.changes || []).length})`;
  }

  function reloadPreviewFrame() {
    const f = document.getElementById("preview-iframe");
    if (f) f.src = `${PREVIEW_ORIGIN}/p/${state.pid}/?v=${Date.now()}`;
  }

  async function renderRightPanel() {
    const body = document.getElementById("panel-body");
    if (!body) return;
    if (state.panelTab === "preview") {
      const url = `${PREVIEW_ORIGIN}/p/${state.pid}/`;
      body.innerHTML = `
        <div class="preview-bar">
          <span class="preview-url">${esc(url)}</span>
          <button class="btn btn-sm" id="btn-reload-preview">↻ Recarregar</button>
          <a class="btn btn-sm" href="${esc(url)}" target="_blank" rel="noopener">Abrir ↗</a>
        </div>
        <iframe id="preview-iframe" class="preview-frame" src="${esc(url)}?v=${Date.now()}" sandbox="allow-scripts allow-forms allow-modals allow-same-origin allow-popups"></iframe>`;
      document.getElementById("btn-reload-preview").onclick = reloadPreviewFrame;
    } else if (state.panelTab === "files") {
      if (state.files.length === 0) {
        body.innerHTML = `<div class="empty-state">A pasta do projeto está vazia.<br>Peça ao agente ou à equipe para criar arquivos.</div>`;
        return;
      }
      if (!state.selectedFile || !state.files.some((f) => f.path === state.selectedFile)) state.selectedFile = state.files[0].path;
      body.innerHTML = `
        <div class="files-split">
          <div class="file-list">
            ${state.files.map((f) => `
              <button class="file-row ${state.selectedFile === f.path ? "active" : ""}" data-fpath="${esc(f.path)}">
                <span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(f.path)}</span>
                <span style="color:var(--text-3);font-size:11px">${fmtSize(f.size)}</span>
              </button>`).join("")}
          </div>
          <div class="file-viewer" id="file-viewer">Carregando…</div>
        </div>`;
      body.querySelectorAll("[data-fpath]").forEach((btn) => {
        btn.onclick = () => {
          state.selectedFile = btn.dataset.fpath;
          body.querySelectorAll("[data-fpath]").forEach((x) => x.classList.toggle("active", x.dataset.fpath === state.selectedFile));
          loadFileViewer();
        };
      });
      loadFileViewer();
    } else if (state.panelTab === "diff") {
      const list = groupedChanges(state.thread?.changes || []);
      if (list.length === 0) {
        body.innerHTML = `<div class="empty-state">Nenhuma alteração de arquivo registrada nesta conversa ainda.</div>`;
        return;
      }
      body.innerHTML = `<div class="diff-wrap">${list.map((c) => {
        const lines = computeDiff(c.before, c.after);
        return `<div class="diff-card">
          <div class="diff-head">
            <span>${esc(c.path)} ${c.before == null ? '<span class="pill ok">novo</span>' : ""}</span>
            <button class="btn btn-sm btn-danger" data-revert="${esc(c.path)}">Desfazer</button>
          </div>
          <div class="diff-lines">${lines.map((l) => `<div class="diff-line ${l.type}">${esc(l.text)}</div>`).join("")}</div>
        </div>`;
      }).join("")}</div>`;
      body.querySelectorAll("[data-revert]").forEach((btn) => {
        btn.onclick = async () => {
          const path = btn.dataset.revert;
          if (!confirm(`Desfazer as alterações em ${path}?`)) return;
          try { await api(`/projects/${state.pid}/threads/${state.tid}/revert`, { method: "POST", body: { path } }); toast(`Desfeito em ${path}.`, "ok"); }
          catch (err) { toast(err.message, "err"); }
        };
      });
    } else if (state.panelTab === "git") {
      body.innerHTML = `<div class="empty-state">Verificando Git…</div>`;
      try {
        const g = await api(`/projects/${state.pid}/git`);
        if (!g.isRepo) {
          body.innerHTML = `
            <div class="git-wrap">
              <div class="card">
                <h3>Repositório Git</h3>
                <p style="font-size:13px;color:var(--text-2);margin:0 0 12px">Esta pasta ainda não é um repositório Git:<br><code style="font-family:var(--mono);font-size:11.5px">${esc(g.workspace)}</code></p>
                <button class="btn btn-primary btn-sm" id="git-init-btn">Inicializar Git aqui</button>
              </div>
            </div>`;
          document.getElementById("git-init-btn").onclick = async () => {
            try { await api(`/projects/${state.pid}/git/init`, { method: "POST" }); toast("Git inicializado!", "ok"); renderRightPanel(); }
            catch (err) { toast(err.message, "err"); }
          };
          return;
        }
        body.innerHTML = `
          <div class="git-wrap">
            <div style="display:flex;align-items:center;justify-content:space-between">
              <span class="pill ok">🌿 ${esc(g.branch)}</span>
              <button class="btn btn-sm" id="git-refresh">↻ Atualizar</button>
            </div>
            <div>
              <div style="font-size:12px;font-weight:600;color:var(--text-2);margin-bottom:5px">Alterações (${g.changes.length})</div>
              <div class="git-list">${g.changes.length === 0 ? "<div>Árvore limpa — nada pendente.</div>" : g.changes.map((l) => `<div>${esc(l)}</div>`).join("")}</div>
            </div>
            ${g.changes.length > 0 ? `
              <div style="display:flex;gap:8px">
                <input class="input" id="git-msg" placeholder="Mensagem do commit (ex.: feat: dashboard)">
                <button class="btn btn-primary btn-sm" id="git-commit-btn">Commitar</button>
              </div>` : ""}
            <div>
              <div style="font-size:12px;font-weight:600;color:var(--text-2);margin-bottom:5px">Últimos commits</div>
              <div class="git-list">${g.commits.length === 0 ? "<div>Nenhum commit ainda.</div>" : g.commits.map((l) => `<div>${esc(l)}</div>`).join("")}</div>
            </div>
          </div>`;
        document.getElementById("git-refresh").onclick = renderRightPanel;
        document.getElementById("git-commit-btn")?.addEventListener("click", async () => {
          const message = document.getElementById("git-msg").value.trim();
          if (!message) { toast("Escreva a mensagem do commit.", "err"); return; }
          try { await api(`/projects/${state.pid}/git/commit`, { method: "POST", body: { message } }); toast("Commit salvo!", "ok"); renderRightPanel(); }
          catch (err) { toast(err.message, "err"); }
        });
      } catch (err) { body.innerHTML = `<div class="empty-state">${esc(err.message)}</div>`; }
    }
  }

  async function loadFileViewer() {
    const viewer = document.getElementById("file-viewer");
    if (!viewer || !state.selectedFile) return;
    try {
      const data = await api(`/projects/${state.pid}/file?path=${encodeURIComponent(state.selectedFile)}`);
      viewer.textContent = data.binary ? `[Arquivo binário · ${fmtSize(data.size)}]` : (data.content ?? "");
    } catch (err) { viewer.textContent = err.message; }
  }

  // ---------- Flow Studio v2 ----------

  function showNewFlowModal(pid) {
    openModal(`
      <h2>🎬 Novo Flow (Vídeo ou Minissérie)</h2>
      <p class="modal-sub">Descreva a ideia e a IA cria o roteiro, bíblia de personagens e prompts de cada cena.</p>
      <div class="field"><label>Ideia / Prompt</label><textarea class="textarea" id="mf-prompt" rows="4" placeholder="Ex.: Minissérie de suspense de 3 episódios sobre um metrô fantasma em São Paulo…"></textarea></div>
      <div class="two-col">
        <div class="field"><label>Formato</label><select class="select" id="mf-format"><option value="video">Vídeo único</option><option value="serie">Minissérie</option></select></div>
        <div class="field"><label>Proporção</label><select class="select" id="mf-aspect"><option value="16:9">16:9 Horizontal</option><option value="9:16">9:16 Vertical</option></select></div>
        <div class="field"><label>Episódios (se série)</label><input class="input" id="mf-ep" type="number" min="2" max="12" value="3"></div>
        <div class="field"><label>Cenas por episódio</label><input class="input" id="mf-sc" type="number" min="1" max="12" value="4"></div>
        <div class="field"><label>Duração da cena</label><select class="select" id="mf-sec"><option value="4">4 segundos</option><option value="6">6 segundos</option><option value="8" selected>8 segundos</option></select></div>
        <div class="field"><label>Estilo visual</label><input class="input" id="mf-style" placeholder="Cinematográfico 35mm"></div>
      </div>
      <div class="modal-foot"><button class="btn" id="mf-cancel">Cancelar</button><button class="btn btn-flow" id="mf-go">Criar storyboard →</button></div>`, (box, close) => {
      box.querySelector("#mf-cancel").onclick = close;
      box.querySelector("#mf-go").onclick = async () => {
        const prompt = box.querySelector("#mf-prompt").value.trim();
        if (!prompt) { toast("Escreva a ideia do vídeo.", "err"); return; }
        try {
          const f = await api(`/projects/${pid}/flows`, {
            method: "POST",
            body: {
              prompt, format: box.querySelector("#mf-format").value, aspect: box.querySelector("#mf-aspect").value,
              episodes: Number(box.querySelector("#mf-ep").value), scenes: Number(box.querySelector("#mf-sc").value),
              seconds: Number(box.querySelector("#mf-sec").value), style: box.querySelector("#mf-style").value,
            },
          });
          close();
          await loadProjectDetails(pid, true);
          openFlow(pid, f.id);
        } catch (err) { toast(err.message, "err"); }
      };
    });
  }

  function renderFlow() {
    const f = state.flow;
    if (!f) return;
    const proj = state.projects.find((p) => p.id === state.pid);
    const media = state.settings?.media || { videoCostPerSecond: 0.4, currency: "US$" };
    const allScenes = (f.episodeList || []).flatMap((e) => e.scenes || []);
    const missingVideos = allScenes.filter((s) => !s.video && !s.busy && !s.videoIntent && !s.operation);
    const main = document.getElementById("main");

    main.innerHTML = `
      <header class="topbar">
        <div class="topbar-left">
          <span class="crumb-pill">${esc(proj?.name || "Projeto")} · Flow</span>
          <span class="topbar-title">${esc(f.title)}</span>
          <span class="pill muted">${f.format === "serie" ? `Série · ${(f.episodeList || []).length} ep.` : "Vídeo"} · ${esc(f.aspect)}</span>
        </div>
        <div class="topbar-right">
          ${f.estimatedSpent > 0 ? `<span class="pill warn" title="Custo estimado em gerações Veo">${esc(media.currency)} ${f.estimatedSpent.toFixed(2)}</span>` : ""}
          <button class="btn btn-sm" id="fl-rewrite">✎ Reescrever</button>
          <button class="btn btn-sm" id="fl-batch-img" ${f.status === "roteirizando" ? "disabled" : ""} title="Gera em paralelo usando todas as contas Gemini do Pool">🖼 Quadros (Pool)</button>
          <button class="btn btn-sm" id="fl-batch-voice" ${f.status === "roteirizando" ? "disabled" : ""}>🎙 Vozes</button>
          <button class="btn btn-sm" id="fl-batch-vid" ${missingVideos.length === 0 ? "disabled" : ""}>🎥 Vídeos (${missingVideos.length})</button>
          <button class="btn btn-sm btn-flow" id="fl-render" ${f.rendering || allScenes.length === 0 ? "disabled" : ""}>🎬 Montar MP4</button>
          <button class="icon-btn" id="fl-folder" title="Abrir pasta do Flow">📁</button>
        </div>
      </header>

      <div class="flow-scroll">
        <div class="flow-inner">
          ${f.status === "roteirizando" ? `<div class="flow-banner"><div class="spinner"></div><strong>Escrevendo roteiro, bíblia de personagens e prompts cinematográficos…</strong></div>` : ""}
          ${f.rendering ? `<div class="flow-banner"><div class="spinner"></div><strong>Montando no FFmpeg: ${esc(f.rendering)}…</strong></div>` : ""}
          ${f.error ? `<div class="notice-bar erro"><span>⚠️</span><div>${esc(f.error)}</div></div>` : ""}

          ${(f.exports || []).length > 0 ? `
            <section class="card">
              <h3>🎉 Vídeos montados prontos</h3>
              <div class="exports-grid">
                ${f.exports.map((ex) => `
                  <div class="export-card">
                    <video controls preload="metadata" src="${mediaUrl(state.pid, state.fid, ex.path)}"></video>
                    <div class="export-meta">
                      <div><strong>${esc(ex.label)}</strong><div style="font-size:12px;color:var(--text-3)">${ex.seconds}s · ${relTime(ex.time)}</div></div>
                      <div style="display:flex;gap:6px">
                        <a class="btn btn-sm btn-primary" href="${mediaUrl(state.pid, state.fid, ex.path)}" download>MP4 ⬇</a>
                        <a class="btn btn-sm" href="${mediaUrl(state.pid, state.fid, ex.path.replace(/\.mp4$/, ".srt"))}" download>SRT</a>
                      </div>
                    </div>
                  </div>`).join("")}
              </div>
            </section>` : ""}

          <div class="flow-meta-grid">
            <div class="card">
              <h3>Direção & Montagem ${f.scriptModel ? `<span class="model-tag" style="margin-left:6px;font-weight:400">${esc(f.scriptModel)}</span>` : ""}</h3>
              <p style="margin:0 0 10px;color:var(--text-2)">${esc(f.logline || f.prompt)}</p>
              <div class="field" style="margin:0">
                <label>Estilo visual global</label>
                <input class="input" id="fl-style-input" value="${esc(f.style || "")}" placeholder="Ex.: cinematic 35mm, moody lighting">
              </div>
              <div class="cinema-bar">
                <label style="display:flex;align-items:center;gap:6px;cursor:pointer">
                  <input type="checkbox" id="fl-sub-toggle" ${state.flowSubtitles ? "checked" : ""}> Legendas
                </label>
                <label style="display:flex;align-items:center;gap:6px">Transição:
                  <select id="fl-fade" style="background:var(--bg);border:1px solid var(--border-strong);border-radius:6px;padding:3px 6px">
                    <option value="0" ${f.transitionSeconds === 0 ? "selected" : ""}>Corte seco</option>
                    <option value="0.4" ${Math.abs((f.transitionSeconds ?? 0.4) - 0.4) < 0.05 ? "selected" : ""}>Fade 0.4s</option>
                    <option value="0.8" ${Math.abs((f.transitionSeconds ?? 0.4) - 0.8) < 0.05 ? "selected" : ""}>Fade 0.8s</option>
                  </select>
                </label>
                ${f.music
                  ? `<span class="pill ok">🎵 Trilha ativa (ducking auto)</span>
                     <input type="range" id="fl-mvol" min="5" max="80" value="${Math.round((f.musicVolume ?? 0.28) * 100)}" style="width:76px" title="Volume da trilha">
                     <button class="btn btn-sm btn-ghost" id="fl-music-del" style="color:#fca5a5">Remover trilha</button>`
                  : `<label class="btn btn-sm" style="cursor:pointer">🎵 Adicionar trilha (MP3/WAV)<input type="file" id="fl-music-up" class="hidden" accept="audio/*"></label>`}
              </div>
            </div>

            <div class="card">
              <h3>Bíblia de personagens (consistência visual)</h3>
              ${(f.characters || []).length === 0 ? `<div style="color:var(--text-3);font-size:13px">Os personagens do roteiro aparecem aqui com retrato de referência.</div>`
                : `<div class="char-list">${f.characters.map((c) => `
                  <div class="char-chip">
                    <div class="char-avatar">
                      ${c.busy ? '<div class="spinner"></div>' : c.image ? `<img src="${mediaUrl(state.pid, state.fid, c.image)}" alt="${esc(c.name)}">` : "👤"}
                    </div>
                    <div style="flex:1;min-width:0">
                      <strong>${esc(c.name)}</strong>
                      <span style="display:block">${esc(c.description)}</span>
                      <div style="display:flex;gap:6px;margin-top:5px">
                        <button class="btn btn-sm" data-cgen="${esc(c.name)}" ${c.busy ? "disabled" : ""}>🎨 Retrato IA</button>
                        <label class="btn btn-sm" style="cursor:pointer">⬆ Foto<input type="file" class="hidden" data-cimp="${esc(c.name)}" accept="image/*"></label>
                      </div>
                    </div>
                  </div>`).join("")}</div>`}
            </div>
          </div>

          ${(f.episodeList || []).map((ep, ei) => `
            <section class="episode-block">
              <div class="episode-head">
                <div>
                  <h2>${f.episodeList.length > 1 ? `Episódio ${ei + 1} · ` : ""}${esc(ep.title)}</h2>
                  ${ep.synopsis ? `<p>${esc(ep.synopsis)}</p>` : ""}
                </div>
                <button class="btn btn-sm" data-add-scene="${ep.id}">+ Adicionar cena</button>
              </div>
              <div class="scene-grid">
                ${(ep.scenes || []).map((s, si) => renderSceneCard(f, s, si)).join("")}
              </div>
            </section>`).join("")}
        </div>
      </div>`;

    document.getElementById("fl-folder").onclick = () => api(`/projects/${state.pid}/open-folder`, { method: "POST", body: { flow: state.fid } });
    document.getElementById("fl-sub-toggle").onchange = (e) => { state.flowSubtitles = e.target.checked; };
    document.getElementById("fl-fade").onchange = (e) => api(`/projects/${state.pid}/flows/${state.fid}`, { method: "PATCH", body: { transitionSeconds: Number(e.target.value) } });
    document.getElementById("fl-mvol")?.addEventListener("change", (e) => api(`/projects/${state.pid}/flows/${state.fid}`, { method: "PATCH", body: { musicVolume: Number(e.target.value) / 100 } }));
    document.getElementById("fl-music-del")?.addEventListener("click", () => api(`/projects/${state.pid}/flows/${state.fid}/music`, { method: "DELETE" }));
    document.getElementById("fl-music-up")?.addEventListener("change", async (e) => {
      const file = e.target.files[0]; if (!file) return;
      const fd = new FormData(); fd.append("file", file);
      try { await api(`/projects/${state.pid}/flows/${state.fid}/music`, { method: "POST", body: fd }); toast("Trilha adicionada (com ducking automático)!", "ok"); }
      catch (err) { toast(err.message, "err"); }
    });
    document.getElementById("fl-style-input").onblur = async (e) => {
      if (e.target.value !== (f.style || "")) {
        await api(`/projects/${state.pid}/flows/${state.fid}`, { method: "PATCH", body: { style: e.target.value } });
        toast("Estilo salvo.", "ok");
      }
    };
    document.getElementById("fl-rewrite").onclick = () => {
      openModal(`
        <h2>Reescrever roteiro</h2>
        <p class="modal-sub">Cenas cujo visual não mudar mantêm todas as tomadas já geradas.</p>
        <div class="field"><label>Instrução</label><textarea class="textarea" id="rw-fb" rows="4" placeholder="Ex.: Deixe mais tenso e adicione uma reviravolta na última cena."></textarea></div>
        <div class="modal-foot"><button class="btn" id="rw-c">Cancelar</button><button class="btn btn-primary" id="rw-ok">Reescrever</button></div>`, (box, close) => {
        box.querySelector("#rw-c").onclick = close;
        box.querySelector("#rw-ok").onclick = async () => {
          try { await api(`/projects/${state.pid}/flows/${state.fid}/rewrite`, { method: "POST", body: { feedback: box.querySelector("#rw-fb").value } }); close(); }
          catch (err) { toast(err.message, "err"); }
        };
      });
    };
    document.getElementById("fl-batch-img").onclick = () => runFlowBatch("image");
    document.getElementById("fl-batch-voice").onclick = () => runFlowBatch("voice");
    document.getElementById("fl-batch-vid").onclick = () => {
      const secs = missingVideos.reduce((acc, s) => acc + (s.seconds || 8), 0);
      confirmVideoCost(`${missingVideos.length} cenas (${secs}s no total)`, secs * (media.videoCostPerSecond || 0.4), media.currency, () => runFlowBatch("video", true));
    };
    document.getElementById("fl-render").onclick = async () => {
      try { await api(`/projects/${state.pid}/flows/${state.fid}/render`, { method: "POST", body: { subtitles: state.flowSubtitles } }); toast("Montagem cinematográfica iniciada!", "ok"); }
      catch (err) { toast(err.message, "err"); }
    };

    main.querySelectorAll("[data-cgen]").forEach((btn) => {
      btn.onclick = () => api(`/projects/${state.pid}/flows/${state.fid}/characters/image`, { method: "POST", body: { name: btn.dataset.cgen } }).catch((e) => toast(e.message, "err"));
    });
    main.querySelectorAll("[data-cimp]").forEach((inp) => {
      inp.onchange = async (e) => {
        const file = e.target.files[0]; if (!file) return;
        const fd = new FormData(); fd.append("name", inp.dataset.cimp); fd.append("file", file);
        try { await api(`/projects/${state.pid}/flows/${state.fid}/characters/import`, { method: "POST", body: fd }); toast("Foto de referência salva!", "ok"); }
        catch (err) { toast(err.message, "err"); }
      };
    });
    main.querySelectorAll("[data-add-scene]").forEach((btn) => {
      btn.onclick = () => api(`/projects/${state.pid}/flows/${state.fid}/episodes/${btn.dataset.addScene}/scenes`, { method: "POST" });
    });

    bindSceneEvents(main, media, f);
  }

  function renderSceneCard(f, s, idx) {
    const busyText = s.busy === "imagem" ? "Gerando quadro…" : s.busy === "video" ? "Gerando vídeo no Veo…" : s.busy === "voz" ? "Gerando narração…" : "";
    const badge = s.video ? (s.videoOrigin || "Vídeo pronto") : s.image ? "Quadro pronto" : `Cena ${idx + 1}`;
    const takes = s.takes || [];
    return `
      <article class="scene-card" data-sid="${s.id}">
        <div class="scene-media ${f.aspect === "9:16" ? "vertical" : ""}" data-dropzone="${s.id}" title="Você pode arrastar e soltar MP4, imagem ou áudio aqui">
          ${s.video
            ? `<video controls preload="metadata" src="${mediaUrl(state.pid, state.fid, s.video)}"></video>`
            : s.image
              ? `<img src="${mediaUrl(state.pid, state.fid, s.image)}" alt="${esc(s.title)}">`
              : `<div class="scene-placeholder">Cena ${idx + 1}<br><span style="font-size:11.5px">Gere pelo Pool ou arraste um MP4 / imagem aqui</span></div>`}
          <span class="scene-badge">${esc(badge)}</span>
          ${s.busy ? `<div class="scene-busy"><div class="spinner"></div><span>${busyText}</span></div>` : ""}
        </div>

        ${takes.length > 0 ? `
          <div class="takes-bar">
            <span style="font-size:10.5px;color:var(--text-3);margin-right:2px">Tomadas:</span>
            ${takes.map((tk, ti) => {
              const isActive = (tk.kind === "video" && s.video === tk.path) || (tk.kind === "imagem" && s.image === tk.path && !s.video);
              return `<button class="take-pill ${isActive ? "active" : ""}" data-sel-take="${tk.id}" title="${esc(tk.origin)}">
                ${tk.kind === "video" ? "🎥" : "🖼"} T${ti + 1}
                <span class="tk-x" data-del-take="${tk.id}" title="Apagar tomada">×</span>
              </button>`;
            }).join("")}
          </div>` : ""}

        <div class="scene-body">
          <div class="scene-row">
            <input data-sf="title" value="${esc(s.title)}" placeholder="Título da cena" style="font-weight:600">
            <select data-sf="seconds" style="width:74px">
              ${[4, 6, 8].map((v) => `<option value="${v}" ${s.seconds === v ? "selected" : ""}>${v}s</option>`).join("")}
            </select>
          </div>
          <textarea data-sf="visual" placeholder="Prompt visual (em inglês) para o gerador de vídeo…">${esc(s.visual)}</textarea>
          <input data-sf="narration" value="${esc(s.narration)}" placeholder="Narração / fala em português (opcional)">
          ${s.audio ? `<audio controls src="${mediaUrl(state.pid, state.fid, s.audio)}"></audio>` : ""}
          ${s.error ? `<div class="scene-err">${esc(s.error)}</div>` : ""}
        </div>

        <div class="scene-actions">
          <button class="btn btn-sm" data-sact="image" ${s.busy ? "disabled" : ""}>🖼 Quadro</button>
          <button class="btn btn-sm btn-primary" data-sact="video" data-secs="${s.seconds}" ${s.busy || s.videoIntent || s.operation ? "disabled" : ""}>🎥 Veo</button>
          ${s.videoIntent || s.operation ? `<small>Operação original preservada · ${esc(s.operation?.split("|").at(-1) || "recibo pendente")}</small>` : ""}
          ${s.videoIntent && s.operation && !s.busy ? '<button class="btn btn-sm" data-sact="resume-video">Consultar operação original</button>' : ""}
          <button class="btn btn-sm" data-sact="voice" ${s.busy || !s.narration ? "disabled" : ""}>🎙 Voz</button>
          <button class="btn btn-sm" data-scopy title="Copiar prompt pronto para colar no Google Flow Web">📋 Prompt</button>
          <label class="btn btn-sm" style="cursor:pointer" title="Importar vídeo, imagem ou áudio">
            ⬆ Importar<input type="file" class="hidden" data-simport accept="video/*,image/*,audio/*">
          </label>
          <button class="btn btn-sm btn-ghost" data-smove="-1" title="Mover antes">←</button>
          <button class="btn btn-sm btn-ghost" data-smove="1" title="Mover depois">→</button>
          <button class="btn btn-sm btn-ghost" data-sdel title="Excluir cena" style="margin-left:auto;color:#fca5a5">✕</button>
        </div>
      </article>`;
  }

  function bindSceneEvents(root, media, flowObj) {
    const uploadToScene = async (sid, file) => {
      const fd = new FormData(); fd.append("file", file);
      try {
        await api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/import`, { method: "POST", body: fd });
        toast("Tomada importada na cena!", "ok");
      } catch (err) { toast(err.message, "err"); }
    };

    root.querySelectorAll(".scene-card").forEach((card) => {
      const sid = card.dataset.sid;
      const dz = card.querySelector("[data-dropzone]");
      if (dz) {
        dz.ondragover = (e) => { e.preventDefault(); dz.classList.add("dragover"); };
        dz.ondragleave = () => dz.classList.remove("dragover");
        dz.ondrop = (e) => {
          e.preventDefault(); dz.classList.remove("dragover");
          const file = e.dataTransfer.files?.[0];
          if (file) uploadToScene(sid, file);
        };
      }

      const saveField = async (el) => {
        const key = el.dataset.sf;
        const val = key === "seconds" ? Number(el.value) : el.value;
        try { await api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}`, { method: "PATCH", body: { [key]: val } }); }
        catch (err) { toast(err.message, "err"); }
      };
      card.querySelectorAll("[data-sf]").forEach((el) => {
        el.onblur = () => saveField(el);
        if (el.tagName === "SELECT") el.onchange = () => saveField(el);
      });
      card.querySelector("[data-scopy]")?.addEventListener("click", () => {
        const visual = card.querySelector('[data-sf="visual"]').value;
        const chars = (flowObj.characters || []).map((c) => `${c.name}: ${c.description}`).join("; ");
        const full = `${visual}\nStyle: ${flowObj.style || "cinematic"}.` + (chars ? `\nCharacters: ${chars}` : "");
        navigator.clipboard.writeText(full);
        toast("Prompt completo copiado — pronto para colar no Flow!", "ok");
      });
      card.querySelectorAll("[data-sel-take]").forEach((btn) => {
        btn.onclick = (e) => {
          if (e.target.closest("[data-del-take]")) return;
          api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/takes/${btn.dataset.selTake}/select`, { method: "POST" });
        };
      });
      card.querySelectorAll("[data-del-take]").forEach((x) => {
        x.onclick = (e) => {
          e.stopPropagation();
          api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/takes/${x.dataset.delTake}`, { method: "DELETE" });
        };
      });
      card.querySelectorAll("[data-sact]").forEach((btn) => {
        btn.onclick = async () => {
          const act = btn.dataset.sact;
          if (act === "video") {
            const secs = Number(btn.dataset.secs || 8);
            confirmVideoCost(`1 cena (${secs}s)`, secs * (media.videoCostPerSecond || 0.4), media.currency, async (providerId) => {
              await api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/video`, { method: "POST", body: { confirm: true, providerId } });
            }, true);
          } else {
            try { await api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/${act}`, { method: "POST" }); }
            catch (err) { toast(err.message, "err"); }
          }
        };
      });
      card.querySelector("[data-simport]")?.addEventListener("change", (e) => {
        const file = e.target.files[0]; if (file) uploadToScene(sid, file);
      });
      card.querySelectorAll("[data-smove]").forEach((btn) => {
        btn.onclick = () => api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}/move`, { method: "POST", body: { delta: Number(btn.dataset.smove) } });
      });
      card.querySelector("[data-sdel]")?.addEventListener("click", () => {
        if (confirm("Remover esta cena?")) api(`/projects/${state.pid}/flows/${state.fid}/scenes/${sid}`, { method: "DELETE" });
      });
    });
  }

  function confirmVideoCost(label, estimate, currency, onConfirm, selectProvider = false) {
    const providers = (state.settings?.providers || []).filter((p) => p.kind === "gemini" && p.enabled && p.hasKey);
    openModal(`
      <h2>Confirmar geração de vídeo (Veo)</h2>
      <p class="modal-sub">Você está prestes a gerar <strong>${esc(label)}</strong> com a API Gemini.</p>
      <div class="notice-bar fallback" style="margin-bottom:12px">
        <span>💰</span>
        <div>Custo estimado <strong>${esc(currency)} ${estimate.toFixed(2)}</strong>. Cada geração permanece vinculada à conexão que a iniciou.</div>
      </div>
      ${selectProvider ? `<div class="field"><label for="cv-provider">Conexão desta cena</label><select class="select" id="cv-provider">
        <option value="">Usar seleção do pool</option>${providers.map((p) => `<option value="${esc(p.id)}">${esc(p.name)}</option>`).join("")}</select></div>` : ""}
      <div class="modal-foot"><button class="btn" id="cv-no">Cancelar</button><button class="btn btn-primary" id="cv-yes">Gerar vídeo agora</button></div>`, (box, close) => {
      box.querySelector("#cv-no").onclick = close;
      box.querySelector("#cv-yes").onclick = async () => {
        const provider = box.querySelector("#cv-provider")?.value || "";
        close();
        try { await onConfirm(provider); } catch (err) { toast(err.message, "err"); }
      };
    });
  }

  async function runFlowBatch(what, confirm = false) {
    try {
      const r = await api(`/projects/${state.pid}/flows/${state.fid}/batch`, { method: "POST", body: { what, confirm } });
      toast(`Enfileiradas ${r.count} cenas em paralelo no Pool!`, "ok");
    } catch (err) { toast(err.message, "err"); }
  }

  function connectFlowStream(pid, fid) {
    const es = new EventSource(`/api/projects/${pid}/flows/${fid}/events?t=${encodeURIComponent(token)}`);
    state.es = es;
    es.onmessage = (ev) => {
      const e = JSON.parse(ev.data);
      if (e.type === "flow") {
        const active = document.activeElement;
        state.flow = e.flow;
        if (!active || !active.matches("[data-sf], #fl-style-input")) renderFlow();
      } else if (e.type === "notice") toast("⚡ " + e.text);
    };
  }

  // ---------- Settings View (Pool, Estratégias, Telemetria e Flow) ----------

  async function refreshAgyPool() {
    try {
      state.agyPool = await api("/antigravity/status");
      renderAgyPool();
    } catch (error) {
      state.agyPool = { issue: error.message };
      renderAgyPool();
    }
  }

  async function actInAgyPool(route, body, message) {
    if (state.agyBusy) return;
    state.agyBusy = true;
    renderAgyPool();
    try {
      await api(`/antigravity/${route}`, { method: "POST", body });
      if (message) toast(message, "ok");
    } catch (error) { toast(error.message, "err"); }
    finally {
      state.agyBusy = false;
      await refreshAgyPool();
    }
  }

  function renderAgyPool() {
    const slot = document.getElementById("antigravity-panel");
    if (!slot) return;
    const current = state.agyPool;
    const activityOpen = slot.querySelector("#agy-activity")?.open;
    const enabled = current?.ready;
    const pool = current?.pool;
    const accounts = pool?.accounts || [];
    const project = pool?.session;
    if (enabled && !state.agyHydrated) {
      state.agyHydrated = true;
      const previous = pool?.lastProject;
      if (previous && state.projects.some(entry => entry.id === previous.projectId)) {
        state.agyProject = previous.projectId;
        state.agyAccounts = previous.accountIds;
        state.agyMode = previous.mode;
        state.agyResume = true;
      }
    }
    const unavailable = Object.entries(current?.dependencies || {}).filter(([, installed]) => !installed)
      .map(([name]) => ({ agy: "Antigravity CLI", node: "Node.js 20+", pwsh: "PowerShell 7" }[name]));
    const disabled = state.agyBusy ? "disabled" : "";
    const idOf = (id) => accounts.find((account) => account.id === id);
    state.agyAccounts = state.agyAccounts.filter(idOf);
    const selected = state.agyAccounts;
    const renderQuota = (window) => {
      if (!window) return "—";
      const reset = window.resetTime ? ` · renova ${new Date(window.resetTime).toLocaleString("pt-BR")}` : "";
      return `${window.percent}%${esc(reset)}`;
    };
    slot.innerHTML = `
      <div class="section-head">
        <div><h2>Antigravity Account Pool</h2><p>Cadastre contas Google e escolha quais podem assumir o projeto na mesma sessão do Antigravity CLI.</p></div>
        ${enabled ? `<span class="pill">${current.simulated ? "Simulado" : "Pool aberto"}</span>` : ""}
      </div>
      ${!current ? "<p>Verificando dependências…</p>" : ""}
      ${current?.issue ? `<p role="status">${esc(current.issue)}</p><button class="btn btn-sm" id="agy-recheck">Verificar conexão</button>` : ""}
      ${current && !current.issue && !enabled ? `
        <p>Ao iniciar, a Central procura o login atual do Antigravity. Depois você pode abrir o login de cada conta por aqui.</p>
        ${unavailable.length ? `<p>Falta instalar ${esc(unavailable.join(", "))}.</p>` : ""}
        <div class="continuity-actions">
          <button class="btn btn-primary" id="agy-start" ${unavailable.length ? "disabled" : disabled}>Iniciar pool Google</button>
          ${unavailable.includes("Antigravity CLI") ? '<a class="btn" href="https://antigravity.google/download" target="_blank" rel="noreferrer">Baixar Antigravity CLI</a>' : ""}
          ${unavailable.length ? '<button class="btn" id="agy-recheck">Verificar instalação</button>' : ""}
        </div>
        <small>Projeto comunitário. A rotação de contas pessoais fica fora do suporte do Google. <a href="https://antigravity.google/docs/faq" target="_blank" rel="noreferrer">FAQ do Google</a></small>` : ""}
      ${enabled ? `
        <div class="continuity-actions">
          <button type="button" class="btn btn-primary" id="agy-add" ${project || pool.enrollment ? "disabled" : disabled}>${accounts.length ? "Entrar com outra conta Google" : "Entrar com a primeira conta Google"}</button>
          <button type="button" class="btn" id="agy-refresh" ${disabled}>Atualizar cotas</button>
          ${pool.enrollment ? `<button type="button" class="btn" id="agy-cancel" ${disabled}>Cancelar cadastro</button>` : ""}
        </div>
        ${pool.enrollment ? `<p role="status">${esc(pool.enrollment.message === "Sign in in the new Antigravity CLI window." ?
          "Faça login na janela do Antigravity que abriu. A conta aparece aqui quando o Google confirmar o login." : pool.enrollment.message)}</p>` : ""}
        ${accounts.length ? `<div class="agy-account-grid">${accounts.map((a) => `
          <article>
            <strong>${esc(a.label)}</strong> · ${esc(a.email)}
            ${project?.account === a.id ? '<span class="pill">Conta ativa</span>' : ""}
            <p>Gemini · 5 h ${renderQuota(a.quotaPools?.gemini?.fiveHour)} · Semana ${renderQuota(a.quotaPools?.gemini?.weekly)}</p>
            <p>Claude/GPT · 5 h ${renderQuota(a.quotaPools?.other?.fiveHour)} · Semana ${renderQuota(a.quotaPools?.other?.weekly)}</p>
            ${a.quotaError ? '<small>Cota ainda indisponível.</small>' : ""}
          </article>`).join("")}</div>` : ""}
        ${project ? `<p id="agy-session">Sessão aberta · ${esc(state.projects.find(p => p.id === project.projectId)?.name || "Projeto")} ·
          ${esc(pool.lastRequestedModel || "Escolha o modelo no Antigravity CLI")}</p><p>Envie o pedido na janela do Antigravity.</p>` :
          accounts.length && !pool.enrollment ? `<div class="agy-project">
            <h3>Abrir projeto no Antigravity</h3>
            <label>Projeto <select class="select" id="agy-project"><option value="">Escolha um projeto</option>${state.projects.map(p =>
              `<option value="${esc(p.id)}" ${p.id === state.agyProject ? "selected" : ""}>${esc(p.name)}</option>`).join("")}</select></label>
            <button type="button" class="btn btn-sm" id="agy-new-project">Novo projeto ou pasta</button>
            <label>Como usar as contas
              <select class="select" id="agy-mode">
                <option value="alternar" ${state.agyMode === "alternar" ? "selected" : ""}>Alternar ao atingir limite</option>
                <option value="fixa" ${state.agyMode === "fixa" ? "selected" : ""}>Fixar uma conta</option>
              </select>
            </label>
            ${selected.length ? `<ol aria-label="Ordem das contas">${selected.map((id) => `<li>${esc(idOf(id).email)}
              <button type="button" class="btn btn-sm" data-agy-unselect="${esc(id)}">Remover da ordem</button></li>`).join("")}</ol>` : ""}
            <label>${state.agyMode === "fixa" ? "Conta do projeto" : "Adicionar conta à ordem"}
              <select class="select" id="agy-choose-account"><option value="">Selecione uma conta</option>${accounts.map(a =>
                selected.includes(a.id) ? "" : `<option value="${esc(a.id)}">${esc(a.email)}</option>`).join("")}</select>
            </label>
            <label><input type="checkbox" id="agy-resume" ${state.agyResume ? "checked" : ""}> Continuar a última conversa desta pasta</label>
            <button type="button" id="agy-launch" class="btn btn-primary"
              ${!state.agyProject || selected.length < (state.agyMode === "fixa" ? 1 : 2) ? "disabled" : disabled}>Abrir projeto no Antigravity CLI</button>
          </div>` : ""}
        ${project && project.accountIds.length > 1 && pool.lastRequestedModel ? `
          <button type="button" class="btn btn-sm" id="agy-test-limit" ${disabled}>Simular limite no próximo pedido</button>
          <small>O próximo pedido real pode usar cota da segunda conta.</small>` : ""}
        ${pool.events?.length ? `<details id="agy-activity" ${activityOpen ? "open" : ""}><summary>Atividade do Antigravity</summary><ul>${pool.events.slice(0, 10)
          .map(e => `<li>${esc(new Date(e.at).toLocaleTimeString("pt-BR"))} · ${esc(e.message)}</li>`).join("")}</ul></details>` : ""}` : ""}`;
    const bind = (id, action) => slot.querySelector(id)?.addEventListener("click", action);
    bind("#agy-start", () => actInAgyPool("start", {}));
    bind("#agy-recheck", refreshAgyPool);
    bind("#agy-add", () => actInAgyPool("add", {}));
    bind("#agy-refresh", () => actInAgyPool("refresh", {}));
    bind("#agy-cancel", () => actInAgyPool("cancel", {}));
    bind("#agy-test-limit", () => actInAgyPool("test-limit", {}, "Teste armado para o próximo pedido."));
    bind("#agy-new-project", () => showNewProjectModal(false, (pid) => { state.agyProject = pid; renderSettings(); }));
    bind("#agy-launch", () => actInAgyPool(`projects/${state.agyProject}/launch`,
      { mode: state.agyMode, accounts: selected, resume: state.agyResume }));
    slot.querySelector("#agy-project")?.addEventListener("change", (event) => { state.agyProject = event.target.value; renderAgyPool(); });
    slot.querySelector("#agy-resume")?.addEventListener("change", (event) => { state.agyResume = event.target.checked; });
    slot.querySelector("#agy-mode")?.addEventListener("change", (event) => {
      state.agyMode = event.target.value;
      if (state.agyMode === "fixa") state.agyAccounts = selected.slice(0, 1);
      renderAgyPool();
    });
    slot.querySelector("#agy-choose-account")?.addEventListener("change", (event) => {
      if (event.target.value) state.agyAccounts = state.agyMode === "fixa" ? [event.target.value] : [...selected, event.target.value].slice(0, 10);
      renderAgyPool();
    });
    slot.querySelectorAll("[data-agy-unselect]").forEach(button => bind(`[data-agy-unselect="${button.dataset.agyUnselect}"]`, () => {
      state.agyAccounts = selected.filter(id => id !== button.dataset.agyUnselect);
      renderAgyPool();
    }));
  }

  function renderSettings() {
    const s = state.settings;
    if (!s) return;
    const allProviders = s.providers || [];
    const googleProfiles = allProviders.filter((p) => ["google-personal", "ensaio"].includes(p.kind));
    const providers = allProviders.filter((p) => !googleProfiles.includes(p));
    const role = state.chainRole;
    const chain = (s.chains && s.chains[role]) || [];
    const strategies = s.strategies || {};
    const curStrategy = strategies[role] || "failover";
    const mediaStrategy = strategies.midia || "roundrobin";
    const media = s.media || {};
    const geminiProviders = providers.filter((p) => p.kind === "gemini");
    const statsById = Object.fromEntries((state.pool || []).map((x) => [x.providerId, x]));
    const main = document.getElementById("main");

    main.innerHTML = `
      <div class="settings-scroll">
        <div class="settings-inner">
          <div class="section-head">
            <div>
              <h1>Contas e modelos</h1>
            </div>
            <div style="display:flex;gap:8px">
              <button class="btn btn-sm" id="btn-rescan">🔍 Escanear PC</button>
              <button class="btn btn-sm" id="btn-refresh-pool">↻ Status</button>
              ${state.cooldowns.length > 0 ? `<button class="btn btn-sm btn-danger" id="btn-reset-all-cd">Zerar pausas (${state.cooldowns.length})</button>` : ""}
            </div>
          </div>

          <section class="card continuity-settings agy-panel" id="antigravity-panel"></section>
          <section class="card continuity-settings">
            <div class="section-head">
              <div><h2>Contas Google pessoais · cadastro manual</h2><p>Perfis de referência para o modo assistido.</p></div>
              <button class="btn" id="btn-add-google">Cadastrar perfil manual</button>
            </div>
            ${googleProfiles.length ? `<ul>${googleProfiles.map((p) => `<li>
              <strong>${esc(p.name)}</strong> · ${esc(p.declaredIdentity || "")} · ${p.kind === "ensaio" ? "Identidade de ensaio" : "Identidade informada · capacidade não observada"}
              <button class="btn btn-sm" data-ptoggle="${esc(p.id)}">${p.enabled ? "Desativar" : "Ativar"}</button>
              <button class="btn btn-sm" data-pdel="${esc(p.id)}">Remover</button></li>`).join("")}</ul>` : ""}
          </section>
          <section class="batch-hero">
            <div>
              <strong>Importar chaves de API</strong>
              <span>Adicione chaves do Google AI Studio, OpenRouter ou Groq, uma por linha.</span>
            </div>
            <button class="btn btn-primary" id="btn-batch-keys">+ Colar várias chaves</button>
          </section>

          <section>
            <div style="font-size:12px;font-weight:650;text-transform:uppercase;letter-spacing:.06em;color:var(--text-3);margin-bottom:8px">🔍 Detectado no seu PC — ativar em 1 clique (sem API key)</div>
            ${!state.discovered
              ? `<div class="card" style="display:flex;align-items:center;gap:10px;color:var(--text-2)"><div class="spinner"></div>Escaneando CLIs instaladas, Ollama e gateways locais…</div>`
              : renderDiscoverCards(state.discovered)}
          </section>

          <section>
            <div style="font-size:12px;font-weight:650;text-transform:uppercase;letter-spacing:.06em;color:var(--text-3);margin-bottom:8px">Ou adicionar manualmente</div>
            <div class="preset-strip">
              ${PRESETS.map((pr, idx) => `
                <button class="preset-btn" data-preset="${idx}">
                  <span class="preset-dot" style="background:${pr.color}"></span>
                  <span>+ ${esc(pr.label)}</span>
                </button>`).join("")}
            </div>
          </section>

          <section>
            ${providers.length === 0
              ? `<div class="card empty-state">Nenhuma conta ativa no Pool ainda. Ative uma CLI detectada acima ou clique em <strong>+ Colar várias chaves</strong>.</div>`
              : `<div class="provider-grid">${providers.map((p) => {
                  const st = statsById[p.id] || { requests: 0, successes: 0, rateLimits: 0, errors: 0, inFlight: 0, paused: [] };
                  const paused = st.paused || [];
                  const ready = p.kind === "cli" || p.hasKey || p.kind === "ollama" || p.baseUrl.includes("127.0.0.1") || p.baseUrl.includes("localhost");
                  return `<div class="provider-card ${p.enabled ? "" : "disabled"}">
                    <div class="provider-top">
                      <div>
                        <strong>${p.kind === "cli" ? "⚡ " : ""}${esc(p.name)}</strong>
                        <span class="provider-url">${p.kind === "cli" ? "CLI oficial instalada no Windows (login nativo)" : esc(p.baseUrl)}</span>
                      </div>
                      <span class="pill ${!p.enabled ? "muted" : paused.length > 0 ? "warn" : ready ? "ok" : "warn"}">
                        ${!p.enabled ? "Desativada" : paused.length > 0 ? "Em pausa (429)" : st.inFlight > 0 ? `⚡ ${st.inFlight} ativa` : ready ? "Pronta" : "Sem chave"}
                      </span>
                    </div>
                    <div style="font-size:12.5px;color:var(--text-2)">
                      Modelo: <code style="font-family:var(--mono);color:var(--text)">${esc(p.defaultModel || "auto")}</code>
                      ${(p.models || []).length > 0 ? ` · ${p.models.length} modelos` : ""}
                    </div>
                    <div class="stat-strip">
                      <span>Reqs: <b>${st.requests}</b></span>
                      <span>OK: <b>${st.successes}</b></span>
                      <span>429: <b style="color:${st.rateLimits > 0 ? "#fbbf24" : "inherit"}">${st.rateLimits}</b></span>
                      <span>Erros: <b>${st.errors}</b></span>
                    </div>
                    ${st.lastError ? `<div style="font-size:11.5px;color:#fca5a5">${esc(st.lastError)}</div>` : ""}
                    <div style="display:flex;flex-wrap:wrap;gap:6px;margin-top:auto">
                      <button class="btn btn-sm" data-ptest="${p.id}">Testar</button>
                      ${p.kind !== "cli" ? `<button class="btn btn-sm" data-pmodels="${p.id}">Modelos</button>` : ""}
                      <button class="btn btn-sm" data-pedit="${p.id}">Editar</button>
                      ${paused.length > 0 ? `<button class="btn btn-sm btn-primary" data-preset-cd="${p.id}">Zerar pausa</button>` : ""}
                      <button class="btn btn-sm btn-ghost" data-ptoggle="${p.id}">${p.enabled ? "Desativar" : "Ativar"}</button>
                      <button class="btn btn-sm btn-ghost" data-pdel="${p.id}" style="color:#fca5a5;margin-left:auto">✕</button>
                    </div>
                  </div>`;
                }).join("")}</div>`}
          </section>

          <section class="chain-box">
            <div class="section-head" style="margin-bottom:12px">
              <div>
                <h2 style="margin:0;font-size:17px">⚡ Estratégia e fila do Pool</h2>
                <p style="margin:3px 0 0;color:var(--text-2);font-size:13px">Escolha como as contas se revezam e qual a ordem de fallback por função.</p>
              </div>
              <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <div class="seg">
                  <button data-crole="codigo" class="${role === "codigo" ? "active" : ""}">💻 Código / Equipe</button>
                  <button data-crole="conversa" class="${role === "conversa" ? "active" : ""}">💬 Conversa</button>
                  <button data-crole="roteiro" class="${role === "roteiro" ? "active" : ""}">🎬 Roteiro</button>
                </div>
                <button class="btn btn-sm" id="btn-autofill-chain" ${providers.length === 0 ? "disabled" : ""}>⚡ Usar todas as contas</button>
              </div>
            </div>

            <div class="strat-bar">
              <span style="font-size:12.5px;color:var(--text-2);font-weight:600">Modo de distribuição:</span>
              <div class="seg">
                <button data-set-strat="roundrobin" class="${curStrategy === "roundrobin" ? "active" : ""}">🔄 Revezamento (Round-Robin)</button>
                <button data-set-strat="leastused" class="${curStrategy === "leastused" ? "active" : ""}">📊 Menor carga</button>
                <button data-set-strat="failover" class="${curStrategy === "failover" ? "active" : ""}">🛡️ Prioridade (Failover)</button>
              </div>
            </div>

            <div class="chain-list">
              ${chain.length === 0
                ? `<div style="padding:14px;color:var(--text-3);font-size:13px;border:1px dashed var(--border-strong);border-radius:10px">Fila vazia — a Central usará o modelo padrão de todas as contas ativas segundo a estratégia acima.</div>`
                : chain.map((r, i) => {
                    const p = providers.find((x) => x.id === r.providerId);
                    return `<div class="chain-row">
                      <span class="chain-num">${i + 1}º</span>
                      <div class="chain-info"><strong>${esc(p?.name || r.providerId)}</strong><small>${esc(r.model)}</small></div>
                      <button class="btn btn-sm btn-ghost" data-cmove="${i}:-1" ${i === 0 ? "disabled" : ""}>↑</button>
                      <button class="btn btn-sm btn-ghost" data-cmove="${i}:1" ${i === chain.length - 1 ? "disabled" : ""}>↓</button>
                      <button class="btn btn-sm btn-ghost" data-crem="${i}" style="color:#fca5a5">✕</button>
                    </div>`;
                  }).join("")}
            </div>

            ${providers.length > 0 ? `
              <div class="chain-add">
                <select class="select" id="ca-prov">${providers.map((p) => `<option value="${p.id}">${esc(p.name)}</option>`).join("")}</select>
                <input class="input" id="ca-model" list="ca-models-list" placeholder="Nome do modelo (ex.: gemini-2.5-pro)">
                <datalist id="ca-models-list"></datalist>
                <button class="btn btn-primary" id="ca-add">+ Adicionar à fila</button>
              </div>` : ""}
          </section>

          <div class="two-col">
            <section class="card">
              <h3>🎬 Flow · Mídia (Pool Gemini paralelo)</h3>
              <div class="field">
                <label>Estratégia entre contas Gemini para imagem, vídeo e voz</label>
                <select class="select" id="sm-strat">
                  <option value="roundrobin" ${mediaStrategy === "roundrobin" ? "selected" : ""}>🔄 Revezamento (espalha cenas entre as contas)</option>
                  <option value="leastused" ${mediaStrategy === "leastused" ? "selected" : ""}>📊 Menor carga</option>
                  <option value="failover" ${mediaStrategy === "failover" ? "selected" : ""}>🛡️ Prioridade (usa a principal até atingir limite)</option>
                </select>
              </div>
              <div class="field"><label>Conta Google principal (para modo Prioridade)</label>
                <select class="select" id="sm-prov">
                  <option value="">Automática</option>
                  ${geminiProviders.map((p) => `<option value="${p.id}" ${media.providerId === p.id ? "selected" : ""}>${esc(p.name)}</option>`).join("")}
                </select>
              </div>
              <div class="field"><label>Modelo de imagem (quadros e retratos)</label><input class="input" id="sm-img" value="${esc(media.imageModel || "")}"></div>
              <div class="field"><label>Modelo de vídeo (Veo)</label><input class="input" id="sm-vid" value="${esc(media.videoModel || "")}"></div>
              <div class="two-col">
                <div class="field"><label>Modelo de voz (TTS)</label><input class="input" id="sm-tts" value="${esc(media.ttsModel || "")}"></div>
                <div class="field"><label>Voz</label>
                  <select class="select" id="sm-voice">${["Kore", "Puck", "Charon", "Fenrir", "Aoede"].map((v) => `<option ${media.voice === v ? "selected" : ""}>${v}</option>`).join("")}</select>
                </div>
                <div class="field"><label>Resolução Veo</label>
                  <select class="select" id="sm-res"><option value="720p" ${media.resolution === "720p" ? "selected" : ""}>720p</option><option value="1080p" ${media.resolution === "1080p" ? "selected" : ""}>1080p</option></select>
                </div>
                <div class="field"><label>Custo estimado / seg (${esc(media.currency || "US$")})</label><input class="input" id="sm-cost" value="${esc(media.videoCostPerSecond ?? 0.4)}"></div>
              </div>
              <label style="display:flex;align-items:center;gap:8px;margin-bottom:12px;font-size:13px;color:var(--text-2);cursor:pointer">
                <input type="checkbox" id="sm-frame" ${media.videoUsesFrame ? "checked" : ""}> Usar o quadro gerado como 1º frame do vídeo
              </label>
              <button class="btn btn-primary btn-sm" id="sm-save">Salvar mídia</button>
            </section>

            <section class="card">
              <h3>🛡️ Segurança e ambiente local</h3>
              <div class="field">
                <label>Execução de comandos PowerShell pelos agentes</label>
                <select class="select" id="sa-mode">
                  <option value="perguntar" ${s.approval !== "automatico" ? "selected" : ""}>Perguntar antes de rodar cada comando (recomendado)</option>
                  <option value="automatico" ${s.approval === "automatico" ? "selected" : ""}>Executar automaticamente dentro do projeto</option>
                </select>
                <small>Chaves de API ficam criptografadas no Cofre do Windows (DPAPI).</small>
              </div>
              <div style="margin-top:16px;padding:12px;border-radius:10px;background:var(--bg);border:1px solid var(--border);font-size:12.5px;color:var(--text-2)">
                <div style="margin-bottom:6px"><strong>Pasta dos dados:</strong><br><code style="font-family:var(--mono);font-size:11.5px;color:var(--text)">${esc(state.dataDir)}</code></div>
                <div><strong>FFmpeg:</strong> ${state.ffmpeg ? '<span style="color:#4ade80">Detectado (xfade + ducking + legendas prontos)</span>' : '<span style="color:#fbbf24">Não encontrado no PATH</span>'}</div>
              </div>
            </section>
          </div>
        </div>
      </div>`;

    document.getElementById("btn-rescan").onclick = async () => { state.discovered = null; renderSettings(); await runDiscover(); };
    document.getElementById("btn-add-google").onclick = showGoogleAccountModal;
    renderAgyPool();
    document.getElementById("btn-batch-keys").onclick = showBatchKeysModal;
    document.getElementById("btn-refresh-pool").onclick = refreshPool;
    main.querySelectorAll("[data-act-cli]").forEach((btn) => {
      btn.onclick = async () => {
        const c = state.discovered.clis.find((x) => x.id === btn.dataset.actCli);
        try {
          state.settings = await api("/providers", { method: "POST", body: { name: c.name, kind: "cli", baseUrl: c.url, defaultModel: "auto", enabled: true } });
          toast(`${c.name} ativada no Pool!`, "ok");
          await Promise.all([refreshPool(), runDiscover()]);
        } catch (err) { toast(err.message, "err"); }
      };
    });
    main.querySelector("[data-act-ollama]")?.addEventListener("click", async () => {
      const o = state.discovered.ollama;
      try {
        state.settings = await api("/providers", { method: "POST", body: { name: "Ollama Local", kind: "ollama", baseUrl: o.url, defaultModel: o.models?.[0] || "gemma3:4b", models: o.models || [], enabled: true } });
        toast("Ollama Local conectado!", "ok");
        await Promise.all([refreshPool(), runDiscover()]);
      } catch (err) { toast(err.message, "err"); }
    });
    main.querySelectorAll("[data-act-gw]").forEach((btn) => {
      btn.onclick = async () => {
        const g = state.discovered.gateways[Number(btn.dataset.actGw)];
        try {
          state.settings = await api("/providers", { method: "POST", body: { name: g.label, kind: "openai", baseUrl: g.url, defaultModel: g.models?.[0] || "", models: g.models || [], enabled: true } });
          toast(`${g.label} conectado!`, "ok");
          await Promise.all([refreshPool(), runDiscover()]);
        } catch (err) { toast(err.message, "err"); }
      };
    });
    main.querySelectorAll("[data-act-env]").forEach((btn) => {
      btn.onclick = async () => {
        try {
          state.settings = await api("/providers/import-env", { method: "POST", body: { envVar: btn.dataset.actEnv } });
          toast(`Chave ${btn.dataset.actEnv} importada com segurança!`, "ok");
          await Promise.all([refreshPool(), runDiscover()]);
        } catch (err) { toast(err.message, "err"); }
      };
    });
    document.getElementById("btn-reset-all-cd")?.addEventListener("click", async () => {
      const r = await api("/pool/reset-cooldown", { method: "POST", body: {} });
      state.cooldowns = r.cooldowns; state.pool = r.pool;
      toast("Todas as pausas foram zeradas.", "ok");
      renderSidebar(); renderSettings();
    });
    main.querySelectorAll("[data-preset]").forEach((btn) => { btn.onclick = () => showProviderModal(PRESETS[Number(btn.dataset.preset)], null); });
    main.querySelectorAll("[data-pedit]").forEach((btn) => { btn.onclick = () => showProviderModal(null, providers.find((x) => x.id === btn.dataset.pedit)); });
    main.querySelectorAll("[data-preset-cd]").forEach((btn) => {
      btn.onclick = async () => {
        const r = await api("/pool/reset-cooldown", { method: "POST", body: { providerId: btn.dataset.presetCd } });
        state.cooldowns = r.cooldowns; state.pool = r.pool;
        renderSidebar(); renderSettings();
      };
    });
    main.querySelectorAll("[data-ptoggle]").forEach((btn) => {
      btn.onclick = async () => {
        const p = allProviders.find((x) => x.id === btn.dataset.ptoggle);
        state.settings = await api("/providers", { method: "POST", body: { id: p.id, enabled: !p.enabled } });
        renderSidebar(); renderSettings();
      };
    });
    main.querySelectorAll("[data-pdel]").forEach((btn) => {
      btn.onclick = async () => {
        const p = allProviders.find((x) => x.id === btn.dataset.pdel);
        if (!confirm(`Remover a conexão “${p.name}”?`)) return;
        state.settings = await api(`/providers/${p.id}`, { method: "DELETE" });
        renderSidebar(); renderSettings();
      };
    });
    main.querySelectorAll("[data-pmodels]").forEach((btn) => {
      btn.onclick = async () => {
        btn.disabled = true; btn.textContent = "…";
        try { state.settings = await api(`/providers/${btn.dataset.pmodels}/models`, { method: "POST" }); toast("Modelos atualizados!", "ok"); renderSettings(); }
        catch (err) { toast(err.message, "err"); btn.disabled = false; btn.textContent = "Modelos"; }
      };
    });
    main.querySelectorAll("[data-ptest]").forEach((btn) => {
      btn.onclick = async () => {
        btn.disabled = true; btn.textContent = "…";
        try {
          const r = await api(`/providers/${btn.dataset.ptest}/test`, { method: "POST", body: {} });
          toast(r.ok ? `✓ Conectado em ${r.ms} ms: ${r.reply}` : `✕ Falhou: ${r.reply}`, r.ok ? "ok" : "err");
        } catch (err) { toast(err.message, "err"); }
        btn.disabled = false; btn.textContent = "Testar";
      };
    });

    main.querySelectorAll("[data-crole]").forEach((btn) => { btn.onclick = () => { state.chainRole = btn.dataset.crole; renderSettings(); }; });
    main.querySelectorAll("[data-set-strat]").forEach((btn) => {
      btn.onclick = async () => {
        state.settings = await api(`/strategies/${role}`, { method: "PUT", body: { strategy: btn.dataset.setStrat } });
        toast("Estratégia atualizada!", "ok");
        renderSettings();
      };
    });
    main.querySelectorAll("[data-cmove]").forEach((btn) => {
      btn.onclick = async () => {
        const [i, d] = btn.dataset.cmove.split(":").map(Number);
        const copy = [...chain]; const j = i + d;
        [copy[i], copy[j]] = [copy[j], copy[i]];
        state.settings = await api(`/chains/${role}`, { method: "PUT", body: copy });
        renderSettings();
      };
    });
    main.querySelectorAll("[data-crem]").forEach((btn) => {
      btn.onclick = async () => {
        state.settings = await api(`/chains/${role}`, { method: "PUT", body: chain.filter((_, i) => i !== Number(btn.dataset.crem)) });
        renderSettings();
      };
    });

    document.getElementById("btn-autofill-chain")?.addEventListener("click", async () => {
      const list = activeProviders().filter((p) => p.defaultModel).map((p) => ({ providerId: p.id, model: p.defaultModel }));
      if (list.length === 0) { toast("Configure um modelo padrão nas contas ativas primeiro.", "err"); return; }
      state.settings = await api(`/chains/${role}`, { method: "PUT", body: list });
      toast(`Fila preenchida com ${list.length} conta(s)!`, "ok");
      renderSettings();
    });

    const provSel = document.getElementById("ca-prov");
    const modInp = document.getElementById("ca-model");
    const dl = document.getElementById("ca-models-list");
    if (provSel) {
      const syncModels = () => {
        const p = providers.find((x) => x.id === provSel.value);
        if (!p) return;
        const models = [...new Set([p.defaultModel, ...(p.models || [])].filter(Boolean))];
        dl.innerHTML = models.map((m) => `<option value="${esc(m)}">`).join("");
        modInp.value = p.defaultModel || models[0] || "";
      };
      provSel.onchange = syncModels;
      syncModels();
      document.getElementById("ca-add").onclick = async () => {
        const model = modInp.value.trim();
        if (!model) { toast("Informe o nome do modelo.", "err"); return; }
        state.settings = await api(`/chains/${role}`, { method: "PUT", body: [...chain, { providerId: provSel.value, model }] });
        renderSettings();
      };
    }

    document.getElementById("sm-strat").onchange = async (e) => {
      state.settings = await api("/strategies/midia", { method: "PUT", body: { strategy: e.target.value } });
      toast("Estratégia de mídia atualizada!", "ok");
    };
    document.getElementById("sm-save").onclick = async () => {
      try {
        state.settings = await api("/settings/media", {
          method: "PUT",
          body: {
            providerId: document.getElementById("sm-prov").value,
            imageModel: document.getElementById("sm-img").value,
            videoModel: document.getElementById("sm-vid").value,
            ttsModel: document.getElementById("sm-tts").value,
            voice: document.getElementById("sm-voice").value,
            resolution: document.getElementById("sm-res").value,
            videoCostPerSecond: document.getElementById("sm-cost").value,
            videoUsesFrame: document.getElementById("sm-frame").checked,
          },
        });
        toast("Configurações de mídia salvas!", "ok");
      } catch (err) { toast(err.message, "err"); }
    };
    document.getElementById("sa-mode").onchange = async (e) => {
      state.settings = await api("/settings/approval", { method: "PUT", body: { approval: e.target.value } });
      toast("Modo de segurança atualizado.", "ok");
    };
  }

  function renderDiscoverCards(d) {
    const cards = [];
    for (const c of (d.clis || [])) {
      cards.push(`<div class="discover-card ${c.installed ? "ready" : "dim"}">
        <div style="display:flex;justify-content:space-between;align-items:center;gap:6px">
          <strong>${esc(c.name)}</strong>
          <span class="pill ${c.connected ? "ok" : c.installed ? "ok" : "muted"}">${c.connected ? "✓ No Pool" : c.installed ? "Instalada" : "Não instalada"}</span>
        </div>
        <small>${esc(c.hint)}</small>
        <div style="margin-top:auto;padding-top:4px">
          ${c.connected ? `<span style="font-size:12px;color:#4ade80">Pronta no modo Agente e Equipe</span>`
            : c.installed ? `<button class="btn btn-sm btn-flow" data-act-cli="${c.id}">Ativar em 1 clique</button>`
            : `<span style="font-size:11.5px;color:var(--text-3)">Instale no PATH e clique Escanear</span>`}
        </div>
      </div>`);
    }
    if (d.ollama) {
      const o = d.ollama;
      cards.push(`<div class="discover-card ${o.running ? "ready" : "dim"}">
        <div style="display:flex;justify-content:space-between;align-items:center;gap:6px">
          <strong>Ollama Local</strong>
          <span class="pill ${o.connected ? "ok" : o.running ? "ok" : "muted"}">${o.connected ? "✓ No Pool" : o.running ? `${o.models.length} modelos` : o.installed ? "Fechado" : "Não instalado"}</span>
        </div>
        <small>${o.running ? `Rodando com: ${o.models.slice(0, 3).join(", ") || "nenhum modelo"}` : "Modelos locais gratuitos e offline no seu Windows."}</small>
        <div style="margin-top:auto;padding-top:4px">
          ${o.connected ? `<span style="font-size:12px;color:#4ade80">Conectado</span>`
            : o.running ? `<button class="btn btn-sm btn-flow" data-act-ollama>Conectar em 1 clique</button>`
            : `<span style="font-size:11.5px;color:var(--text-3)">Abra o Ollama e clique Escanear</span>`}
        </div>
      </div>`);
    }
    (d.gateways || []).forEach((g, idx) => {
      cards.push(`<div class="discover-card ready">
        <div style="display:flex;justify-content:space-between;align-items:center;gap:6px">
          <strong>${esc(g.label)}</strong>
          <span class="pill ok">${g.connected ? "✓ No Pool" : `${g.models.length} modelos`}</span>
        </div>
        <small>Gateway local detectado em <code>${esc(g.url)}</code></small>
        <div style="margin-top:auto;padding-top:4px">
          ${g.connected ? `<span style="font-size:12px;color:#4ade80">Conectado</span>`
            : `<button class="btn btn-sm btn-flow" data-act-gw="${idx}">Conectar gateway</button>`}
        </div>
      </div>`);
    });
    for (const e of (d.envs || [])) {
      cards.push(`<div class="discover-card ready">
        <div style="display:flex;justify-content:space-between;align-items:center;gap:6px">
          <strong>${esc(e.name)}</strong>
          <span class="pill ok">Variável Windows</span>
        </div>
        <small>Chave detectada em <code>${esc(e.envVar)}</code> — importe sem copiar.</small>
        <div style="margin-top:auto;padding-top:4px">
          <button class="btn btn-sm btn-primary" data-act-env="${esc(e.envVar)}">Importar chave</button>
        </div>
      </div>`);
    }
    return `<div class="discover-grid">${cards.join("")}</div>`;
  }

  function showGoogleAccountModal() {
    openModal(`
      <h2>Cadastrar perfil Google</h2>
      <p class="modal-sub">Salve o nome e o e-mail para identificar o perfil na Central. O Antigravity oficial abre o login do Google e mostra a conta ativa.</p>
      <form id="google-profile-form">
        <div class="field"><label for="google-name">Nome do perfil</label><input class="input" id="google-name" maxlength="60" required></div>
        <div class="field"><label for="google-identity">E-mail Google</label><input class="input" id="google-identity" type="email" maxlength="254" required></div>
        <div class="modal-foot">
          <a class="btn" target="_blank" rel="noreferrer" href="https://antigravity.google/">Abrir cliente oficial</a>
          <button class="btn btn-primary">Salvar perfil</button>
        </div>
      </form>`, (box, close) => {
      box.querySelector("form").onsubmit = async (event) => {
        event.preventDefault();
        try {
          const r = await api("/google-accounts", { method: "POST", body: { name: box.querySelector("#google-name").value,
            identity: box.querySelector("#google-identity").value } });
          state.settings = r.settings;
          close();
          if (state.view === "settings") renderSettings();
          else { state.homeAccounts = [r.profile.id]; renderExecutionOptions(); }
        } catch (err) { toast(err.message, "err"); }
      };
    });
  }

  function showBatchKeysModal() {
    const opts = [
      { label: "Google Gemini (AI Studio)", kind: "gemini", prefix: "Gemini", url: "https://generativelanguage.googleapis.com/v1beta/openai", model: "gemini-2.5-flash", link: "https://aistudio.google.com/apikey", linkText: "Abrir Google AI Studio para copiar chaves ↗" },
      { label: "OpenRouter", kind: "openrouter", prefix: "OpenRouter", url: "https://openrouter.ai/api/v1", model: "anthropic/claude-sonnet-4", link: "https://openrouter.ai/keys", linkText: "Abrir OpenRouter Keys ↗" },
      { label: "Groq", kind: "groq", prefix: "Groq", url: "https://api.groq.com/openai/v1", model: "llama-3.3-70b-versatile", link: "https://console.groq.com/keys", linkText: "Abrir Groq Console ↗" },
      { label: "OpenAI", kind: "openai", prefix: "OpenAI", url: "https://api.openai.com/v1", model: "gpt-4.1", link: "https://platform.openai.com/api-keys", linkText: "Abrir OpenAI Keys ↗" },
      { label: "DeepSeek", kind: "deepseek", prefix: "DeepSeek", url: "https://api.deepseek.com/v1", model: "deepseek-chat", link: "https://platform.deepseek.com/api_keys", linkText: "Abrir DeepSeek Keys ↗" },
    ];
    openModal(`
      <h2>⚡ Adicionar várias contas de uma vez</h2>
      <p class="modal-sub">Cole uma chave por linha (ou <code>Nome = chave</code>). A Central salva no Cofre do Windows, testa todas em paralelo e liga o Revezamento automático.</p>
      <div class="field"><label>Serviço</label>
        <select class="select" id="bk-svc">${opts.map((o, i) => `<option value="${i}">${esc(o.label)}</option>`).join("")}</select>
        <small><a id="bk-link" href="${opts[0].link}" target="_blank" rel="noopener">${opts[0].linkText}</a></small>
      </div>
      <div class="field"><label>Modelo padrão</label><input class="input" id="bk-model" value="${opts[0].model}"></div>
      <div class="field"><label>Chaves (1 por linha)</label>
        <textarea class="textarea" id="bk-keys" rows="6" placeholder="AIzaSy... (Conta 1)&#10;AIzaSy... (Conta 2)&#10;Conta Trabalho = AIzaSy..."></textarea>
      </div>
      <div id="bk-res" style="font-size:12.5px"></div>
      <div class="modal-foot">
        <button class="btn" id="bk-c">Fechar</button>
        <button class="btn btn-primary" id="bk-go">Conectar e testar todas</button>
      </div>`, (box, close) => {
      const sel = box.querySelector("#bk-svc");
      const link = box.querySelector("#bk-link");
      const mod = box.querySelector("#bk-model");
      sel.onchange = () => {
        const o = opts[Number(sel.value)];
        link.href = o.link; link.textContent = o.linkText; mod.value = o.model;
      };
      box.querySelector("#bk-c").onclick = close;
      box.querySelector("#bk-go").onclick = async () => {
        const o = opts[Number(sel.value)];
        const keys = box.querySelector("#bk-keys").value.split("\n").map((x) => x.trim()).filter(Boolean);
        if (keys.length === 0) { toast("Cole pelo menos uma chave.", "err"); return; }
        const btn = box.querySelector("#bk-go");
        const out = box.querySelector("#bk-res");
        btn.disabled = true; btn.textContent = `Testando ${keys.length} conta(s)…`;
        out.innerHTML = `<div class="status-line"><div class="spinner"></div>Conectando e validando em paralelo…</div>`;
        try {
          const r = await api("/providers/batch", {
            method: "POST",
            body: { kind: o.kind, prefix: o.prefix, baseUrl: o.url, defaultModel: mod.value.trim(), keys, test: true },
          });
          state.settings = r.settings;
          state.pool = r.pool;
          out.innerHTML = r.results.map((x) =>
            `<div style="padding:4px 0;color:${x.ok ? "#4ade80" : "#fca5a5"}">${x.ok ? "✓" : "✕"} <strong>${esc(x.name)}</strong> — ${esc(x.reply)} ${x.ms ? `(${x.ms} ms)` : ""}</div>`).join("");
          toast(`${r.added} conta(s) adicionada(s) ao Pool com Revezamento ativo!`, "ok");
          renderSidebar(); renderSettings();
          btn.disabled = false; btn.textContent = "Conectar mais";
        } catch (err) {
          out.innerHTML = `<div style="color:#fca5a5">${esc(err.message)}</div>`;
          btn.disabled = false; btn.textContent = "Conectar e testar todas";
        }
      };
    });
  }

  function showProviderModal(preset, existing) {
    const sameKindCount = (state.settings?.providers || []).filter((p) => p.kind === preset?.kind).length;
    const defaultName = existing ? existing.name : (sameKindCount > 0 ? `${preset.name} ${sameKindCount + 1}` : preset.name);
    const kind = existing?.kind || preset.kind;
    const models = existing?.models || preset?.models || [];
    const showUrlOpen = !preset || preset.label === "Gateway / Custom";
    openModal(`
      <h2>${existing ? "Editar conexão" : `Conectar ${esc(preset.label)}`}</h2>
      <p class="modal-sub">${esc(preset?.hint || "Sua chave fica salva criptografada no Cofre do Windows.")}</p>
      <div class="field"><label>Nome da conta</label><input class="input" id="mp-name" value="${esc(defaultName)}" placeholder="Ex.: Gemini Conta 1"></div>
      ${kind !== "ollama" && kind !== "cli" ? `<div class="field"><label>Chave de API${existing?.hasKey ? " — deixe vazio para manter" : showUrlOpen ? " (opcional)" : ""}</label><input class="input" type="password" id="mp-key" placeholder="Cole sua chave aqui"></div>` : ""}
      <div class="field"><label>Modelo padrão</label><input class="input" id="mp-model" list="mp-mlist" value="${esc(existing?.defaultModel ?? preset.defaultModel)}" placeholder="${kind === "cli" ? "auto" : "Ex.: gemini-2.5-flash"}"><datalist id="mp-mlist">${models.map((m) => `<option value="${esc(m)}">`).join("")}</datalist></div>
      <details class="adv-details" ${showUrlOpen ? "open" : ""}>
        <summary>Avançado (endereço da API)</summary>
        <div class="field" style="margin:8px 0 0"><label>Endereço</label><input class="input" id="mp-url" value="${esc(existing?.baseUrl ?? preset.baseUrl)}" placeholder="https://.../v1"></div>
      </details>
      <div class="modal-foot"><button class="btn" id="mp-c">Cancelar</button><button class="btn btn-primary" id="mp-save">Salvar e testar</button></div>`, (box, close) => {
      box.querySelector("#mp-c").onclick = close;
      box.querySelector("#mp-save").onclick = async () => {
        const body = {
          id: existing?.id || "", name: box.querySelector("#mp-name").value.trim(), kind,
          baseUrl: box.querySelector("#mp-url").value.trim(), defaultModel: box.querySelector("#mp-model").value.trim(),
          enabled: existing ? existing.enabled : true,
        };
        if (!existing && models.length > 0) body.models = models;
        const keyEl = box.querySelector("#mp-key");
        if (keyEl && keyEl.value.trim()) body.apiKey = keyEl.value.trim();
        const btn = box.querySelector("#mp-save");
        btn.disabled = true; btn.textContent = "Salvando…";
        try {
          state.settings = await api("/providers", { method: "POST", body });
          const saved = existing ? state.settings.providers.find((x) => x.id === existing.id) : state.settings.providers[state.settings.providers.length - 1];
          close();
          toast("Conexão salva! Testando…");
          await Promise.all([refreshPool(), runDiscover()]);
          if (state.view === "settings") renderSettings();
          else if (state.view === "home") renderHome();
          if (saved) {
            const tr = await api(`/providers/${saved.id}/test`, { method: "POST", body: {} });
            toast(tr.ok ? `✓ ${saved.name}: ${tr.reply} (${tr.ms} ms)` : `⚠️ ${saved.name}: ${tr.reply}`, tr.ok ? "ok" : "err");
          }
        } catch (err) { toast(err.message, "err"); btn.disabled = false; btn.textContent = "Salvar e testar"; }
      };
    });
  }

  function showNewProjectModal(focusPath = false, onCreated) {
    openModal(`
      <h2>Novo projeto</h2>
      <p class="modal-sub">Use uma pasta gerenciada automaticamente ou vincule qualquer repositório/pasta já existente no seu computador.</p>
      <div class="field"><label>Nome do projeto</label><input class="input" id="np-name" placeholder="Ex.: Meu SaaS ou Série Cyberpunk"></div>
      <div class="field"><label>Pasta existente no PC (opcional)</label><input class="input" id="np-path" placeholder="Ex.: E:\\Projetos\\MeuApp (vazio = cria pasta automática)"><small>Se informar uma pasta existente, o agente e a aba Git trabalham direto nela.</small></div>
      <div class="modal-foot"><button class="btn" id="np-c">Cancelar</button><button class="btn btn-primary" id="np-ok">Criar projeto</button></div>`, (box, close) => {
      const inp = box.querySelector(focusPath ? "#np-path" : "#np-name");
      setTimeout(() => inp.focus(), 20);
      const go = async () => {
        const customPath = box.querySelector("#np-path").value.trim();
        let name = box.querySelector("#np-name").value.trim();
        if (!name && customPath) name = customPath.replace(/[\\/]+$/, "").split(/[\\/]/).pop();
        if (!name) { toast("Informe o nome do projeto.", "err"); return; }
        try {
          const p = await api("/projects", { method: "POST", body: { name, kind: "geral", customPath } });
          state.expanded.add(p.id);
          await refreshState();
          close();
          if (onCreated) { onCreated(p.id); return; }
          const t = await api(`/projects/${p.id}/threads`, { method: "POST", body: { mode: "agente" } });
          await loadProjectDetails(p.id, true);
          openThread(p.id, t.id);
        } catch (err) { toast(err.message, "err"); }
      };
      box.querySelector("#np-name").onkeydown = (e) => { if (e.key === "Enter") go(); };
      box.querySelector("#np-path").onkeydown = (e) => { if (e.key === "Enter") go(); };
      box.querySelector("#np-c").onclick = close;
      box.querySelector("#np-ok").onclick = go;
    });
  }

  // ---------- Startup ----------

  document.getElementById("btn-home").onclick = goHome;
  document.querySelector('[data-nav="home"]').onclick = goHome;
  document.querySelector('[data-nav="settings"]').onclick = goSettings;
  document.getElementById("btn-new-project").onclick = () => showNewProjectModal(false);
  document.getElementById("btn-new-project-2").onclick = () => showNewProjectModal(false);

  (async () => {
    try {
      await refreshState();
      const h = location.hash;
      const tm = h.match(/^#\/p\/([a-z0-9]+)\/c\/([a-z0-9]+)$/);
      const fm = h.match(/^#\/p\/([a-z0-9]+)\/f\/([a-z0-9]+)$/);
      if (h === "#/contas") goSettings();
      else if (tm) openThread(tm[1], tm[2]);
      else if (fm) openFlow(fm[1], fm[2]);
      else goHome();
    } catch (err) {
      document.getElementById("main").innerHTML = `<div class="empty-state">Erro ao iniciar a Central IA: ${esc(err.message)}</div>`;
    }
  })();
})();
