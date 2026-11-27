import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { openSessionFromPicker, openSessionIntentFromModifiers, type OpenSessionIntent } from '@/app/open-session'
import { Button } from '@/components/ui/button'
import { Codicon } from '@/components/ui/codicon'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { SidebarMenuButton, SidebarMenuItem } from '@/components/ui/sidebar'
import { openPreview } from '@/store/preview'
import { $activeGatewayProfile } from '@/store/profile'

type Project = { id: string; name: string; workspace: string; managed: boolean }
type Chat = { id: string; name: string; projectId: string }
type Overview = { projects: Project[]; missions: Chat[] }
const emptyOverview: Overview = { projects: [], missions: [] }
const destinations = [ ['video', 'Vídeo e minisséries', 'device-camera-video'], ['pesquisa', 'Pesquisa', 'search'], ['audio', 'Áudio', 'unmute'] ] as const

export function OrbitaProductionMenu() {
  const [opening, setOpening] = useState<string>()
  const [error, setError] = useState('')
  if (!window.orbitaStudio) return null
  async function launch(area: string, label: string) {
    setOpening(area)
    setError('')
    try {
      const profile = $activeGatewayProfile.get() ?? 'default'
      const url = await window.orbitaStudio!('production', { area, profile }) as string
      openPreview({ kind: 'url', label, source: url, url, transient: true, orbitaProduction: true })
    } catch (reason) { setError(String(reason instanceof Error ? reason.message : reason)) }
    finally { setOpening(undefined) }
  }
  return <>
    {destinations.map(([area, label, icon]) => (
      <SidebarMenuItem key={area}>
        <SidebarMenuButton disabled={Boolean(opening)} onClick={() => void launch(area, label)}>
          <Codicon name={icon} /><span>{opening === area ? 'Abrindo…' : label}</span>
        </SidebarMenuButton>
      </SidebarMenuItem>
    ))}
    {error && <li className="px-2 pb-2 text-xs text-destructive" role="alert">{error}</li>}
  </>
}

export function OrbitaNewSession() {
  return <SidebarMenuItem>
    <SidebarMenuButton onClick={() => window.dispatchEvent(new Event('orbita-new-mission'))}>
      <Codicon name="new-file" /><span>Nova sessão</span>
    </SidebarMenuButton>
  </SidebarMenuItem>
}

export function OrbitaProjects({ visible }: { visible: boolean }) {
  return window.orbitaStudio ? <ProjectsPanel invoke={window.orbitaStudio} visible={visible} /> : null
}

function ProjectsPanel({ invoke, visible }: { invoke: NonNullable<Window['orbitaStudio']>; visible: boolean }) {
  const navigate = useNavigate()
  const [overview, setOverview] = useState<Overview>(emptyOverview)
  const [expanded, setExpanded] = useState('')
  const [form, setForm] = useState<'chat' | 'project'>()
  const [projectId, setProjectId] = useState('')
  const [folder, setFolder] = useState<string>()
  const [title, setTitle] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const revision = useRef(0)
  const project = overview.projects.find(item => item.id === projectId)

  useEffect(() => {
    let alive = true
    async function refresh() {
      const observed = ++revision.current
      try {
        const result = await invoke('project-overview') as Overview
        if (alive && observed === revision.current) setOverview(result)
      } catch (reason) {
        if (alive && observed === revision.current) setError(String(reason instanceof Error ? reason.message : reason))
      }
    }
    const show = () => { setTitle(''); setError(''); setForm('chat'); void refresh() }
    window.addEventListener('orbita-new-mission', show)
    window.addEventListener('focus', refresh)
    const timer = window.setInterval(() => { if (visible && document.visibilityState === 'visible') void refresh() }, 4000)
    void refresh()
    return () => {
      alive = false
      window.removeEventListener('orbita-new-mission', show)
      window.removeEventListener('focus', refresh)
      window.clearInterval(timer)
    }
  }, [invoke, visible])

  function newChat(id: string) {
    setProjectId(id)
    setTitle('')
    setError('')
    setForm('chat')
  }

  function newProject() {
    setFolder(undefined)
    setTitle('')
    setError('')
    setForm('project')
  }

  async function withFeedback(work: () => Promise<void>) {
    setBusy(true)
    setError('')
    try { await work() }
    catch (reason) { setError(String(reason instanceof Error ? reason.message : reason).replace(/^Error invoking remote method '[^']+': Error: /, '')) }
    finally { setBusy(false) }
  }

  async function openChat(chat: Chat, intent: OpenSessionIntent = 'in-place') {
    const sessionId = await invoke('open-mission', { missionId: chat.id }) as string
    openSessionFromPicker(sessionId, navigate, intent)
  }

  async function submit() {
    if (form === 'project' && title.trim()) {
      const created = await invoke('create-project', { name: title.trim(), workspace: folder }) as Project
      revision.current++
      setOverview(current => ({ ...current, projects: [...current.projects.filter(item => item.id !== created.id), created]
        .sort((a, b) => a.name.localeCompare(b.name, 'pt-BR')) }))
      setExpanded(created.id)
      setProjectId(created.id)
      setForm(undefined)
    } else if (form === 'chat' && project) {
      const names = new Set(overview.missions.filter(chat => chat.projectId === project.id).map(chat => chat.name))
      let number = 1
      while (names.has(`Chat ${number}`)) number++
      const chat = await invoke('create-mission', { projectId: project.id, name: title.trim() || `Chat ${number}` }) as Chat
      revision.current++
      setOverview(current => ({ ...current, missions: [chat, ...current.missions.filter(item => item.id !== chat.id)] }))
      setExpanded(project.id)
      setForm(undefined)
      await openChat(chat, 'stack')
    }
  }

  const field = 'w-full rounded-md border bg-background p-2'
  return <>
    {visible && <section aria-label="Projetos do Orbita" className="max-h-64 shrink-0 overflow-y-auto px-2 pb-1">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-xs font-medium uppercase text-muted-foreground">Projetos</h2>
        <Button type="button" size="xs" variant="ghost" disabled={busy} onClick={newProject}>
          <Codicon name="add" />Novo projeto
        </Button>
      </div>
      <ul className="space-y-1">
        {overview.projects.map(item => {
          const open = expanded === item.id
          const chats = overview.missions.filter(chat => chat.projectId === item.id)
          return <li key={item.id}>
            <div className="flex items-center gap-1">
              <Button type="button" variant="ghost" size="sm" className="min-w-0 flex-1 justify-start" aria-expanded={open}
                onClick={() => { setExpanded(open ? '' : item.id); setProjectId(item.id) }}>
                <Codicon name={open ? 'folder-opened' : 'folder'} /><span className="truncate" title={item.workspace}>{item.name}</span>
              </Button>
              <Button type="button" variant="ghost" size="icon-xs" disabled={busy} aria-label={`Novo chat em ${item.name}`}
                title={`Novo chat em ${item.name}`} onClick={() => newChat(item.id)}><Codicon name="add" /></Button>
            </div>
            {open && <ul aria-label={`Chats de ${item.name}`} className="space-y-0.5 pl-4">
              {chats.map(chat => <li key={chat.id}><Button type="button" size="sm" variant="ghost" disabled={busy}
                className="w-full justify-start" onClick={event => {
                  const intent = openSessionIntentFromModifiers(event)
                  void withFeedback(() => openChat(chat, intent))
                }}><Codicon name="comment" /><span className="truncate">{chat.name}</span></Button></li>)}
              {!chats.length && <li className="px-2 text-xs text-muted-foreground">Nenhum chat</li>}
            </ul>}
          </li>
        })}
      </ul>
      {!form && error && <p role="alert" className="text-xs text-destructive">{error}</p>}
    </section>}
    <Dialog open={Boolean(form)} onOpenChange={open => { if (!open && !busy) setForm(undefined) }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{form === 'project' ? 'Novo projeto' : 'Novo chat'}</DialogTitle>
          <DialogDescription>{form === 'project' ? 'Dê um nome ao projeto e escolha onde guardar seus arquivos.' : 'Escolha em qual projeto abrir este chat.'}</DialogDescription>
        </DialogHeader>
        <form className="space-y-3" onSubmit={event => { event.preventDefault(); if (!busy) void withFeedback(submit) }}>
          {form === 'project' ? <>
            <label className="block space-y-1 text-sm"><span>Nome do projeto</span>
              <input autoFocus className={field} value={title} maxLength={100} onChange={event => setTitle(event.target.value)} />
            </label>
            <p className="break-all text-sm text-muted-foreground">{folder || 'Pasta padrão do Orbita'}</p>
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" type="button" disabled={busy} onClick={() => void withFeedback(async () => {
                const selected = await invoke('pick-project-folder') as string | null
                if (selected) setFolder(selected)
              })}>Vincular pasta…</Button>
              {folder && <Button variant="ghost" type="button" disabled={busy} onClick={() => setFolder(undefined)}>Usar pasta padrão</Button>}
            </div>
          </> : <>
            <div className="space-y-1 text-sm">
              <label htmlFor="orbita-project-picker">Projeto</label>
              <select id="orbita-project-picker" className={field} value={projectId} disabled={busy} onChange={event => setProjectId(event.target.value)}>
                <option value="">Selecione um projeto</option>
                {overview.projects.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
              </select>
            </div>
            {project && <p className="break-all text-xs text-muted-foreground">{project.workspace}</p>}
            <label className="block space-y-1 text-sm"><span>Nome do chat (opcional)</span>
              <input className={field} value={title} maxLength={100} onChange={event => setTitle(event.target.value)} />
            </label>
            {!overview.projects.length && <Button variant="outline" type="button" disabled={busy} onClick={newProject}>Novo projeto</Button>}
          </>}
          {error && <p role="alert" className="text-destructive">{error}</p>}
          <Button type="submit" disabled={busy || (form === 'project' ? !title.trim() : !project)}>
            {busy ? 'Aguarde…' : form === 'project' ? 'Criar projeto' : 'Abrir chat'}
          </Button>
        </form>
      </DialogContent>
    </Dialog>
  </>
}
