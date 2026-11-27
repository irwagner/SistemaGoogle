import { BrowserWindow, dialog, shell, type IpcMain, type IpcMainInvokeEvent } from 'electron'
import { readFile, rename, statfs, writeFile } from 'node:fs/promises'
import { randomUUID } from 'node:crypto'
import { basename, dirname, join, normalize, parse } from 'node:path'
import { cpus, freemem, totalmem } from 'node:os'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { OrbitaApi } from './orbita-api.cjs'
import { openProduction } from './orbita-production'

const actions = new Set(['pool', 'pool-start', 'pool-add', 'pool-cancel', 'pool-refresh', 'pool-reserve'])
const run = promisify(execFile)

function processorTimes() {
  let idle = 0
  let total = 0
  for (const { times } of cpus()) {
    idle += times.idle
    total += times.idle + times.user + times.nice + times.sys + times.irq
  }
  return { idle, total }
}

function resourceUsage(used: number, total: number) {
  return { used, total, percent: total > 0 ? Math.round(used * 100 / total) : null }
}

function createSystemUsage(profile: string) {
  const volume = parse(process.env.SystemRoot || profile).root
  let lastCpu = processorTimes()
  let sampledCpuAt = performance.now()
  let cpu: number | null = null
  let sampledStorageAt = -Infinity
  let storage: (ReturnType<typeof resourceUsage> & { volume: string }) | null = null

  return async () => {
    const now = performance.now()
    if (now - sampledCpuAt >= 1000) {
      const current = processorTimes()
      const total = current.total - lastCpu.total
      const busy = Math.max(0, Math.min(total, total - (current.idle - lastCpu.idle)))
      cpu = resourceUsage(busy, total).percent
      sampledCpuAt = now
      lastCpu = current
    }
    if (now - sampledStorageAt >= 15000) {
      sampledStorageAt = now
      const disk = await statfs(volume).catch(() => null)
      storage = disk && disk.blocks > 0 ? {
        volume,
        ...resourceUsage((disk.blocks - disk.bfree) * disk.bsize, disk.blocks * disk.bsize)
      } : null
    }
    const memory = totalmem()
    return { cpu, ram: resourceUsage(memory - freemem(), memory), storage }
  }
}

function preferences(value: unknown) {
  const { mode, accounts, model, effort = 'auto' } = (value ?? {}) as Record<string, unknown>
  if (mode !== 'mission' && mode !== 'cli' && mode !== 'pool') throw new Error('Escolha como conectar o Antigravity.')
  if (typeof model !== 'string' || !['mission', 'auto', 'neon', 'opus'].includes(model)) throw new Error('Modelo inválido.')
  if (typeof effort !== 'string' || !['auto', 'low', 'medium', 'high', 'xhigh', 'max'].includes(effort)) throw new Error('Nível de esforço inválido.')
  if (!Array.isArray(accounts) || accounts.length > 10 || accounts.some(id => typeof id !== 'string' || !/^[a-zA-Z0-9_-]{1,100}$/.test(id))) {
    throw new Error('Selecione até dez contas Google.')
  }
  if (mode === 'pool' && accounts.length === 0) throw new Error('Selecione pelo menos uma conta para usar o pool.')
  return { mode, model, effort, accounts: [...new Set(accounts)] }
}

export function registerOrbitaPool(ipc: IpcMain, rendererUrl: () => string) {
  const profile = process.env.HERMES_HOME
  const enabled = process.env.ORBITA_STUDIO_DESKTOP === '1' && Boolean(profile)
  ipc.on('orbita:enabled', event => { event.returnValue = enabled })
  if (!enabled || !profile) return
  const settings = join(profile, 'orbita-preferences.json')
  const permission = join(profile, 'orbita-permission.json')
  const systemUsage = createSystemUsage(profile)
  let client: OrbitaApi | undefined
  let currentPort: number | undefined

  function isOrbitaWindow(event: IpcMainInvokeEvent) {
    const frame = event.senderFrame
    if (!frame || frame.parent) return false
    const url = new URL(frame.url)
    url.search = ''
    url.hash = ''
    return url.href === rendererUrl()
  }

  ipc.handle('orbita:pool', async (event, action: unknown, data: unknown) => {
    if (!isOrbitaWindow(event)) throw new Error('Abra Contas na janela do Orbita Studio.')
    if (action === 'system-usage') return systemUsage()
    if (action === 'signal-setup') {
      const launcher = process.env.ORBITA_SIGNAL_SETUP
      if (!launcher) throw new Error('Abra o cliente pelo atalho do Orbita Studio.')
      const error = await shell.openPath(launcher)
      if (error) throw new Error('Não foi possível abrir o vínculo do Signal.')
      return true
    }
    if (action === 'production') {
      const { area, profile } = (data ?? {}) as Record<string, unknown>
      if (typeof area !== 'string' || typeof profile !== 'string' || profile.length > 120) throw new Error('Área de produção inválida.')
      return openProduction(area, profile)
    }
    if (action === 'preferences') {
      try { return preferences(JSON.parse(await readFile(settings, 'utf8'))) }
      catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') return { mode: 'mission', model: 'mission', effort: 'auto', accounts: [] }
        throw error
      }
    }
    if (action === 'save-preferences') {
      const selected = preferences(data)
      const temp = `${settings}.${randomUUID()}.tmp`
      await writeFile(temp, JSON.stringify(selected), { flag: 'wx' })
      await rename(temp, settings)
      return selected
    }
    if (action === 'permission') {
      try { return JSON.parse(await readFile(permission, 'utf8')) === true }
      catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false
        throw error
      }
    }
    if (action === 'save-permission') {
      if (typeof data !== 'boolean') throw new Error('Modo de permissão inválido.')
      const temp = `${permission}.${randomUUID()}.tmp`
      await writeFile(temp, JSON.stringify(data), { flag: 'wx' })
      await rename(temp, permission)
      return data
    }
    const port = Number(await readFile(join(profile, 'orbita-local-port'), 'ascii'))
    if (!client || port !== currentPort) {
      client = new OrbitaApi(port)
      currentPort = port
    }
    if (action === 'pool-test') return client.call(action, data)
    if (action === 'pool-verification') {
      const { url } = await client.call(action, data)
      const link = new URL(url)
      if (link.origin !== 'https://accounts.google.com' || link.username || link.password ||
          !/^\/(?:v\d+\/)?signin(?:\/|$)/.test(link.pathname)) throw new Error('O Google não enviou um endereço de verificação válido.')
      await shell.openExternal(link.href)
      return true
    }
    if (action === 'project-overview') {
      const { projects, missions } = await client.call('state')
      return { projects, missions }
    }
    if (action === 'pick-project-folder') {
      const parent = BrowserWindow.fromWebContents(event.sender)
      if (!parent) return null
      const selection = await dialog.showOpenDialog(parent, { title: 'Escolha a pasta do projeto', properties: ['openDirectory'] })
      return selection.canceled ? null : selection.filePaths[0] ?? null
    }
    if (action === 'create-project') {
      const { name, workspace } = (data ?? {}) as Record<string, unknown>
      if (typeof name !== 'string' || !name.trim() || (workspace != null && typeof workspace !== 'string'))
        throw new Error('Informe o nome e escolha a pasta do projeto.')
      if (workspace) {
        const key = (value: string) => normalize(value).replace(/[\\/]+$/, '').toLowerCase()
        const { projects } = await client.call('state')
        const existing = projects.find(project => key(project.workspace) === key(workspace as string))
        if (existing) throw new Error(`Essa pasta já está vinculada a ${existing.name}.`)
      }
      return client.call('project', { name, workspace })
    }
    if (action === 'create-mission') {
      const { projectId, name } = (data ?? {}) as Record<string, unknown>
      return client.call('mission', { projectId, name })
    }
    if (action === 'signal-activity') {
      const { sessionId, cursor } = (data ?? {}) as Record<string, unknown>
      if (typeof sessionId !== 'string' || !/^[A-Za-z0-9_-]{1,120}$/.test(sessionId)) return null
      const canonical = /^orbita_([a-z0-9]{6,40})$/.exec(sessionId)?.[1]
      const missionId = canonical ?? (await readFile(join(profile, 'orbita-missions', `${sessionId}.txt`), 'ascii')
        .catch(error => { if (error.code === 'ENOENT') return ''; throw error })).trim()
      if (!/^[a-z0-9]{6,40}$/.test(missionId)) return null
      return client.call(action, { missionId, cursor })
    }
    if (action === 'save-signal-attachment') {
      const { missionId, attachmentId, name } = (data ?? {}) as Record<string, unknown>
      if (typeof name !== 'string' || !name || name.length > 180 || basename(name) !== name || /[\u0000-\u001f]/.test(name))
        throw new Error('Nome de anexo inválido.')
      const parent = BrowserWindow.fromWebContents(event.sender)
      if (!parent) return false
      const response = await client.call('signal-attachment', { missionId, attachmentId })
      const bytes = Buffer.from(await response.arrayBuffer())
      const selection = await dialog.showSaveDialog(parent, { title: 'Salvar anexo', defaultPath: name })
      if (selection.canceled || !selection.filePath) return false
      await writeFile(selection.filePath, bytes)
      return true
    }
    if (action === 'open-mission') {
      const { missionId } = (data ?? {}) as Record<string, unknown>
      const runtime = process.env.HERMES_DESKTOP_HERMES
      const cwd = process.env.HERMES_DESKTOP_HERMES_ROOT
      if (typeof missionId !== 'string' || !/^[a-z0-9]{6,40}$/.test(missionId) || !runtime || !cwd)
        throw new Error('Não foi possível abrir este chat do Orbita.')
      try {
        const { stdout } = await run(join(dirname(runtime), 'python.exe'), ['-m', 'tui_gateway.orbita_bridge', '--open-mission', missionId],
          { cwd, windowsHide: true, timeout: 20_000 })
        const { sessionId } = JSON.parse(stdout.trim().split('\n').at(-1) ?? '')
        if (typeof sessionId === 'string' && /^[A-Za-z0-9_-]{1,120}$/.test(sessionId)) return sessionId
      } catch { /* The saved mission stays available for a retry. */ }
      throw new Error('O chat ficou salvo, mas a janela não conseguiu abri-lo. Tente abrir pelo projeto.')
    }
    if (typeof action !== 'string' || !actions.has(action)) throw new Error('Ação indisponível.')
    if (action === 'pool-add' || action === 'pool-refresh' || action === 'pool-reserve') await client.call('pool-start')
    const pool = await client.call(action, data ?? {})
    const state = await client.call('state').catch(() => null)
    const activeMission = state?.missions.find(mission => mission.state === 'conversando' || mission.state === 'executando')
    const recent = [...(state?.missions ?? [])].filter(mission => mission.model).sort((a, b) => Date.parse(b.updated) - Date.parse(a.updated))[0]
    const shown = activeMission ?? recent
    return { ...pool, directRunning: Boolean(activeMission && !activeMission.accountIds?.length),
      currentRun: shown ? { model: shown.model, effort: shown.effort, active: Boolean(activeMission), order: shown.accountIds ?? [] } : null }
  })
}
