import { app, session } from 'electron'
import { spawn, type ChildProcess } from 'node:child_process'
import { access, mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, isAbsolute, join } from 'node:path'
import { setTimeout as wait } from 'node:timers/promises'
import { previewPartitionForProfile } from './preview-guest-throttle'

export const productionAreas = new Set(['video', 'pesquisa', 'audio'])
let current: Promise<{ url: string; cookie: string }> | undefined
let child: ChildProcess | undefined

async function startProduction() {
  const executable = process.env.ORBITA_CENTRAL_EXE
  if (!executable || !isAbsolute(executable)) throw new Error('O pacote dos módulos de produção está ausente.')
  await access(executable).catch(() => { throw new Error('O pacote dos módulos de produção está ausente.') })
  const temporary = await mkdtemp(join(tmpdir(), 'orbita-producao-'))
  const ticket = join(temporary, 'sessao-local')
  const server = spawn(executable, [], { cwd: dirname(executable), windowsHide: true, stdio: 'ignore',
    env: { ...process.env, CENTRAL_PORT: '0', CENTRAL_BOOTSTRAP_FILE: ticket, CENTRAL_ORBITA_PRODUCTION: '1' } })
  child = server
  let stopped = false
  const onExit = () => { stopped = true; if (child === server) { child = undefined; current = undefined } }
  server.once('exit', onExit)
  server.once('error', onExit)
  try {
    for (let attempt = 0; attempt < 160 && !stopped; attempt++) {
      const link = await readFile(ticket, 'utf8').catch(() => '')
      const match = /^http:\/\/127\.0\.0\.1:(\d+)\/entrar#([a-f0-9]{64})$/.exec(link.trim())
      if (match) {
        const url = `http://127.0.0.1:${match[1]}`
        const response = await fetch(`${url}/entrar`, { method: 'POST', redirect: 'error', signal: AbortSignal.timeout(8000),
          headers: { Origin: url, 'X-Central-Bootstrap': match[2] } })
        const cookie = /^central=([a-f0-9]{64})/.exec(response.headers.get('set-cookie') ?? '')?.[1]
        if (response.ok && cookie) return { url, cookie }
        throw new Error('A Central recusou a sessão local do módulo.')
      }
      await wait(125)
    }
    throw new Error('Os módulos de produção não iniciaram. Confira se a Central já está aberta com a mesma pasta de dados.')
  } catch (error) { if (!stopped) server.kill(); throw error }
  finally { await rm(temporary, { recursive: true, force: true }).catch(() => {}) }
}

app.on('before-quit', () => { child?.kill() })

export async function openProduction(area: string, profile: string) {
  if (!productionAreas.has(area)) throw new Error('Área de produção inválida.')
  current ??= startProduction().catch(error => { current = undefined; throw error })
  const { url, cookie } = await current
  await session.fromPartition(previewPartitionForProfile(profile)).cookies.set({ url, name: 'central', value: cookie,
    path: '/', httpOnly: true, sameSite: 'strict' })
  return `${url}/area/${area}`
}
