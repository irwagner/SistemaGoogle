param([switch]$PularBuild)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$integration = Join-Path $workspace 'src\Orbita.Hermes'
$lock = Get-Content -LiteralPath (Join-Path $integration 'upstream.json') -Raw | ConvertFrom-Json
$cache = Join-Path $workspace '.orbita-hermes'
$upstream = Join-Path $cache 'upstream'
$venv = Join-Path $cache 'runtime-venv'

if (!(Test-Path -LiteralPath $upstream)) {
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    & git -c core.longpaths=true clone --depth 1 --branch $lock.tag $lock.repository $upstream
    if ($LASTEXITCODE) { throw 'O checkout do Hermes falhou.' }
}
if ((& git -C $upstream rev-parse HEAD) -ne $lock.commit) { throw 'O Hermes não está no commit fixado pelo Orbita.' }

function Set-DesktopSource([string]$file, [string]$anchor, [string]$replacement) {
    $source = [IO.File]::ReadAllText($file)
    if ($source.Contains($replacement)) { return }
    if (!$source.Contains($anchor)) { throw "O ponto de integração mudou em $file." }
    [IO.File]::WriteAllText($file, $source.Replace($anchor, $replacement), [Text.UTF8Encoding]::new($false))
}

Push-Location -LiteralPath $upstream
try {
    $env:UV_PROJECT_ENVIRONMENT = $venv
    $runtimeReady = Test-Path -LiteralPath (Join-Path $venv 'Scripts\hermes.exe')
    if ($runtimeReady) {
        & (Join-Path $venv 'Scripts\python.exe') -c 'import telegram, discord' 2>$null
        $runtimeReady = $LASTEXITCODE -eq 0
    }
    if (!$runtimeReady) {
        & uv sync --locked --no-dev --extra telegram --extra discord --inexact --python 3.14
        if ($LASTEXITCODE) { throw 'O runtime do gateway Hermes não foi preparado.' }
    }
    $link = Join-Path $upstream '.venv'
    if (!(Test-Path -LiteralPath $link)) { New-Item -ItemType Junction -Path $link -Target $venv | Out-Null }
    $desktop = Join-Path $upstream 'apps\desktop'
    if (!$PularBuild) {
        $frontendBuild = Join-Path $upstream 'scripts\build\frontend-common.mjs'
        $backupCleanup = "  rmSync(backupRoot, { recursive: true, force: true })`n}"
        Set-DesktopSource $frontendBuild $backupCleanup @'
  try { rmSync(backupRoot, { recursive: true, force: true }) }
  catch (error) {
    if (process.platform !== 'win32' || !['EPERM', 'EBUSY'].includes(error.code)) throw error
    console.warn(`Build published; the open app still holds the previous build at ${backupRoot}`)
  }
}
'@
        Copy-Item -LiteralPath (Join-Path $integration 'native\orbita-api.cjs') -Destination (Join-Path $desktop 'electron\orbita-api.cjs')
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-pool.ts') -Destination (Join-Path $desktop 'electron\orbita-pool.ts')
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-production.ts') -Destination (Join-Path $desktop 'electron\orbita-production.ts')
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-antigravity.tsx') -Destination (Join-Path $desktop 'src\app\chat\sidebar\orbita-antigravity.tsx')
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-signal.tsx') -Destination (Join-Path $desktop 'src\app\messaging\orbita-signal.tsx')
        $messaging = Join-Path $desktop 'src\app\messaging\index.tsx'
        $messageImport = "import { TelegramQrSetup } from './telegram-qr-setup'"
        Set-DesktopSource $messaging $messageImport "$messageImport`nimport { OrbitaSignalSetup } from './orbita-signal'"
        $signalSection = "      {platform.id === 'telegram' && ("
        Set-DesktopSource $messaging $signalSection "      {platform.id === 'signal' && window.orbitaStudio && <OrbitaSignalSetup />}`n$signalSection"
        $discordIntro = "'Open the Discord Developer Portal, create an application, add a Bot, then copy its token. Invite the bot to your server with the right scopes.'"
        $oldDiscordIntro = "'Crie um bot no portal do Discord e convide-o para um servidor privado. Preencha o token, seu ID e o ID do canal ou tópico. Depois vincule a conversa com !vincular Atlas / Chat 1.'"
        $discordIntroAnchor = if ([IO.File]::ReadAllText($messaging).Contains($oldDiscordIntro)) { $oldDiscordIntro } else { $discordIntro }
        Set-DesktopSource $messaging $discordIntroAnchor "'Crie o bot Orbita Studio no portal do Discord. Cole o token e seu ID de usuário e clique em Salvar e conectar. Depois converse na mensagem privada do bot. Para usar um canal, adicione o ID em Opções avançadas.'"
        Set-DesktopSource $messaging '  DISCORD_REPLY_TO_MODE: { advanced: true },' "  DISCORD_ALLOWED_CHANNELS: { advanced: true },`n  DISCORD_REPLY_TO_MODE: { advanced: true },"
        Set-DesktopSource $messaging 'const hasChanges = (update: MessagingPlatformUpdate) =>' @'
function orbitaDiscordReady(platform: MessagingPlatformInfo, update: MessagingPlatformUpdate) {
  return Boolean(window.orbitaStudio && platform.id === 'discord' && ['DISCORD_BOT_TOKEN', 'DISCORD_ALLOWED_USERS']
    .every(key => Boolean(update.env?.[key] || (!update.clear_env?.includes(key) &&
      platform.env_vars.some(field => field.key === key && field.is_set)))))
}

const hasChanges = (update: MessagingPlatformUpdate) =>
'@
        Set-DesktopSource $messaging '    setSaving(`env:${platform.id}`)' @'
    const orbitaDiscord = Boolean(window.orbitaStudio && platform.id === 'discord')
    if (orbitaDiscord) update.enabled = orbitaDiscordReady(platform, update)
    setSaving(`env:${platform.id}`)
'@
        Set-DesktopSource $messaging @'
      settleAfterUpdate(result.hot_served)
      notify({
        kind: 'success',
        title: m.setupSaved(platform.name),
'@ @'
      settleAfterUpdate(result.hot_served)
      if (orbitaDiscord && (update.enabled || platform.enabled) && !result.hot_served) await restartGatewayNow()
      notify({
        kind: 'success',
        title: m.setupSaved(platform.name),
'@
        Set-DesktopSource $messaging '{m.openSetupGuide}' "{window.orbitaStudio && platform.id === 'discord' ? 'Abrir portal do Discord' : m.openSetupGuide}"
        $actionEdits = '                      hasEdits={hasChanges(platformChanges(selected, edits[selected.id] || {}))}'
        Set-DesktopSource $messaging $actionEdits "                      connectDiscord={orbitaDiscordReady(selected, platformChanges(selected, edits[selected.id] || {}))}`n$actionEdits"
        Set-DesktopSource $messaging "function PlatformActionBar({`n  hasEdits," "function PlatformActionBar({`n  connectDiscord,`n  hasEdits,"
        Set-DesktopSource $messaging '  hasEdits: boolean' "  connectDiscord: boolean`n  hasEdits: boolean"
        $interimDiscordButton = "{isSavingEnv ? m.saving : orbitaDiscordReady(platform, platformChanges(platform, edits)) ? 'Salvar e conectar' : m.saveChanges}"
        $saveActionAnchor = if ([IO.File]::ReadAllText($messaging).Contains($interimDiscordButton)) { $interimDiscordButton } else { '{isSavingEnv ? m.saving : m.saveChanges}' }
        Set-DesktopSource $messaging $saveActionAnchor "{isSavingEnv ? m.saving : connectDiscord ? 'Salvar e conectar' : m.saveChanges}"
        Set-DesktopSource $messaging 'message: result.hot_served ? m.connectingLive : m.restartToReconnect' "message: orbitaDiscord && update.enabled ? 'O status da conexão aparece neste painel.' : result.hot_served ? m.connectingLive : m.restartToReconnect"
        foreach ($feature in @('orbita-workspace.tsx', 'orbita-status.tsx', 'orbita-permission.tsx')) {
            Copy-Item -LiteralPath (Join-Path $integration "desktop\$feature") -Destination (Join-Path $desktop "src\app\chat\sidebar\$feature")
        }
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-signal-activity.tsx') -Destination (Join-Path $desktop 'src\app\chat\orbita-signal-activity.tsx')
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-system-usage.tsx') -Destination (Join-Path $desktop 'src\app\shell\orbita-system-usage.tsx')
        $titlebar = Join-Path $desktop 'src\app\shell\titlebar-controls.tsx'
        Set-DesktopSource $titlebar 'import { type ComponentProps, type MouseEvent, type ReactNode, useEffect, useState }' 'import { Fragment, type ComponentProps, type MouseEvent, type ReactNode, useEffect, useState }'
        $titlebarImport = "import { TitlebarIcon } from './titlebar-icon'"
        Set-DesktopSource $titlebar $titlebarImport "$titlebarImport`nimport { OrbitaSystemUsage } from './orbita-system-usage'"
        foreach ($toolList in @('visibleLeftTools', 'visibleSystemTools')) {
            $toolListAnchor = @"
        {$toolList.map(tool => (
          <TitlebarToolButton key={tool.id} navigate={navigate} tool={tool} />
        ))}
"@
            Set-DesktopSource $titlebar $toolListAnchor @"
        {$toolList.map(tool => (
          <Fragment key={tool.id}>
            {tool.id === 'settings' && <OrbitaSystemUsage />}
            <TitlebarToolButton navigate={navigate} tool={tool} />
          </Fragment>
        ))}
"@
        }
        $chatView = Join-Path $desktop 'src\app\chat\index.tsx'
        $runtimeImport = "import { useRuntimeMessageRepository } from './runtime-repository'"
        Set-DesktopSource $chatView $runtimeImport "$runtimeImport`nimport { OrbitaSignalActivity, useOrbitaSignalActivity } from './orbita-signal-activity'"
        $liveMessages = 'const messages = suppressMessages ? NO_MESSAGES : storeMessages'
        Set-DesktopSource $chatView $liveMessages 'const { activity, transcript: messages } = useOrbitaSignalActivity(suppressMessages || history.page ? null : storedId ?? null, suppressMessages ? NO_MESSAGES : storeMessages)'
        $runtimeProvider = '<AssistantRuntimeProvider runtime={runtime}>{children}</AssistantRuntimeProvider>'
        Set-DesktopSource $chatView $runtimeProvider "<OrbitaSignalActivity.Provider value={activity}>`n          $runtimeProvider`n        </OrbitaSignalActivity.Provider>"
        $threadView = Join-Path $desktop 'src\components\assistant-ui\thread\index.tsx'
        $threadImport = "import { ChatEmptySlot } from '@/components/assistant-ui/chat-empty-slot'"
        Set-DesktopSource $threadView $threadImport "$threadImport`nimport { OrbitaSignalProgress } from '@/app/chat/orbita-signal-activity'"
        Set-DesktopSource $threadView 'useMemo(() => <BackgroundResumeNotice />, [])' 'useMemo(() => <><BackgroundResumeNotice /><OrbitaSignalProgress /></>, [])'
        $chatMessages = Join-Path $desktop 'src\lib\chat-messages\types.ts'
        Set-DesktopSource $chatMessages 'export type ChatMessage = {' "export type ChatMessage = {`n  orbitaSignalFiles?: { missionId: string; id: string; name: string }[]"
        $chatRuntime = Join-Path $desktop 'src\lib\chat-runtime.ts'
        Set-DesktopSource $chatRuntime 'custom: { attachmentRefs: message.attachmentRefs ?? [], ...reactionMeta, ...timelineMeta }' 'custom: { attachmentRefs: message.attachmentRefs ?? [], orbitaSignalFiles: message.orbitaSignalFiles, ...reactionMeta, ...timelineMeta }'
        $userMessage = Join-Path $desktop 'src\components\assistant-ui\thread\user-message.tsx'
        $userMessageSource = [IO.File]::ReadAllText($userMessage)
        if (!$userMessageSource.Contains('import { OrbitaSignalAttachmentList }')) {
            [IO.File]::WriteAllText($userMessage, "import { OrbitaSignalAttachmentList } from '@/app/chat/orbita-signal-activity'`n" + $userMessageSource, [Text.UTF8Encoding]::new($false))
        }
        $userAttachmentRow = @'
          attachmentRefs.length > 0 && !chipOnlyTurn ? (
            <div className="mb-2 flex flex-wrap gap-1">
              <DirectiveContent text={attachmentRefs.join(' ')} />
            </div>
          ) : null
'@
        Set-DesktopSource $userMessage $userAttachmentRow @'
          <>
            {attachmentRefs.length > 0 && !chipOnlyTurn && (
              <div className="mb-2 flex flex-wrap gap-1">
                <DirectiveContent text={attachmentRefs.join(' ')} />
              </div>
            )}
            <OrbitaSignalAttachmentList />
          </>
'@
        Copy-Item -LiteralPath (Join-Path $integration 'desktop\orbita-pt-br.ts') -Destination (Join-Path $desktop 'src\i18n\orbita-pt-br.ts')
        $renderer = Join-Path $desktop 'src\main.tsx'
        Set-DesktopSource $renderer 'installClipboardShim()' "installOrbitaPortuguese()`ninstallClipboardShim()"
        Set-DesktopSource $renderer "import { ProfileI18nProvider as I18nProvider } from './i18n/profile-provider'" "import { ProfileI18nProvider as I18nProvider } from './i18n/profile-provider'`nimport { installOrbitaPortuguese } from './i18n/orbita-pt-br'"
        $localeProvider = Join-Path $desktop 'src\i18n\profile-provider.tsx'
        Set-DesktopSource $localeProvider '<I18nProvider scopeKey={scopeKey}>' "<I18nProvider scopeKey={scopeKey} initialLocale={window.orbitaStudio ? 'pt-br' : undefined}>"
        $icon = Join-Path $integration 'desktop\orbita-icon.png'
        Copy-Item -LiteralPath $icon -Destination (Join-Path $desktop 'src\app\chat\sidebar\orbita-icon.png')
        Copy-Item -LiteralPath $icon -Destination (Join-Path $desktop 'assets\orbita-icon.png')
        $main = Join-Path $desktop 'electron\main.ts'
        $tray = Join-Path $desktop 'electron\minimize-to-tray.ts'
        Set-DesktopSource $tray '  preferencesPath: string' @'
  preferencesPath: string
  defaultEnabled?: boolean
  labels?: { tooltip: string; show: string; quit: string }
'@
        Set-DesktopSource $tray "tray.setToolTip('Hermes')" "tray.setToolTip(options.labels?.tooltip ?? 'Hermes')"
        Set-DesktopSource $tray "label: 'Show Hermes'" "label: options.labels?.show ?? 'Show Hermes'"
        Set-DesktopSource $tray "label: 'Quit Hermes'" "label: options.labels?.quit ?? 'Quit Hermes'"
        Set-DesktopSource $tray @'
    let on = false

    try {
      on = JSON.parse(fs.readFileSync(options.preferencesPath, 'utf8')).enabled === true
    } catch {
      // Missing or malformed preference preserves ordinary minimize/close.
    }
'@ @'
    let on = options.defaultEnabled ?? false

    try {
      const saved = JSON.parse(fs.readFileSync(options.preferencesPath, 'utf8')).enabled
      if (typeof saved === 'boolean') on = saved
    } catch {
      // Use the application default without replacing the user's file.
    }
'@
        $trayConfig = "  preferencesPath: path.join(app.getPath('userData'), 'minimize-to-tray.json'),"
        Set-DesktopSource $main $trayConfig @"
$trayConfig
  ...(IS_WINDOWS && process.env.ORBITA_STUDIO_DESKTOP === '1' ? {
    defaultEnabled: true,
    labels: { tooltip: 'Orbita Studio', show: 'Abrir Orbita Studio', quit: 'Sair do Orbita Studio' }
  } : {}),
"@
        Set-DesktopSource $main "import { pathToFileURL } from 'node:url'" "import { pathToFileURL } from 'node:url'`nimport { registerOrbitaPool } from './orbita-pool'"
        $windowControls = 'registerWindowControlIpc(ipcMain, sender => BrowserWindow.fromWebContents(sender))'
        Set-DesktopSource $main $windowControls "$windowControls`nregisterOrbitaPool(ipcMain, () => pathToFileURL(resolveRendererIndex()).toString())"
        Set-DesktopSource $main "import { type ChildProcess, execFileSync, spawn } from 'node:child_process'" "import { type ChildProcess, execFile, execFileSync, spawn } from 'node:child_process'"
        Set-DesktopSource $main '  if (quitTeardown.begin(teardownTasks)) {' @'
  const orbitaMessagingCli = process.env.HERMES_DESKTOP_HERMES
  if (process.env.ORBITA_MANAGE_MESSAGING === '1' && orbitaMessagingCli) {
    teardownTasks.push({
      waitForCompletion: true,
      run: async () => {
        await backendShutdown.run().catch(() => undefined)
        await new Promise<void>(resolve => execFile(orbitaMessagingCli, ['gateway', 'stop'], {
          env: { ...process.env, HERMES_HOME }, windowsHide: true, timeout: 12_000
        }, error => {
          if (error) rememberLog('[orbita] O gateway não confirmou o encerramento.')
          resolve()
        }))
      }
    })
  }

  if (quitTeardown.begin(teardownTasks)) {
'@
        Set-DesktopSource $main "title: 'Hermes'," 'title: APP_NAME,'
        $titleBarTheme = 'let rendererTitleBarTheme = null'
        Set-DesktopSource $main $titleBarTheme "if (process.env.ORBITA_STUDIO_DESKTOP === '1') APP_ICON_PATHS.unshift(path.join(APP_ROOT, 'assets', 'orbita-icon.png'))`n`n$titleBarTheme"
        Set-DesktopSource $main "PRODUCT_IDENTITY.appId : 'com.nousresearch.hermes'" "PRODUCT_IDENTITY.appId : (process.env.ORBITA_STUDIO_DESKTOP === '1' ? 'local.orbita.studio' : 'com.nousresearch.hermes')"
        Set-DesktopSource (Join-Path $desktop 'index.html') '<title>Hermes</title>' '<title>Orbita Studio</title>'
        $english = Join-Path $desktop 'src\i18n\en.ts'
        Set-DesktopSource $english "'Give Hermes a task'" "'Diga o que quer fazer no Orbita Studio'"
        Set-DesktopSource $english "'Starting Hermes...'" "'Iniciando o Orbita Studio…'"
        Set-DesktopSource $english "'Reconnecting to Hermes…'" "'Reconectando ao Orbita Studio…'"
        $intro = Join-Path $desktop 'src\components\chat\intro.tsx'
        Set-DesktopSource $intro 'text={WORDMARK}' "text={window.orbitaStudio ? 'ORBITA STUDIO' : WORDMARK}"
        Set-DesktopSource $intro '{body}</p>' "{window.orbitaStudio ? 'Planeje e desenvolva seus projetos com o Antigravity.' : body}</p>"
        $status = Join-Path $desktop 'src\app\chat\composer\status-stack\index.tsx'
        Set-DesktopSource $status 'const sharedMetricsOffer = ownsFreeTierNotice' 'const sharedMetricsOffer = !window.orbitaStudio && ownsFreeTierNotice'
        $preload = Join-Path $desktop 'electron\preload.ts'
        $bridge = "contextBridge.exposeInMainWorld('hermesDesktop', {"
        Set-DesktopSource $preload $bridge @'
if (ipcRenderer.sendSync('orbita:enabled')) {
  contextBridge.exposeInMainWorld('orbitaStudio', (action: string, data?: unknown) => ipcRenderer.invoke('orbita:pool', action, data))
}

contextBridge.exposeInMainWorld('hermesDesktop', {
'@
        $sidebar = Join-Path $desktop 'src\app\chat\sidebar\index.tsx'
        $sidebarImport = "import { useLocation } from 'react-router'"
        $oldOrbitaImport = "import { OrbitaAntigravity } from './orbita-antigravity'"
        $orbitaImport = "import { OrbitaAntigravity, OrbitaBrand } from './orbita-antigravity'"
        if ([IO.File]::ReadAllText($sidebar).Contains($oldOrbitaImport)) {
            Set-DesktopSource $sidebar $oldOrbitaImport $orbitaImport
        } else {
            Set-DesktopSource $sidebar $sidebarImport "$sidebarImport`n$orbitaImport"
        }
        Set-DesktopSource $sidebar '<SidebarMenu className="gap-px">' "<SidebarMenu className=`"gap-px`">`n              <OrbitaAntigravity />"
        Set-DesktopSource $sidebar '<SidebarMenu className="gap-px">' "<OrbitaBrand />`n            <SidebarMenu className=`"gap-px`">"
        $previousWorkspaceImport = "import { OrbitaNewSession, OrbitaProductionMenu } from './orbita-workspace'"
        $workspaceImport = "import { OrbitaNewSession, OrbitaProductionMenu, OrbitaProjects } from './orbita-workspace'"
        if ([IO.File]::ReadAllText($sidebar).Contains($previousWorkspaceImport)) {
            Set-DesktopSource $sidebar $previousWorkspaceImport $workspaceImport
        } else {
            Set-DesktopSource $sidebar $orbitaImport "$orbitaImport`n$workspaceImport"
        }
        $storageNotice = '<SidebarStorageCorruptNotice />'
        Set-DesktopSource $sidebar $storageNotice "$storageNotice`n        <OrbitaProjects visible={showSessionSections && !trimmedQuery} />"
        Set-DesktopSource $sidebar '<OrbitaAntigravity />' "<OrbitaAntigravity />`n              <OrbitaProductionMenu />"
        $newSessionRow = "const isNewSession = item.id === 'new-session'"
        Set-DesktopSource $sidebar $newSessionRow "$newSessionRow`n                if (isNewSession && window.orbitaStudio) return <OrbitaNewSession key={item.id} />"
        $keybinds = Join-Path $desktop 'src\app\hooks\use-keybinds.ts'
        $newSessionKey = "'session.new': () => {"
        Set-DesktopSource $keybinds $newSessionKey "$newSessionKey`n      if (window.orbitaStudio) { window.dispatchEvent(new Event('orbita-new-mission')); return }"
        $wiring = Join-Path $desktop 'src\app\contrib\wiring.tsx'
        $newTab = 'const openNewSessionTab = useCallback(() => openNewSessionTabAction(openNewSessionTile), [openNewSessionTile])'
        Set-DesktopSource $wiring $newTab @'
const openNewSessionTab = useCallback(() => {
    if (window.orbitaStudio) window.dispatchEvent(new Event('orbita-new-mission'))
    else openNewSessionTabAction(openNewSessionTile)
  }, [openNewSessionTile])
'@
        $footer = Join-Path $desktop 'src\app\shell\statusbar-controls.tsx'
        $footerImport = "import { cn } from '@/lib/utils'"
        Set-DesktopSource $footer $footerImport "$footerImport`nimport { OrbitaRunStatus } from '@/app/chat/sidebar/orbita-status'"
        Set-DesktopSource $footer '{leftItems.filter(visible).map(item => (' "<OrbitaRunStatus />`n            {leftItems.filter(visible).map(item => ("
        $controller = Join-Path $desktop 'src\app\contrib\controller.tsx'
        $barFlag = "import { `$statusbarVisible } from '@/store/statusbar-prefs'"
        Set-DesktopSource $controller $barFlag "$barFlag`nimport { OrbitaRunStatus } from '@/app/chat/sidebar/orbita-status'"
        Set-DesktopSource $controller '{statusbarVisible && <WiredPane part="statusbar" />}' '{statusbarVisible ? <WiredPane part="statusbar" /> : <OrbitaRunStatus standalone />}'
        $composer = Join-Path $desktop 'src\app\chat\composer\controls.tsx'
        Set-DesktopSource $composer '{hideModelPill ? null : (' "{window.orbitaStudio ? <Button type=`"button`" variant=`"ghost`" size=`"sm`" onClick={() => window.dispatchEvent(new Event('orbita-open-antigravity'))}>Modelo e esforço</Button> : hideModelPill ? null : ("
        $composerImport = "import { ModelPill } from './model-pill'"
        Set-DesktopSource $composer $composerImport "$composerImport`nimport { OrbitaPermission } from '../sidebar/orbita-permission'"
        Set-DesktopSource $composer '{voiceControls}' "{window.orbitaStudio && <OrbitaPermission disabled={disabled || busy} />}`n          {voiceControls}"
        $submit = Join-Path $desktop 'src\app\chat\composer\hooks\use-middleware-submit.ts'
        $submitImport = "import { runComposerMiddleware } from '../contrib'"
        Set-DesktopSource $submit $submitImport "$submitImport`nimport { waitForOrbitaPermission } from '../../sidebar/orbita-permission'"
        $send = 'return onSubmitProp(draft.text, { ...options, attachments: draft.attachments })'
        Set-DesktopSource $submit $send "if (window.orbitaStudio) await waitForOrbitaPermission()`n      $send"
        $previewStore = Join-Path $desktop 'src\store\preview.ts'
        Set-DesktopSource $previewStore 'export interface PreviewTarget {' "export interface PreviewTarget {`n  orbitaProduction?: boolean"
        $previewPane = Join-Path $desktop 'src\app\chat\right-rail\preview-pane.tsx'
        Set-DesktopSource $previewPane "{target.kind === 'url' && tabId && <RealProfileConsentDialog" "{target.kind === 'url' && !target.orbitaProduction && tabId && <RealProfileConsentDialog"
        if (!(Test-Path -LiteralPath (Join-Path $desktop 'node_modules\vite\bin\vite.js'))) {
            & npx.cmd --yes "pnpm@$($lock.pnpm)" --filter 'hermes...' install --frozen-lockfile --trust-lockfile --ignore-scripts
            if ($LASTEXITCODE) { throw 'As dependências do cliente não foram instaladas.' }
        }
        $electron = Join-Path $desktop 'node_modules\electron'
        if (!(Test-Path -LiteralPath (Join-Path $electron 'dist\electron.exe'))) {
            & node (Join-Path $electron 'install.js')
            if ($LASTEXITCODE) { throw 'O runtime Electron não foi baixado.' }
        }
        & node 'apps/desktop/scripts/build.mjs'
        if ($LASTEXITCODE) { throw 'O build nativo do Hermes Desktop falhou.' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $desktop 'dist\electron-main.mjs'))) { throw 'O cliente nativo ainda precisa ser compilado.' }
    $module = Join-Path $upstream 'gateway\run_turn_runner.py'
    $source = [IO.File]::ReadAllText($module)
    $entry = '            run_message = self._native_image_run_message()'
    $marker = 'from tui_gateway.orbita_bridge import bridge_port, run_gateway_turn'
    if (!$source.Contains($marker)) {
        if ($source.Split($entry, [StringSplitOptions]::None).Count -ne 2) { throw 'O ponto de execução do Hermes mudou.' }
        $addition = @'
            from tui_gateway.orbita_bridge import bridge_port, run_gateway_turn
            if (port := bridge_port()) is not None:
                images = self._runner._consume_pending_native_image_paths(ctx.session_key)
                return run_gateway_turn(port, ctx.session_id, agent, ctx.message, images, agent_history)
            run_message = self._native_image_run_message()
'@
        [IO.File]::WriteAllText($module, $source.Replace($entry, $addition), [Text.UTF8Encoding]::new($false))
    }
    $bridgeTurn = 'return run_gateway_turn(port, ctx.session_id, agent, ctx.message, images, agent_history)'
    $sourceTurn = 'return run_gateway_turn(port, ctx.session_id, agent, ctx.message, images, agent_history, ctx.source)'
    if (![IO.File]::ReadAllText($module).Contains('signal_files=signal_files')) {
        Set-DesktopSource $module $bridgeTurn $sourceTurn
        Set-DesktopSource $module $sourceTurn @'
from tui_gateway.orbita_signal_files import consume_signal_files
                signal_files = consume_signal_files(self._runner, ctx.session_key)
                return run_gateway_turn(port, ctx.session_id, agent, ctx.message, images, agent_history, ctx.source, signal_files=signal_files)
'@
    }
    $inbound = Join-Path $upstream 'gateway\run_inbound.py'
    $inboundText = '        message_text = self._prefix_inbound_sender_context(event, source, message_text)'
    $signalInbound = @'
        if source.platform == Platform.SIGNAL:
            from tui_gateway.orbita_bridge import bridge_port
            from tui_gateway.orbita_signal_files import prepare_signal_files
            if bridge_port() is not None and (orbita_caption := prepare_signal_files(self, session_key, event)) is not None:
                return orbita_caption
        message_text = self._prefix_inbound_sender_context(event, source, message_text)
'@
    $multiInbound = @'
        if source.platform in {Platform.SIGNAL, Platform.DISCORD}:
            from tui_gateway.orbita_bridge import bridge_port
            from tui_gateway.orbita_signal_files import prepare_signal_files
            if bridge_port() is not None and (orbita_caption := prepare_signal_files(self, session_key, event, source.platform.value)) is not None:
                return orbita_caption
        message_text = self._prefix_inbound_sender_context(event, source, message_text)
'@
    $inboundSource = [IO.File]::ReadAllText($inbound)
    $inboundAnchor = if ($inboundSource.Contains($multiInbound)) { $multiInbound } elseif ($inboundSource.Contains($signalInbound)) { $signalInbound } else { $inboundText }
    Set-DesktopSource $inbound $inboundAnchor @'
        if source.platform in {Platform.SIGNAL, Platform.DISCORD}:
            from tui_gateway.orbita_bridge import bridge_port
            from tui_gateway.orbita_signal_files import prepare_signal_files
            if bridge_port() is not None:
                if (orbita_caption := prepare_signal_files(self, session_key, event, source.platform.value)) is not None:
                    return orbita_caption
                if source.platform == Platform.DISCORD and not event.media_urls:
                    return message_text
        message_text = self._prefix_inbound_sender_context(event, source, message_text)
'@
    $adapterSignal = Join-Path $upstream 'plugins\platforms\signal\adapter.py'
    Set-DesktopSource $adapterSignal 'async def _collect_attachments(self, attachments_data: list) ->' 'async def _collect_attachments(self, attachments_data: list, filenames: list[str] | None = None) ->'
    $signalMime = '                    media_types.append(att.get("contentType") or _ext_to_mime(ext))'
    Set-DesktopSource $adapterSignal $signalMime @'
                    media_types.append(att.get("contentType") or _ext_to_mime(ext))
                    if filenames is not None:
                        filenames.append(att.get("filename") or att.get("fileName") or "")
'@
    Set-DesktopSource $adapterSignal '        media_urls, media_types = [], []' '        media_urls, media_types, media_filenames = [], [], []'
    Set-DesktopSource $adapterSignal 'await self._collect_attachments(attachments_data)' 'await self._collect_attachments(attachments_data, media_filenames)'
    Set-DesktopSource $adapterSignal 'media_types=media_types, timestamp=timestamp,' 'media_types=media_types, media_filenames=media_filenames, timestamp=timestamp,'
    Set-DesktopSource $adapterSignal '"quote": quote_data if quote_data else None},' '"quote": quote_data if quote_data else None, "orbita_attachment_count": len(attachments_data)},'
    $discordCard = Join-Path $upstream 'plugins\platforms\discord\dashboard_card.py'
    Set-DesktopSource $discordCard 'dashboard_description="Connect Hermes to Discord DMs, channels, and threads.",' 'dashboard_description="Converse com o Orbita pelo bot em um canal privado do Discord.",'
    $discordFields = '    dashboard_env_vars=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS", "DISCORD_ALLOWED_CHANNELS"),'
    Set-DesktopSource $discordCard '    dashboard_env_vars=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS"),' $discordFields
    $oldDiscordCard = @'
    dashboard_env_vars=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS", "DISCORD_ALLOWED_CHANNELS"),
    dashboard_required_env=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS", "DISCORD_ALLOWED_CHANNELS"),
'@
    $discordCardAnchor = if ([IO.File]::ReadAllText($discordCard).Contains($oldDiscordCard)) { $oldDiscordCard } else { $discordFields }
    Set-DesktopSource $discordCard $discordCardAnchor @'
    dashboard_env_vars=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS", "DISCORD_ALLOWED_CHANNELS"),
    dashboard_required_env=("DISCORD_BOT_TOKEN", "DISCORD_ALLOWED_USERS"),
'@
    $channelCatalog = Join-Path $upstream 'hermes_cli\web_server_messaging.py'
    $oldChannelCatalog = @'
        required = set(override.get("required_env", ()))
        explicit = tuple(key for key in override["env_vars"] if key in required or not _is_setup_hidden_env(key))
'@
    $channelCatalogAnchor = if ([IO.File]::ReadAllText($channelCatalog).Contains($oldChannelCatalog)) { $oldChannelCatalog } else {
        '        explicit = tuple(key for key in override["env_vars"] if not _is_setup_hidden_env(key))'
    }
    Set-DesktopSource $channelCatalog $channelCatalogAnchor @'
        required = set(override.get("required_env", ()))
        explicit = tuple(key for key in override["env_vars"]
                         if key in required or key == "DISCORD_ALLOWED_CHANNELS" or not _is_setup_hidden_env(key))
'@
    $discordAdapter = Join-Path $upstream 'plugins\platforms\discord\adapter.py'
    Set-DesktopSource $discordAdapter '        """Handle one Discord message and report whether it reached dispatch."""' @'
        """Handle one Discord message and report whether it reached dispatch."""
        from tui_gateway.orbita_bridge import bridge_port
        if bridge_port() is not None:
            from tui_gateway.orbita_messaging import discord_chat_allowed
            if is_automated_message(message) or not discord_chat_allowed(
                str(message.author.id), str(message.channel.id), isinstance(message.channel, discord.DMChannel),
            ):
                return False
'@
    $discordSlash = Join-Path $upstream 'plugins\platforms\discord\adapter_slash_auth.py'
    Set-DesktopSource $discordSlash '        user_id = str(user.id)' @'
        user_id = str(user.id)
        from tui_gateway.orbita_bridge import bridge_port
        if bridge_port() is not None:
            from tui_gateway.orbita_messaging import discord_chat_allowed
            destination = getattr(interaction, "channel_id", None) or getattr(chan_obj, "id", None)
            if not discord_chat_allowed(user_id, str(destination or ""), in_dm):
                return (False, "Canal ou usuário fora da autorização do Orbita.")
'@
    $discordCode = [IO.File]::ReadAllText($discordAdapter)
    if (!$discordCode.Contains('_collect_attachment_media(self, all_attachments: list, filenames: list[str] | None = None)')) {
        $discordSignature = '_collect_attachment_media(self, all_attachments: list)'
        if (!$discordCode.Contains($discordSignature)) { throw 'O ponto de anexos do Discord mudou.' }
        $discordCode = $discordCode.Replace($discordSignature, '_collect_attachment_media(self, all_attachments: list, filenames: list[str] | None = None)')
        $discordCode = [regex]::Replace($discordCode, '(?m)^( +)media_text_inlined\.append\(False\)\r?$', { param($match)
            $indent = $match.Groups[1].Value
            return "${indent}media_text_inlined.append(False)`n${indent}if filenames is not None:`n$indent    filenames.append(att.filename or `"`")"
        })
        [IO.File]::WriteAllText($discordAdapter, $discordCode, [Text.UTF8Encoding]::new($false))
    }
    $discordCollect = @'
        media_urls, media_types, media_text_inlined, pending_text_injection = await self._collect_attachment_media(
            all_attachments)
'@
    Set-DesktopSource $discordAdapter $discordCollect @'
        media_filenames = []
        media_urls, media_types, media_text_inlined, pending_text_injection = await self._collect_attachment_media(
            all_attachments, media_filenames)
'@
    $discordMedia = 'media_text_inlined=media_text_inlined,'
    Set-DesktopSource $discordAdapter $discordMedia 'media_text_inlined=media_text_inlined, media_filenames=media_filenames,'
    $discordEvent = @'
        event = MessageEvent(
            text=event_text, message_type=msg_type, source=source, raw_message=message,
'@
    $discordWithCaption = @'
        from tui_gateway.orbita_bridge import bridge_port
        if bridge_port() is not None and media_urls:
            event_text = normalized_content or ""
        event = MessageEvent(
            text=event_text, message_type=msg_type, source=source, raw_message=message,
'@
    $discordEventAnchor = if ([IO.File]::ReadAllText($discordAdapter).Contains($discordWithCaption)) { $discordWithCaption } else { $discordEvent }
    Set-DesktopSource $discordAdapter $discordEventAnchor @'
        from tui_gateway.orbita_bridge import bridge_port
        if bridge_port() is not None and all_attachments:
            event_text = normalized_content or ""
        event = MessageEvent(
            text=event_text, message_type=msg_type, source=source, raw_message=message,
'@
    $discordEventEnd = '        if is_automated_message(message) and self._is_bot_tag_debounce_continuation(message):'
    Set-DesktopSource $discordAdapter $discordEventEnd @'
        event._orbita_attachment_count = len(all_attachments)
        if is_automated_message(message) and self._is_bot_tag_debounce_continuation(message):
'@
    $bootstrap = Join-Path $upstream 'gateway\run_bootstrap.py'
    $oldAlerts = 'from tui_gateway.orbita_telegram import start_alerts'
    if ([IO.File]::ReadAllText($bootstrap).Contains($oldAlerts)) {
        Set-DesktopSource $bootstrap $oldAlerts 'from tui_gateway.orbita_messaging import start_alerts'
    }
    Set-DesktopSource $bootstrap '            await wait_gateway_runtime(runner)' @'
            from tui_gateway.orbita_messaging import start_alerts
            orbita_alerts = start_alerts(runner)
            try:
                await wait_gateway_runtime(runner)
            finally:
                if orbita_alerts is not None:
                    orbita_alerts.cancel()
                    await asyncio.gather(orbita_alerts, return_exceptions=True)
'@
    $previous = Join-Path $upstream 'tui_gateway\prompt_turn.py'
    if ([IO.File]::ReadAllText($previous).Contains('import run_turn as run_orbita_turn')) {
        & git -C $upstream restore --worktree -- tui_gateway/prompt_turn.py
        if ($LASTEXITCODE) { throw 'O ponto anterior do gateway não pôde ser restaurado.' }
    }
    foreach ($moduleName in @('orbita_bridge.py', 'orbita_messaging.py', 'orbita_signal_chat.py', 'orbita_signal_tools.py', 'orbita_signal_files.py')) {
        Copy-Item -LiteralPath (Join-Path $integration "gateway\$moduleName") -Destination (Join-Path $upstream "tui_gateway\$moduleName")
    }
} finally { Pop-Location }

Write-Output $desktop
