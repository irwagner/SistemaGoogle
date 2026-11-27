import { useEffect, useState } from 'react'

type WindowQuota = { percent: number }
type Family = { fiveHour?: WindowQuota; weekly?: WindowQuota }
type Account = { id: string; label: string; gemini: Family; claude: Family; observed: boolean }
type Usage = { accounts: Account[]; active?: string; model?: string; reserve: number; directRunning: boolean;
  currentRun?: { model?: string; effort?: string; active: boolean; order: string[] } }

const labels: Record<string, string> = { low: 'Baixo', medium: 'Médio', high: 'Alto', xhigh: 'Extra', max: 'Máximo' }

function modelLabel(id: string) {
  if (/^gemini-4-neon/.test(id)) return 'Gemini Neon'
  if (/^claude-opus-5-5/.test(id)) return 'Claude Opus 5.5'
  if (/^claude-opus-4-6/.test(id)) return 'Claude Opus 4.6'
  return id
}

export function OrbitaRunStatus({ standalone = false }: { standalone?: boolean }) {
  const [status, setStatus] = useState<Usage>()
  useEffect(() => {
    if (!window.orbitaStudio) return
    let alive = true
    let timer: ReturnType<typeof setTimeout>
    async function refresh() {
      try {
        const value = await window.orbitaStudio!('pool') as Usage
        if (alive) setStatus(value)
      } catch { if (alive) setStatus(undefined) }
      if (alive) timer = setTimeout(refresh, 3000)
    }
    void refresh()
    return () => { alive = false; clearTimeout(timer) }
  }, [])
  if (!window.orbitaStudio) return null

  const run = status?.currentRun
  const account = status?.accounts.find(item => item.id === status.active)
  const model = (account && status?.model) || run?.model
  const group = model?.startsWith('gemini') ? 'gemini' : model && /^(claude|gpt)/.test(model) ? 'claude' : undefined
  const family = account?.observed && group ? account[group] : undefined
  const values = [family?.fiveHour?.percent, family?.weekly?.percent].filter((value): value is number => typeof value === 'number')
  const percentage = values.length ? Math.max(0, Math.min(100, ...values)) : undefined
  const orderIndex = account ? run?.order.indexOf(account.id) ?? -1 : -1
  const cutoff = status?.reserve ?? 0
  const color = percentage === undefined ? '' : percentage <= Math.max(cutoff, 15) ? '#f45f65' : percentage <= 45 ? '#e5a52b' : '#38b77a'
  const modelText = model ? modelLabel(model) : run?.active ? 'Identificando modelo…' : 'Modelo em espera'
  return <div data-slot="orbita-antigravity-status" className={`flex min-w-0 items-center gap-2 px-2 text-[11px] text-muted-foreground ${standalone ? 'min-h-6 border-t border-border' : 'shrink-0'}`}>
    <span className="shrink-0 font-medium text-foreground">{modelText}</span>
    {!run?.active && model && <span>Última sessão</span>}
    {run?.effort && <span title="Esforço confirmado pelo Antigravity">{labels[run.effort] ?? run.effort}</span>}
    {account ? <span className="truncate" title={account.label}>{orderIndex >= 0 ? `Conta ${orderIndex + 1} de ${run?.order.length}` : account.label}</span>
      : status?.directRunning ? <span>CLI direto</span> : <span>Pool em espera</span>}
    {percentage !== undefined && <span className="inline-flex shrink-0 items-center gap-1" title={`5 h: ${family?.fiveHour?.percent ?? '—'}% · Semana: ${family?.weekly?.percent ?? '—'}% · Reserva: ${cutoff}%`}>
      <span role="meter" aria-label="Cota restante" aria-valuenow={percentage} aria-valuemin={0} aria-valuemax={100} className="h-1.5 w-16 overflow-hidden rounded bg-muted">
        <span className="block h-full rounded" style={{ width: `${percentage}%`, background: color }} />
      </span>{percentage}%
    </span>}
  </div>
}
