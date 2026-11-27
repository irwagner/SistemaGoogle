import { useStore } from '@nanostores/react'
import { atom, onMount } from 'nanostores'

import { Button } from '@/components/ui/button'

type Permission = { active: boolean; pending: boolean; error?: string }

const permission = atom<Permission>({ active: false, pending: true })
let pendingChange: Promise<void> | undefined

onMount(permission, () => {
  if (!window.orbitaStudio || pendingChange || !permission.get().pending) return
  pendingChange = window.orbitaStudio('permission').then(active => {
    permission.set({ active: active === true, pending: false })
  }).catch((error: unknown) => {
    permission.set({ active: false, pending: false, error: 'Falha ao ler a permissão do Orbita.' })
    throw error
  }).finally(() => { pendingChange = undefined })
  void pendingChange.catch(() => {})
})

export function waitForOrbitaPermission() {
  return pendingChange
}

export function OrbitaPermission({ disabled }: { disabled: boolean }) {
  const mode = useStore(permission)

  function toggle() {
    if (pendingChange || !window.orbitaStudio) return
    permission.set({ active: mode.active, pending: true })
    pendingChange = window.orbitaStudio('save-permission', !mode.active).then(active => {
      permission.set({ active: active === true, pending: false })
    }).catch((error: unknown) => {
      permission.set({ active: mode.active, pending: false, error: 'Falha ao salvar a permissão do Orbita.' })
      throw error
    }).finally(() => { pendingChange = undefined })
    void pendingChange.catch(() => {})
  }

  return <>
    <Button type="button" size="sm" variant={mode.active ? 'outline' : 'ghost'}
      className={mode.active ? 'bg-amber-500/15 text-amber-300 hover:bg-amber-500/25 hover:text-amber-200' : undefined}
      aria-pressed={mode.active} disabled={disabled || mode.pending}
      title="Executa os próximos pedidos sem aprovações do Orbita, com acesso de arquivos, rede e programas da sua conta do Windows."
      onClick={toggle}>
      Permissão total
    </Button>
    {mode.error && <span className="text-xs text-destructive" role="alert">{mode.error}</span>}
  </>
}
