import { useEffect, useState } from 'react'

type Capacity = { used: number; total: number; percent: number | null }
type SystemUsage = { cpu: number | null; ram: Capacity; storage: (Capacity & { volume: string }) | null }

const gigabytes = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 })
const percent = (value: number | null | undefined) => value == null ? '—' : `${value}%`
const capacity = (value: Capacity) => `${gigabytes.format(value.used / 1024 ** 3)} de ${gigabytes.format(value.total / 1024 ** 3)} GB`

export function OrbitaSystemUsage() {
  const [usage, setUsage] = useState<SystemUsage | null>(null)

  useEffect(() => {
    const invoke = window.orbitaStudio
    if (!invoke) return
    let disposed = false
    let reading = false
    const refresh = async () => {
      if (document.hidden || reading) return
      reading = true
      try {
        const result = await invoke('system-usage') as SystemUsage
        if (!disposed) setUsage(result)
      } catch {
        if (!disposed) setUsage(null)
      } finally { reading = false }
    }
    void refresh()
    const timer = window.setInterval(refresh, 2500)
    document.addEventListener('visibilitychange', refresh)
    return () => {
      disposed = true
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', refresh)
    }
  }, [])

  if (!window.orbitaStudio) return null
  const disk = usage?.storage
  const details = [
    `CPU do computador ${percent(usage?.cpu)}`,
    usage ? `Memória RAM ${capacity(usage.ram)}` : 'Memória RAM indisponível',
    disk ? `Espaço ocupado no disco do Windows (${disk.volume}) ${capacity(disk)}` : 'Armazenamento indisponível'
  ].join('\n')

  return <span role="group" aria-label={details} title={details} data-slot="orbita-system-usage"
    className="hidden h-5 shrink-0 select-none items-center gap-2 whitespace-nowrap rounded border border-border/60 px-1.5 text-[10px] font-medium tabular-nums text-muted-foreground min-[580px]:inline-flex">
    {([['CPU', usage?.cpu], ['RAM', usage?.ram.percent], ['SSD', disk?.percent]] as const).map(([label, value]) => (
      <span key={label}>{label} <span className="text-foreground">{percent(value)}</span></span>
    ))}
  </span>
}
