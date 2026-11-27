import { createContext, useContext, useEffect, useMemo, useState } from 'react'
import { useAuiState } from '@assistant-ui/react'
import { useElapsedSeconds } from '@/components/chat/activity-timer'
import { ActivityTimerText } from '@/components/chat/activity-timer-text'
import { StatusPulse } from '@/components/ui/status-pulse'
import { Codicon } from '@/components/ui/codicon'
import { chatMessageText, type ChatMessage } from '@/lib/chat-messages'

type SignalAttachment = { id: string; name: string }
type SignalTurn = { sequence: number; startedAt: string; endedAt: string | null; recordedText: string; text: string;
  reply: string; active: boolean; detail?: string | null; missionId: string; attachments?: SignalAttachment[] | null; source?: string }
type Snapshot = { cursor: string; turns?: SignalTurn[] }
const empty: SignalTurn[] = []
export const OrbitaSignalActivity = createContext<{ sessionId: string; turn: SignalTurn } | null>(null)

function useSignalTurns(sessionId: string | null) {
  const [snapshot, setSnapshot] = useState<{ sessionId: string; turns: SignalTurn[] }>()
  useEffect(() => {
    const invoke = window.orbitaStudio
    if (!sessionId || !invoke) return
    let cursor = ''
    let cancelled = false
    let timer: ReturnType<typeof setTimeout>
    async function refresh() {
      try {
        const update = await invoke!('signal-activity', { sessionId, cursor }) as Snapshot | null
        if (cancelled) return
        if (update) {
          cursor = update.cursor
          if (update.turns) setSnapshot({ sessionId: sessionId!, turns: update.turns })
        }
      } catch { /* The next poll reconnects after the local server restarts. */ }
      if (!cancelled) timer = setTimeout(refresh, 1250)
    }
    void refresh()
    return () => { cancelled = true; clearTimeout(timer) }
  }, [sessionId])
  return snapshot && snapshot.sessionId === sessionId ? snapshot.turns : empty
}

function mergeSignalMessages(messages: ChatMessage[], turns: SignalTurn[], sessionId: string): ChatMessage[] {
  if (!turns.length) return messages
  const merged = [...messages]
  for (const turn of turns) {
    const start = Date.parse(turn.startedAt) / 1000
    const end = turn.endedAt ? Date.parse(turn.endedAt) / 1000 : undefined
    const prefix = `orbita-signal-${sessionId}-${turn.sequence}`
    const userIndex = merged.findIndex(row => row.role === 'user' &&
      (row.timestamp === undefined || Math.abs(row.timestamp - start) < 20) &&
      [turn.recordedText, turn.text].includes(chatMessageText(row)))
    const signalFiles = turn.attachments?.map(file => ({ ...file, missionId: turn.missionId }))
    if (userIndex >= 0 && (chatMessageText(merged[userIndex]) !== turn.text || signalFiles?.length)) {
      merged[userIndex] = { ...merged[userIndex], parts: [{ type: 'text', text: turn.text }], orbitaSignalFiles: signalFiles }
    }
    let index = userIndex
    if (index < 0) {
      const next = merged.findIndex(row => row.timestamp !== undefined && row.timestamp > start)
      index = next < 0 ? merged.length : next
      merged.splice(index, 0, { id: `${prefix}-user`, timestamp: start, role: 'user', parts: [{ type: 'text', text: turn.text }],
        orbitaSignalFiles: signalFiles })
    }
    const reply = turn.reply || (!turn.active ? turn.detail : '')
    if (!reply) continue
    let existing = -1
    for (let candidate = index + 1; candidate < merged.length && merged[candidate].role !== 'user'; candidate++) {
      const row = merged[candidate]
      const text = chatMessageText(row).trimEnd()
      if (row.role === 'assistant' && text && reply.startsWith(text)) { existing = candidate; break }
    }
    const prior = existing < 0 ? undefined : merged[existing]
    const assistant: ChatMessage = { ...prior, id: prior?.id ?? `${prefix}-reply`, role: 'assistant',
      timestamp: prior?.timestamp ?? start, completedAt: end, durationS: end === undefined ? undefined : Math.max(0, end - start),
      parts: [{ type: 'text', text: reply }] }
    if (existing < 0) merged.splice(index + 1, 0, assistant)
    else merged[existing] = assistant
  }
  return merged
}

export function useOrbitaSignalActivity(sessionId: string | null, messages: ChatMessage[]) {
  const turns = useSignalTurns(sessionId)
  const transcript = useMemo(() => sessionId ? mergeSignalMessages(messages, turns, sessionId) : messages,
    [messages, sessionId, turns])
  const running = turns.find(turn => turn.active)
  const activity = useMemo(() => sessionId && running ? { sessionId, turn: running } : null, [sessionId, running])
  return { activity, transcript }
}

export function OrbitaSignalProgress() {
  const activity = useContext(OrbitaSignalActivity)
  return activity ? <RunningSignalTurn key={`${activity.sessionId}-${activity.turn.sequence}`} turn={activity.turn} /> : null
}

export function OrbitaSignalAttachmentList() {
  const files = useAuiState(state => state.message.metadata?.custom?.orbitaSignalFiles) as (SignalAttachment & { missionId: string })[] | undefined
  const [saving, setSaving] = useState('')
  const [error, setError] = useState(false)
  if (!files?.length || !window.orbitaStudio) return null
  async function save(file: SignalAttachment & { missionId: string }) {
    setSaving(file.id)
    setError(false)
    try { await window.orbitaStudio!('save-signal-attachment', { missionId: file.missionId, attachmentId: file.id, name: file.name }) }
    catch { setError(true) }
    finally { setSaving('') }
  }
  return <div data-slot="orbita-signal-attachments" className="mb-2 flex flex-wrap justify-end gap-1">
    {files.map(file => <button key={file.id} type="button" onClick={() => void save(file)} disabled={Boolean(saving)}
      title={`Salvar ${file.name}`} className="inline-flex min-w-0 max-w-full items-center gap-1.5 rounded-md border border-border px-2 py-1 text-xs hover:bg-muted">
      <Codicon name="file" /><span className="truncate">{file.name}</span>
    </button>)}
    {error && <p role="alert" className="w-full text-right text-xs text-destructive">Não foi possível salvar o anexo.</p>}
  </div>
}

function RunningSignalTurn({ turn }: { turn: SignalTurn }) {
  const seconds = useElapsedSeconds(true, undefined, Date.parse(turn.startedAt))
  return <div data-slot="orbita-signal-progress" className="my-2 flex flex-wrap items-center gap-x-3 gap-y-1 rounded-xl border border-border bg-background px-3 py-2 text-xs">
    <div role="status" className="flex min-w-0 items-center gap-2">
      <StatusPulse kind="opacity" aria-hidden="true" className="size-1.5 shrink-0 rounded-full bg-primary" />
      <span className="font-medium">Executando · {turn.source === 'discord' ? 'Discord' : 'Signal'}</span>
      <ActivityTimerText seconds={seconds} aria-hidden={true} />
      {turn.detail && <span className="max-w-60 truncate text-muted-foreground" title={turn.detail}>{turn.detail}</span>}
    </div>
  </div>
}
