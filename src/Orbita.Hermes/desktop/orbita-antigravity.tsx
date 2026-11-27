import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Codicon } from '@/components/ui/codicon'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'
import { SidebarMenuButton, SidebarMenuItem } from '@/components/ui/sidebar'
import mark from './orbita-icon.png'

type QuotaWindow = { percent: number; resetTime?: string }
type Family = { fiveHour?: QuotaWindow; weekly?: QuotaWindow }
type Account = { id: string; label: string; email: string; gemini: Family; claude: Family; observed: boolean; verificationRequired?: boolean }
type Pool = {
  accounts: Account[]
  active?: string
  directRunning?: boolean
  enrollment?: { message: string; deadline: number }
  mode?: string
  model?: string
  ready: number
  reserve: number
  running: boolean
}
type Preferences = { mode: 'mission' | 'cli' | 'pool'; model: 'mission' | 'auto' | 'neon' | 'opus'; accounts: string[]; effort?: string }
type RotationInput = { accounts: string[]; model: Preferences['model'] }
type Rotation = { checks: { id: string; state: 'ready' | 'verification-required' | 'unavailable' }[];
  rotation: 'passed' | 'blocked' | 'single'; from?: string; to?: string }
type OrbitaBridge = (action: string, data?: Preferences | Record<string, unknown> | boolean) => Promise<unknown>

declare global {
  interface Window { orbitaStudio?: OrbitaBridge }
}

const defaultPreferences: Preferences = { mode: 'mission', model: 'mission', accounts: [] }
const inputStyle = 'w-full rounded-md border border-border bg-background px-2 py-1.5 text-sm'
const resetFormatter = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' })

function message(reason: unknown) {
  return String(reason instanceof Error ? reason.message : reason).replace(/^Error invoking remote method '[^']+': Error: /, '')
}

function quota(value: QuotaWindow | undefined, label: string) {
  const reset = value?.resetTime ? Date.parse(value.resetTime) : NaN
  return (
    <div className="min-w-0">
      <div className="flex justify-between gap-2 text-xs"><span>{label}</span><span>{value ? `${value.percent}%` : 'Sem leitura'}</span></div>
      {value && (
        <>
          <meter className="block h-2 w-full" aria-label={`${label} restantes`} min={0} max={100} value={value.percent} />
          {Number.isFinite(reset) && <span className="text-[11px] text-muted-foreground">Renova {resetFormatter.format(reset)}</span>}
        </>
      )}
    </div>
  )
}

export function OrbitaAntigravity() {
  const invoke = window.orbitaStudio
  return invoke ? <AntigravityMenu invoke={invoke} /> : null
}

export function OrbitaBrand() {
  if (!window.orbitaStudio) return null
  return (
    <div className="flex items-center gap-1.5 px-2 pb-2 text-sm font-semibold">
      <img src={mark} alt="" className="size-5" />
      <span>Orbita Studio</span>
    </div>
  )
}

function AntigravityMenu({ invoke }: { invoke: OrbitaBridge }) {
  const [open, setOpen] = useState(false)
  const [pool, setPool] = useState<Pool>()
  const [prefs, setPrefs] = useState<Preferences>(defaultPreferences)
  const [margin, setMargin] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [rotation, setRotation] = useState<Rotation>()
  const checkOnReturn = useRef<RotationInput | null>(null)

  useEffect(() => {
    let alive = true
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const state = await invoke('pool') as Pool
        if (alive) setPool(state)
      } catch (reason) {
        if (alive && open) setError(message(reason))
      }
      if (alive) timer = setTimeout(poll, open ? 2500 : 7000)
    }
    if (!busy) void poll()
    return () => { alive = false; clearTimeout(timer) }
  }, [busy, invoke, open])

  const run = useCallback(async (action: string, data?: Parameters<OrbitaBridge>[1], recheck?: RotationInput) => {
    setBusy(true)
    setError('')
    setNotice('')
    try {
      const response = await invoke(action, data)
      if (action === 'save-preferences') {
        setPrefs(response as Preferences)
        setNotice('Preferências salvas para os próximos pedidos.')
      } else if (action === 'pool-test') {
        setRotation(response as Rotation)
        setPool(await invoke('pool') as Pool)
      } else if (action === 'pool-verification') {
        checkOnReturn.current = recheck ?? null
        setNotice('Conclua a verificação na página do Google. O Orbita confere novamente quando você voltar.')
      } else {
        setPool(response as Pool)
        if (action === 'pool-reserve') { setNotice('Margem de reserva salva.'); setMargin('') }
      }
    } catch (reason) { setError(message(reason)) }
    finally { setBusy(false) }
  }, [invoke])

  useEffect(() => {
    if (!open) return
    function recheck() {
      const pending = checkOnReturn.current
      if (!pending) return
      checkOnReturn.current = null
      void run('pool-test', pending)
    }
    window.addEventListener('focus', recheck)
    return () => window.removeEventListener('focus', recheck)
  }, [open, run])

  function setDialog(value: boolean) {
    setOpen(value)
    if (!value) return
    setError('')
    setNotice('')
    setMargin('')
    void invoke('preferences').then(current => setPrefs(current as Preferences)).catch(reason => setError(message(reason)))
  }

  useEffect(() => {
    const show = () => setDialog(true)
    window.addEventListener('orbita-open-antigravity', show)
    return () => window.removeEventListener('orbita-open-antigravity', show)
  }, [invoke])

  const current = pool?.accounts.find(account => account.id === pool.active)
  const summary = current ? `${current.label} em uso` : pool?.directRunning ? 'CLI em uso' : pool?.running
    ? `${pool.ready}/${pool.accounts.length} prontas` : pool?.accounts.length ? `${pool.accounts.length} salvas` : ''
  const order = prefs.accounts.filter(id => pool?.accounts.some(account => account.id === id))
  const testAccounts = prefs.mode === 'pool' && order.length ? order : pool?.accounts.map(account => account.id) ?? []
  const formMargin = margin || String(pool?.reserve ?? 0)

  return (
    <SidebarMenuItem>
      <Dialog open={open} onOpenChange={setDialog}>
        <DialogTrigger asChild>
          <SidebarMenuButton aria-label="Contas e cotas do Antigravity">
            <Codicon name="account" />
            <span className="min-w-0 truncate">Contas do Antigravity</span>
            {summary && <span className="ml-auto shrink-0 text-xs text-muted-foreground">{summary}</span>}
          </SidebarMenuButton>
        </DialogTrigger>
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-[43rem]">
          <DialogHeader>
            <DialogTitle>Contas e cotas do Antigravity</DialogTitle>
            <DialogDescription>{current ? `Em uso: ${current.label}${pool?.mode === 'alternar' ? ' · troca automática' : ''}` : pool?.directRunning ? 'Login direto do Antigravity CLI em uso' : pool?.running ? 'Pool disponível' : 'Pool em espera'}{pool?.model ? ` · ${pool.model}` : ''}</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 text-sm">
            <div className="flex flex-wrap gap-2">
              <Button disabled={busy || !pool || !!pool.enrollment} onClick={() => void run('pool-add')}>
                Conectar conta Google
              </Button>
              <Button variant="outline" disabled={busy || !pool || !!pool.enrollment} onClick={() => void run('pool-refresh')}>Atualizar cotas</Button>
              <Button variant="outline" disabled={busy || !testAccounts.length || !!pool?.enrollment}
                onClick={() => void run('pool-test', { accounts: testAccounts, model: prefs.model })}>Testar rotação</Button>
              {pool?.enrollment && <Button variant="outline" disabled={busy} onClick={() => void run('pool-cancel')}>Cancelar login</Button>}
            </div>
            {pool?.enrollment && <p role="status" className="rounded-md border p-2">Conclua o login na janela do Antigravity CLI. A conta aparece aqui quando a conexão terminar.</p>}
            {pool && !pool.accounts.length && <p className="text-muted-foreground">Nenhuma conta Google conectada ao pool.</p>}
            {rotation && <div role="status" className="space-y-1 rounded-md border border-border p-2 text-xs">
              <p>{rotation.rotation === 'passed' ? `Troca simulada: ${rotation.from} → ${rotation.to}.`
                : rotation.rotation === 'blocked' ? 'A troca simulada não encontrou duas contas disponíveis na ordem testada.'
                : 'Teste de uma conta concluído. Selecione duas para simular a troca.'}</p>
              <p className="text-muted-foreground">Conexões consultadas no Google. Nenhum pedido foi enviado ao modelo.</p>
            </div>}
            <div className="space-y-2">
              {pool?.accounts.map(account => {
                const position = order.indexOf(account.id)
                const check = rotation?.checks.find(result => result.id === account.id)
                return (
                  <article key={account.id} className="space-y-3 rounded-lg border border-border p-3" aria-label={account.label}>
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <span className="break-all"><strong>{account.label}</strong> · {account.email}{account.id === pool.active && ' · Em uso'}</span>
                      <label className="inline-flex items-center gap-1.5 text-xs">
                        <input type="checkbox" checked={position >= 0} disabled={prefs.mode !== 'pool' || busy || (position < 0 && order.length >= 10)}
                          onChange={event => setPrefs({ ...prefs, accounts: event.target.checked ? [...order, account.id] : order.filter(id => id !== account.id) })} />
                        {position < 0 ? 'Incluir na ordem' : `${position + 1}ª na ordem`}
                      </label>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2">
                      {([['Gemini', account.gemini], ['Claude e GPT', account.claude]] as const).map(([label, family]) => (
                        <div className="space-y-1" key={label}>
                          <div className="font-medium">{label}</div>
                          {quota(family.fiveHour, '5 horas')}{quota(family.weekly, 'Semanal')}
                        </div>
                      ))}
                    </div>
                    {account.verificationRequired ? <div className="flex flex-wrap items-center gap-2">
                      <span className="text-xs text-amber-500">O Google pediu verificação desta conta.</span>
                      <Button variant="outline" size="sm" disabled={busy || !!pool.enrollment} onClick={() => void run('pool-verification',
                        { id: account.id }, { accounts: testAccounts.includes(account.id) ? testAccounts : [account.id], model: prefs.model })}>Verificar no Google</Button>
                    </div> : check?.state === 'ready' ? <p className="text-xs text-muted-foreground">Conexão confirmada no teste.</p>
                      : check?.state === 'unavailable' ? <p className="text-xs text-destructive">O Google não confirmou a conexão. Atualize as cotas e teste novamente.</p>
                      : !account.observed && <p className="text-xs text-muted-foreground">Cotas aguardando consulta.</p>}
                  </article>
                )
              })}
            </div>
            <div className="grid items-end gap-2 sm:grid-cols-2">
              <label className="space-y-1">
                <span>Conexão nos próximos pedidos</span>
                <select className={inputStyle} disabled={busy} value={prefs.mode} onChange={event => setPrefs({ ...prefs, mode: event.target.value as Preferences['mode'] })}>
                  <option value="mission">Usar a conexão da missão</option>
                  <option value="cli">Login direto do Antigravity CLI</option>
                  <option value="pool">Usar contas selecionadas</option>
                </select>
              </label>
              <label className="space-y-1">
                <span>Modelo nos próximos pedidos</span>
                <select className={inputStyle} disabled={busy} value={prefs.model} onChange={event => setPrefs({ ...prefs, model: event.target.value as Preferences['model'] })}>
                  <option value="mission">Usar o modelo da missão</option><option value="auto">Padrão do Antigravity</option>
                  <option value="neon">Gemini Neon</option><option value="opus">Claude Opus</option>
                </select>
              </label>
            </div>
            <label className="block space-y-1">
              <span>Esforço nos próximos pedidos</span>
              <select className={inputStyle} disabled={busy} value={prefs.effort ?? 'auto'} onChange={event => setPrefs({ ...prefs, effort: event.target.value })}>
                <option value="auto">Padrão do modelo</option><option value="low">Baixo</option><option value="medium">Médio</option>
                <option value="high">Alto</option><option value="xhigh">Extra</option><option value="max">Máximo</option>
              </select>
            </label>
            {prefs.mode === 'pool' && <p className="text-xs text-muted-foreground">Marque as contas na ordem em que devem ser usadas. Com duas ou mais, o pool pode alternar automaticamente.</p>}
            <div className="flex flex-wrap items-end justify-between gap-2">
              <div className="flex items-end gap-2">
                <label className="block space-y-1"><span>Reserva (%)</span>
                  <input className={inputStyle + ' block w-24'} type="number" min={0} max={100} value={formMargin} onChange={event => setMargin(event.target.value)} /></label>
                <Button variant="outline" disabled={busy || !pool || Boolean(pool.enrollment)} onClick={() => void run('pool-reserve', { percent: Number(formMargin) })}>Salvar margem</Button>
              </div>
              <Button disabled={busy || (prefs.mode === 'pool' && !order.length)} onClick={() => void run('save-preferences', { ...prefs, accounts: order })}>Salvar preferências</Button>
            </div>
            {busy && <p role="status" className="text-muted-foreground">Aguarde o Antigravity…</p>}
            {notice && <p role="status" className="text-muted-foreground">{notice}</p>}
            {error && <p role="alert" className="text-destructive">{error}</p>}
          </div>
        </DialogContent>
      </Dialog>
    </SidebarMenuItem>
  )
}
