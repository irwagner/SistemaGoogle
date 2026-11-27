import { registerAppLocale } from './registry'

export function installOrbitaPortuguese() {
  if (!window.orbitaStudio) return
  registerAppLocale('pt-br', { endonym: 'Português (Brasil)', englishName: 'Brazilian Portuguese', translations: {
    common: { close: 'Fechar', cancel: 'Cancelar', save: 'Salvar', done: 'Concluir', edit: 'Editar', delete: 'Excluir', copy: 'Copiar', open: 'Abrir', loading: 'Carregando…', back: 'Voltar', search: 'Buscar', settings: 'Configurações', apply: 'Aplicar', rename: 'Renomear', confirm: 'Confirmar' },
    sidebar: {
      nav: { 'new-session': 'Nova sessão', capabilities: 'Recursos do agente', messaging: 'Mensagens', artifacts: 'Artefatos', cron: 'Tarefas agendadas' },
      searchAria: 'Buscar sessões', searchPlaceholder: 'Buscar sessões…', clearSearch: 'Limpar busca', results: 'Resultados',
      pinned: 'Fixadas', sessions: 'Sessões', terminal: 'Terminal', files: 'Arquivos', review: 'Revisão', logs: 'Registros', cronJobs: 'Tarefas agendadas',
      showProjects: 'Mostrar projetos', showSessions: 'Mostrar sessões', groupAriaGrouped: 'Mostrar sessões em uma lista', groupAriaUngrouped: 'Agrupar sessões por pasta',
      groupTitleGrouped: 'Desagrupar sessões', groupTitleUngrouped: 'Agrupar por pasta', shiftClickHint: 'Shift + clique para fixar uma conversa',
      noWorkspace: 'Sem pasta do projeto', projectEmpty: 'Nenhuma sessão neste projeto', projectLoadFailed: 'Falha ao carregar sessões',
      noSessions: 'Nenhuma sessão', noFilterMatches: 'Nenhuma sessão corresponde aos filtros', newSessionIn: (label: string) => `Nova sessão em ${label}`,
      loading: 'Carregando…', projects: { showAllSessions: 'Mostrar todas as sessões', sectionLabel: 'Projetos', home: 'Início',
        autoDiscovered: 'Encontrado automaticamente', newButton: 'Novo projeto', createTitle: 'Novo projeto', renameTitle: 'Renomear projeto',
        addFolderTitle: 'Adicionar pasta', foldersLabel: 'Pastas', noFolders: 'Nenhuma pasta adicionada', addFolder: 'Adicionar pasta',
        create: 'Criar', menu: 'Ações', menuRename: 'Renomear…', menuDelete: 'Excluir', reveal: 'Mostrar no Explorador', copyPath: 'Copiar caminho',
        startWork: 'Novo worktree', moveToProject: 'Mover para um projeto' } },
    composer: { message: 'Mensagem', placeholderStarting: 'Iniciando o Orbita Studio…', placeholderReconnecting: 'Reconectando ao Orbita Studio…',
      placeholderFollowUp: 'Envie uma mensagem', newSessionPlaceholders: ['O que você quer fazer?', 'Descreva o seu pedido', 'Escreva o objetivo da missão'],
      followUpPlaceholders: ['Envie uma mensagem', 'Acrescente contexto', 'Continue de onde parou'] },
    messaging: {
      getCredentials: 'Como obter as credenciais', openSetupGuide: 'Abrir guia de configuração',
      required: 'Obrigatório', recommended: 'Recomendado', restartGateway: 'Reiniciar gateway',
      enabled: 'Ativado', disabled: 'Desativado', saveChanges: 'Salvar alterações', saving: 'Salvando…', saved: 'Salvo',
      enableAria: (name: string) => `Ativar ${name}`, disableAria: (name: string) => `Desativar ${name}`,
      listEntryPlaceholder: 'Digite um ID', states: { connected: 'Conectado', not_configured: 'Precisa de configuração' },
      advanced: (count: number) => `Opções avançadas (${count})`, restartNow: 'Reiniciar agora',
      restartNeeded: 'As alterações foram salvas. Reinicie a conexão para aplicá-las.',
      fieldCopy: {
        DISCORD_BOT_TOKEN: { label: 'Token do bot', help: 'Na aba Bot do portal do Discord, ative Message Content Intent e copie o token.' },
        DISCORD_ALLOWED_USERS: { label: 'IDs de usuário autorizados', help: 'Ative o Modo desenvolvedor no Discord e copie o ID do seu perfil.' },
        DISCORD_ALLOWED_CHANNELS: { label: 'IDs de canais autorizados', help: 'Copie o ID do canal privado ou do tópico exato que você vai usar.' }
      },
      telegramQr: {
      title: 'Conecte seu bot do Telegram', subtitle: 'As credenciais do bot ficam no perfil local do Orbita.',
      quickSetup: 'Configuração rápida', recommended: 'Recomendado',
      quickHelp: 'Escaneie o QR e confirme no Telegram para criar o bot e autorizar sua conta.',
      createWithQr: 'Criar com QR', starting: 'Iniciando…', scanHint: 'Escaneie com o Telegram ou abra o link neste computador.',
      waiting: 'Aguardando o Telegram…', openTelegram: 'Abrir Telegram', ready: 'Bot criado',
      allowedUsers: 'Usuários autorizados', ownerDetected: 'Conta identificada', saveAndRestart: 'Salvar e reiniciar', applying: 'Salvando…'
    } },
    commandCenter: { projects: 'Projetos', openFolder: 'Abrir pasta como projeto…', commandCenter: 'Central de comandos',
      newSessionInProject: (project: string) => `Nova sessão em ${project}`, toggleBrowser: 'Alternar navegador' },
    shell: { statusbar: { terminal: 'Terminal', browser: 'Navegador', files: 'Arquivos', review: 'Revisão', cron: 'Tarefas agendadas', settings: 'Configurações' } },
    settings: { appearance: {
      minimizeToTrayTitle: 'Manter o Orbita na bandeja',
      minimizeToTrayDesc: 'Ao minimizar ou fechar a janela, o Orbita continua rodando na bandeja do Windows.',
      minimizeToTrayUnavailable: 'A bandeja do sistema está indisponível.'
    } },
    language: { label: 'Idioma' }
  } }, 'app')
}
