import { useState } from 'react'

import { Button } from '@/components/ui/button'

export function OrbitaSignalSetup() {
  const [status, setStatus] = useState('')
  const [opening, setOpening] = useState(false)

  async function linkDevice() {
    setOpening(true)
    setStatus('')
    try {
      await window.orbitaStudio!('signal-setup')
      setStatus('Escaneie o QR na janela que abriu. Após vincular, reabra o Orbita e mande uma mensagem em Anotações.')
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Não foi possível abrir o vínculo do Signal.')
    } finally { setOpening(false) }
  }

  return <section className="space-y-2 rounded-xl border border-border p-3 text-sm">
    <p>Vincule o seu Signal ao Orbita para pedir trabalho e receber avisos na conversa Anotações.</p>
    <Button type="button" disabled={opening} onClick={() => void linkDevice()}>Vincular aparelho Signal</Button>
    {status && <p role="status" className="text-muted-foreground">{status}</p>}
  </section>
}
