import '../styles.css'
import './orbita.css'

import { AssistantRuntimeProvider, type ThreadMessage, useExternalStoreRuntime } from '@assistant-ui/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ArrowLeft, ArrowUp, CircleStop, ExternalLink, Globe, Layers, Paperclip, Plus, RefreshCw, X } from 'lucide-react'
import { type FormEvent, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { HashRouter } from 'react-router'

import { Thread } from '@/components/assistant-ui/thread'
import { RootErrorBoundary } from '@/components/error-boundary'
import { RootTooltipProvider } from '@/components/ui/tooltip'
import { I18nProvider } from '@/i18n'
import { modePref, ThemeProvider } from '@/themes/context'

interface Project { id: string; name: string; workspace: string }
interface Mission { id: string; projectId: string; name: string; workspace: string; state: string; updated: string; pending?: string; fullAccess?: boolean; requestedModel?: string; accountIds?: string[] }
interface Snapshot { version: string; projects: Project[]; missions: Mission[] }
interface Attachment { name: string }
interface Entry { sequence: number; at: string; kind: string; text: string; data?: { attachments?: Attachment[] } }
interface Packet { missionId: string; items?: Entry[]; disconnected?: boolean; error?: string }
interface Changes { status: string; diff: string; revision: string }
interface Pool { accounts: { id: string; label: string }[] }

declare global {
  interface Window {
    orbita: {
      call<T>(action: string, payload?: Record<string, unknown>): Promise<T>
      watch(id: string, after: number): void
      onEvents(callback: (packet: Packet) => void): () => void
      preview(action: string, value?: string | { x: number; y: number; width: number; height: number }): Promise<string | undefined>
    }
  }
}

const api = window.orbita
const query = new QueryClient()
modePref.assign('default', 'dark')
const emptyEntries: Entry[] = []
const label: Record<string, string> = { pronta: 'Pronta', concluida: 'Concluída', executando: 'Executando', conversando: 'Respondendo',
  'precisa-de-voce': 'Precisa de você', pendente: 'Pendente', 'aguardando-cota': 'Aguardando cota', 'aguardando-verificacao': 'Aguardando verificação do Google', interrompida: 'Interrompida', aceita: 'Diff aceito', revertida: 'Revertida' }
const waiting = (mission?: Mission) => mission?.state === 'executando' || mission?.state === 'conversando'
const detail = (error: unknown) => error instanceof Error ? error.message : String(error)

function userMessage(id: string, text: string, createdAt = new Date()): ThreadMessage {
  return { id, role: 'user', createdAt, content: [{ type: 'text', text }], attachments: [], metadata: { custom: {} } }
}

function asMessages(entries: Entry[]): ThreadMessage[] {
  const messages: ThreadMessage[] = []
  let assistant: { entry: Entry; text: string } | undefined
  const flush = () => {
    if (!assistant) return
    messages.push({ id: `a-${assistant.entry.sequence}`, role: 'assistant', createdAt: new Date(assistant.entry.at),
      content: [{ type: 'text', text: assistant.text }], status: { type: 'complete', reason: 'stop' },
      metadata: { unstable_state: null, unstable_annotations: [], unstable_data: [], steps: [], custom: {} } })
    assistant = undefined
  }
  for (const entry of entries) {
    if (entry.kind === 'usuario') {
      flush()
      const files = entry.data?.attachments?.map(({ name }) => `📎 ${name}`).join('\n')
      messages.push(userMessage(`u-${entry.sequence}`, files ? `${entry.text}\n\n${files}` : entry.text, new Date(entry.at)))
    } else if (entry.kind === 'mensagem') {
      if (assistant) assistant.text += entry.text
      else assistant = { entry, text: entry.text }
    }
  }
  flush()
  return messages
}

function Transcript({ id, entries, optimistic, running, cancel }: { id: string; entries: Entry[]; optimistic: string; running: boolean; cancel: () => Promise<void> }) {
  const messages = useMemo(() => {
    const items = asMessages(entries)
    if (optimistic) items.push(userMessage('draft-sending', optimistic))
    const last = items.at(-1)
    if (running && last?.role === 'assistant') items[items.length - 1] = { ...last, status: { type: 'running' } }
    return items
  }, [entries, optimistic, running])
  const runtime = useExternalStoreRuntime({ messages, isRunning: running, onNew: async () => {}, onCancel: cancel })
  return (
    <div className="orbita-transcript" aria-label="Conversa">
      {messages.length || running ? <AssistantRuntimeProvider runtime={runtime}>
        <Thread sessionKey={id} onCancel={cancel} />
      </AssistantRuntimeProvider> : <div className="orbita-empty"><h2>Como posso ajudar neste projeto?</h2><p>Converse com o Antigravity ou peça para ele executar a missão.</p></div>}
    </div>
  )
}

function Preview({ close, report }: { close: () => void; report: (text: string) => void }) {
  const target = useRef<HTMLDivElement>(null)
  const [url, setUrl] = useState(() => sessionStorage.getItem('orbita-preview-url') || '')
  const [loaded, setLoaded] = useState(Boolean(sessionStorage.getItem('orbita-preview-url')))
  useLayoutEffect(() => {
    const element = target.current
    if (!element || !loaded) return
    const resize = () => {
      const { x, y, width, height } = element.getBoundingClientRect()
      void api.preview('bounds', { x, y, width, height }).catch((error) => report(detail(error)))
    }
    const observer = new ResizeObserver(resize)
    observer.observe(element)
    window.addEventListener('resize', resize)
    resize()
    return () => { observer.disconnect(); window.removeEventListener('resize', resize); void api.preview('hide') }
  }, [loaded, report])

  async function navigate(event: FormEvent) {
    event.preventDefault()
    try {
      await api.preview('open', url.trim())
      sessionStorage.setItem('orbita-preview-url', url.trim())
      setLoaded(true)
    } catch (error) { report(detail(error)) }
  }

  return <section className="orbita-preview" aria-label="Navegador do projeto">
    <form onSubmit={(event) => void navigate(event)}>
      <button type="button" aria-label="Voltar página" onClick={() => void api.preview('back')}><ArrowLeft size={16} /></button>
      <button type="button" aria-label="Recarregar página" onClick={() => void api.preview('reload')}><RefreshCw size={15} /></button>
      <input aria-label="Endereço do projeto" placeholder="http://127.0.0.1:3000" value={url} onChange={(event) => setUrl(event.target.value)} />
      <button type="submit" aria-label="Abrir página" title="Abrir página"><ExternalLink size={16} /></button>
      <button type="button" aria-label="Fechar navegador" onClick={close}><X size={16} /></button>
    </form>
    <div className="orbita-preview-view" ref={target}>{!loaded && <p>Abra a página local do projeto para testar ao lado do chat.</p>}</div>
  </section>
}

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot>()
  const refreshSequence = useRef(0)
  const [projectId, setProject] = useState(() => localStorage.getItem('orbita-project') || '')
  const [missionId, setMission] = useState(() => localStorage.getItem('orbita-mission') || '')
  const [advanced, setAdvanced] = useState(() => localStorage.getItem('orbita-advanced') === '1')
  const [pane, setPane] = useState<'chat' | 'activity' | 'diff' | 'accounts'>('chat')
  const [browser, setBrowser] = useState(false)
  const [transcript, setTranscript] = useState<{ id: string; entries: Entry[] }>()
  const cache = useRef(new Map<string, Entry[]>())
  const [optimistic, setOptimistic] = useState('')
  const submission = useRef<{ missionId: string; after: number } | null>(null)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState('')
  const [modal, setModal] = useState<'project' | 'mission' | 'app' | undefined>()
  const [changes, setChanges] = useState<Changes>()
  const [pool, setPool] = useState<Pool>()
  const [model, setModel] = useState('')
  const [full, setFull] = useState(false)
  const [accounts, setAccounts] = useState<string[]>([])
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [filesByMission, setFilesByMission] = useState<Record<string, File[]>>({})
  const draft = drafts[missionId] ?? localStorage.getItem(`orbita-draft:${missionId}`) ?? ''
  const selectedFiles = filesByMission[missionId] ?? []
  const records = transcript?.id === missionId ? transcript.entries : cache.current.get(missionId) || emptyEntries
  const picker = useRef<HTMLInputElement>(null)
  const selectedProject = snapshot?.projects.find((project) => project.id === projectId)
  const mission = snapshot?.missions.find((item) => item.id === missionId && item.projectId === projectId)
  const busy = sending || !!snapshot?.missions.some(waiting)

  const refresh = useCallback(async () => {
    const sequence = ++refreshSequence.current
    const next = await api.call<Snapshot>('state')
    if (sequence !== refreshSequence.current) return next
    setSnapshot(next)
    setProject((current) => next.projects.some(({ id }) => id === current) ? current : next.projects[0]?.id || '')
    return next
  }, [])
  const report = useCallback((text: string) => setError(text), [])
  const guard = useCallback((operation: () => Promise<unknown>) => { setError(''); void operation().catch((failure) => report(detail(failure))) }, [report])

  useEffect(() => {
    guard(refresh)
    const sync = () => { void refresh().catch(() => undefined) }
    window.addEventListener('focus', sync)
    return () => window.removeEventListener('focus', sync)
  }, [guard, refresh])
  useEffect(() => { localStorage.setItem('orbita-project', projectId); localStorage.setItem('orbita-mission', missionId) }, [projectId, missionId])
  useEffect(() => { localStorage.setItem('orbita-advanced', advanced ? '1' : '0') }, [advanced])
  useEffect(() => {
    setOptimistic('')
    if (!missionId) return
    let reconnect: number | undefined
    const subscribe = () => api.watch(missionId, cache.current.get(missionId)?.at(-1)?.sequence || 0)
    const release = api.onEvents((packet) => {
      if (packet.missionId !== missionId) return
      if (packet.disconnected) { reconnect = window.setTimeout(subscribe, 2000); return }
      const before = cache.current.get(missionId) || []
      const last = before.at(-1)?.sequence || 0
      const batch = packet.items?.filter((item) => item.sequence > last) || []
      if (!batch.length) return
      const next = [...before, ...batch]
      cache.current.set(missionId, next)
      setTranscript({ id: missionId, entries: next })
      if (submission.current?.missionId === missionId && batch.some((item) => item.kind === 'usuario' && item.sequence > (submission.current?.after || 0))) {
        submission.current = null
        setOptimistic('')
      }
      if (batch.some((item) => ['estado', 'sessao'].includes(item.kind))) void refresh().catch((failure) => report(detail(failure)))
    })
    subscribe()
    return () => { release(); window.clearTimeout(reconnect); api.watch('', 0) }
  }, [missionId, refresh, report])
  useEffect(() => {
    setModel(mission?.requestedModel || '')
    setFull(!!mission?.fullAccess)
    setAccounts(mission?.accountIds || [])
  }, [mission?.id])
  useEffect(() => {
    if (pane === 'diff' && missionId) guard(async () => setChanges(await api.call<Changes>('diff', { missionId })))
    if (pane === 'accounts') guard(async () => setPool(await api.call<Pool>('pool')))
  }, [pane, missionId, guard])
  useEffect(() => { if (modal) void api.preview('hide') }, [modal])

  function switchMission(id: string) { setMission(id); setPane('chat'); setChanges(undefined); setError('') }
  function changeDraft(text: string) {
    setDrafts((before) => ({ ...before, [missionId]: text }))
    localStorage.setItem(`orbita-draft:${missionId}`, text)
  }
  function changeFiles(files: File[]) {
    const mb = 1024 * 1024
    if (files.length > 6 || files.some(({ size }) => size > 5 * mb || size === 0) || files.reduce((total, { size }) => total + size, 0) > 10 * mb) {
      report('Use até 6 anexos, 5 MB por arquivo e 10 MB no total.')
      return
    }
    setFilesByMission((before) => ({ ...before, [missionId]: files }))
  }

  async function send(execute: boolean) {
    if (!mission || busy || (!draft.trim() && !selectedFiles.length)) return
    const text = draft.trim()
    const sendId = mission.id
    const after = cache.current.get(sendId)?.at(-1)?.sequence || 0
    submission.current = { missionId: sendId, after }
    setOptimistic([text, ...selectedFiles.map(({ name }) => `📎 ${name}`)].filter(Boolean).join('\n'))
    setSending(true)
    try {
      const files = await Promise.all(selectedFiles.map(async (file) => ({ name: file.name, bytes: new Uint8Array(await file.arrayBuffer()) })))
      const result = await api.call<Mission>('send', { missionId: sendId, text, execute, model, fullAccess: full, accounts, files })
      setSnapshot((previous) => previous && { ...previous, missions: previous.missions.map((item) => item.id === result.id && result.updated >= item.updated ? result : item) })
      changeDraft('')
      changeFiles([])
    } catch (failure) { submission.current = null; setOptimistic(''); throw failure }
    finally { setSending(false) }
  }

  const cancel = useCallback(async () => { await api.call('cancel', { missionId }); await refresh() }, [missionId, refresh])

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const values = Object.fromEntries(new FormData(event.currentTarget))
    if (modal === 'project') {
      const result = await api.call<Project>('project', values)
      await refresh()
      setProject(result.id)
      switchMission('')
    } else if (modal === 'mission') {
      const result = await api.call<Mission>('mission', { ...values, projectId })
      await refresh()
      switchMission(result.id)
    } else {
      const result = await api.call<{ message: string }>('open-app', { ...values, missionId })
      report(result.message)
    }
    setModal(undefined)
  }

  return <div className="orbita-shell" data-layout={advanced ? 'advanced' : 'simple'}>
    <aside className="orbita-sidebar" aria-label="Projetos e conversas">
      <div className="orbita-brand"><span aria-hidden="true">◉</span> Orbita Studio</div>
      <div className="orbita-projects">
        <select aria-label="Projeto" value={projectId} disabled={sending} onChange={(event) => { setProject(event.target.value); switchMission('') }}>
          <option value="">Selecione um projeto</option>
          {snapshot?.projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}
        </select>
        <button type="button" title="Novo projeto" aria-label="Novo projeto" onClick={() => setModal('project')}><Plus size={19} /></button>
      </div>
      <button className="orbita-new" disabled={!selectedProject || busy} onClick={() => setModal('mission')}><Plus size={16} /> Nova missão</button>
      <nav>{snapshot?.missions.filter((item) => item.projectId === projectId).map((item) =>
        <button key={item.id} aria-current={item.id === missionId} disabled={sending} onClick={() => switchMission(item.id)}>
          <strong>{item.name}</strong><small>{label[item.state] || item.state}</small>
        </button>)}</nav>
      <small className="orbita-version">{snapshot ? `Servidor Orbita ${snapshot.version}` : 'Conectando ao Orbita local'}</small>
    </aside>
    <main className="orbita-main">
      <header className="orbita-titlebar">
        <div className="orbita-heading"><h1>{mission?.name || selectedProject?.name || 'Escolha um projeto'}</h1><span>{mission ? label[mission.state] || mission.state : 'Antigravity'}</span></div>
        <button className="orbita-icon" type="button" title="Navegador do projeto" aria-label="Navegador do projeto" aria-pressed={browser} onClick={() => setBrowser(!browser)}><Globe size={17} /></button>
        <button className="orbita-icon" type="button" aria-pressed={advanced} onClick={() => { setAdvanced(!advanced); setPane('chat') }}><Layers size={17} /> {advanced ? 'Avançado' : 'Simples'}</button>
      </header>
      {advanced && <div className="orbita-tabs" aria-label="Painéis">
        {([['chat', 'Conversa'], ['activity', 'Atividade'], ['diff', 'Diff'], ['accounts', 'Contas']] as const).map(([id, name]) =>
          <button key={id} disabled={id !== 'accounts' && !mission} aria-pressed={pane === id} onClick={() => setPane(id)}>{name}</button>)}
        {mission && <button onClick={() => setModal('app')}>Abrir aplicativo</button>}
      </div>}
      {error && <div className="orbita-notice" role="status">{error}<button title="Fechar mensagem" aria-label="Fechar mensagem" onClick={() => setError('')}><X size={16} /></button></div>}
      <div className="orbita-workspace">
        <div className="orbita-chat">
          {pane === 'chat' ? mission ? <>
            <Transcript key={mission.id} id={mission.id} entries={records} optimistic={optimistic} running={waiting(mission) || sending} cancel={cancel} />
            <form className="orbita-composer" onSubmit={(event) => { event.preventDefault(); guard(() => send(false)) }}>
              {selectedFiles.length > 0 && <div className="orbita-attachments">{selectedFiles.map((file, index) => <button type="button" key={`${file.name}-${index}`} disabled={sending} title={`Remover ${file.name}`} onClick={() => changeFiles(selectedFiles.filter((_, at) => at !== index))}>{file.name} <X size={12} /></button>)}</div>}
              <textarea aria-label="Mensagem para o Antigravity" value={draft} readOnly={sending} placeholder="Peça algo ao Antigravity…" maxLength={32000} rows={2} onChange={(event) => changeDraft(event.target.value)}
                onPaste={(event) => { if (!sending && event.clipboardData.files.length) changeFiles([...selectedFiles, ...event.clipboardData.files]) }}
                onKeyDown={(event) => { if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); guard(() => send(false)) } }} />
              <div className="orbita-composer-actions">
                <input type="file" multiple hidden ref={picker} onChange={(event) => { changeFiles([...selectedFiles, ...Array.from(event.target.files || [])]); event.target.value = '' }} />
                <button type="button" aria-label="Anexar arquivos" disabled={sending} onClick={() => picker.current?.click()}><Paperclip size={16} /></button>
                <select aria-label="Modelo" value={model} disabled={busy} onChange={(event) => setModel(event.target.value)}><option value="">Antigravity</option><option value="neon">Gemini Neon</option><option value="opus">Claude Opus</option></select>
                <button type="button" disabled={busy} aria-pressed={full} onClick={() => setFull(!full)}>{full ? 'Permissão total' : 'Autônomo'}</button>
                <span className="orbita-spacer" />
                {waiting(mission) ? <button type="button" onClick={() => guard(cancel)}><CircleStop size={16} /> Interromper</button> : <>
                  {mission.pending && <button type="button" disabled={busy} onClick={() => guard(async () => { await api.call('resume', { missionId, fullAccess: full }); await refresh() })}>Retomar</button>}
                  <button type="button" disabled={busy || (!draft.trim() && !selectedFiles.length)} onClick={() => guard(() => send(true))}>Executar</button>
                  <button className="orbita-primary" type="submit" disabled={busy || (!draft.trim() && !selectedFiles.length)}><ArrowUp size={15} /> Enviar</button>
                </>}
              </div>
            </form>
          </> : <div className="orbita-empty"><h2>{selectedProject ? 'Abra uma conversa' : 'Abra ou crie um projeto'}</h2><p>{selectedProject ? 'Escolha uma missão à esquerda ou comece uma nova.' : 'Os projetos salvos no Orbita aparecem aqui.'}</p><button className="orbita-primary" onClick={() => setModal(selectedProject ? 'mission' : 'project')} disabled={!!selectedProject && busy}>{selectedProject ? 'Nova missão' : 'Novo projeto'}</button>{!snapshot && <button onClick={() => guard(refresh)}>Tentar conectar</button>}</div>
          : pane === 'activity' ? <section className="orbita-inspector" aria-label="Atividade"><h2>Atividade</h2>{records.filter((entry) => !['usuario', 'mensagem'].includes(entry.kind)).map((entry) => <p key={entry.sequence}>{entry.text}</p>)}</section>
          : pane === 'diff' ? <section className="orbita-inspector" aria-label="Diff"><h2>Alterações</h2><pre>{changes ? changes.diff || changes.status || 'Nenhuma alteração.' : 'Carregando…'}</pre>{changes?.diff && <button disabled={busy} onClick={() => guard(async () => { await api.call('review', { missionId, revision: changes.revision }); setChanges(await api.call('diff', { missionId })) })}>Aceitar diff</button>}</section>
          : <section className="orbita-inspector" aria-label="Contas"><h2>Contas</h2>{pool?.accounts.map((account) => <label key={account.id}><input type="checkbox" disabled={!mission || busy} checked={accounts.includes(account.id)} onChange={(event) => setAccounts(event.target.checked ? [...accounts, account.id] : accounts.filter((id) => id !== account.id))} /> {account.label}</label>)}{pool?.accounts.length === 0 && <p>Nenhuma conta foi salva no Orbita.</p>}</section>}
        </div>
        {browser && !modal && <Preview close={() => setBrowser(false)} report={report} />}
      </div>
    </main>
    {modal && <div className="orbita-modal-backdrop"><form className="orbita-modal" role="dialog" aria-modal="true" onSubmit={(event) => { event.preventDefault(); guard(() => save(event)) }}>
      <h2>{modal === 'project' ? 'Novo projeto' : modal === 'mission' ? 'Nova missão' : 'Abrir aplicativo'}</h2>
      {modal === 'app' ? <label>Executável (.exe)<input name="path" required autoFocus /></label> : <label>Nome<input name="name" required maxLength={100} autoFocus /></label>}
      {modal === 'project' && <label>Pasta Git existente (opcional)<input name="workspace" /></label>}
      {error && <p role="alert">{error}</p>}
      <div><button type="button" onClick={() => { setModal(undefined); setError('') }}>Cancelar</button><button className="orbita-primary" type="submit">{modal === 'app' ? 'Abrir' : 'Criar'}</button></div>
    </form></div>}
  </div>
}

createRoot(document.getElementById('root')!).render(
  <RootErrorBoundary><QueryClientProvider client={query}><I18nProvider initialLocale="pt-BR" configClient={null}><ThemeProvider><RootTooltipProvider>
    <HashRouter useTransitions={false}><App /></HashRouter>
  </RootTooltipProvider></ThemeProvider></I18nProvider></QueryClientProvider></RootErrorBoundary>
)
