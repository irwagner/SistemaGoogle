# Orbita Studio para Windows

Abra `Abrir-OrbitaStudioNativo.cmd` na raiz do repositório. O atalho aproveita o Orbita aberto na porta 7784 ou inicia o servidor local em segundo plano. O cliente usa a base do Hermes Desktop e mantém seus layouts, navegação, painéis e navegador integrado.

## Trabalhar no Orbita

- Na barra lateral, abra **Projetos → Novo projeto**, dê um nome e mantenha a pasta padrão do Orbita ou escolha **Vincular pasta**. Abra o projeto para ver os chats salvos e use o botão **+** do projeto para criar mais chats. O nome do chat é opcional.
- **Nova sessão** ou `Ctrl+N` abre o mesmo formulário para escolher um projeto existente. Cada chat é uma missão com histórico e worktree próprios dentro do projeto. Ao reabrir um chat, o Orbita retoma a missão registrada.
- Para uma pasta vinculada sem repositório Git, o Orbita cria a cópia de trabalho de cada novo chat a partir dos arquivos atuais da pasta. As edições ficam no worktree do chat. O Orbita guarda o repositório da cópia no seu diretório de dados.
- Envie o pedido normalmente para conversar e planejar. Para começar a execução diretamente no modo padrão, escreva `Executar:` seguido do pedido.
- Ative **Permissão total** ao lado de **Modelo e esforço** para executar os próximos pedidos sem aprovar plano, ferramentas ou diff. O Antigravity pode executar comandos, baixar e instalar programas com os direitos da conta do Windows. A escolha fica salva no perfil desktop. Clique no botão novamente para voltar ao modo padrão.
- `Ctrl+Shift+L` alterna o navegador integrado. A integração do Orbita com esse navegador e com o Computer Use do Hermes ainda está pendente.

## Contas e cotas

1. Na barra lateral, clique em **Contas do Antigravity**. As contas salvas aparecem mesmo quando o pool está em espera.
2. Use **Conectar conta Google** para abrir o login oficial do Antigravity CLI. Conclua o login com a conta desejada. O painel acompanha o início e o fim da conexão.
3. Use **Atualizar cotas** para consultar o saldo de 5 horas e semanal de Gemini e de Claude e GPT. A conta em uso aparece enquanto o pool atende uma missão.
4. Em **Conexão nos próximos pedidos**, escolha **Usar contas selecionadas** e marque as contas na ordem desejada. Escolha o modelo e use **Salvar preferências**. Essas preferências se aplicam aos próximos pedidos enviados por este cliente. A opção **Usar a conexão da missão** respeita as contas já salvas na missão.
5. Use **Modelo e esforço** no chat ou **Contas do Antigravity** para escolher o esforço. O ACP confirma o nível aplicado quando a sessão começa. O rodapé consulta o servidor local a cada três segundos. Durante uma missão com o pool, o servidor consulta a cota da conta efetiva em intervalos de cinco segundos, respeitando o cache e a pausa após falhas. A barra mostra o menor saldo disponível entre as duas janelas da família do modelo em uso. A posição da conta vem da ordem da missão.
6. **Testar rotação** consulta as contas selecionadas no Google e simula a troca no pool sem gerar código nem gastar cota de mensagens. Quando o Google exigir verificação, use **Verificar no Google** na conta indicada. Conclua na página oficial e volte ao Orbita para a nova checagem.

## Telegram

Em **Mensagens → Telegram**, use **Criar com QR** e vincule a sua conta do Telegram. No chat privado do bot, envie `!missoes` e depois `!usar ID` para selecionar uma missão do Orbita. Você pode pedir trabalho, ver o estado com `!status`, pedir uma interrupção com `!parar` e retomar um pedido pendente com `!retomar`. **Permissão total** no Orbita também se aplica aos pedidos do bot.

Com o Orbita e o gateway em execução no PC, o bot avisa o chat privado pareado quando uma missão precisar de intervenção, ficar sem cota ou for interrompida. As confirmações de conta do Google continuam na página oficial de verificação.

## Signal

Abra **Mensagens → Signal → Vincular aparelho Signal**. No celular, vá a **Configurações → Dispositivos vinculados**, escaneie o QR da janela do Orbita e reabra o cliente. Na conversa **Anotações**, fale normalmente. Por exemplo, envie “Como está o LifeHub?” ou “Continua de onde parou”. O Orbita lembra a missão escolhida, pede o nome se houver dúvida e avisa sobre bloqueios. O serviço local do Signal inicia junto com o Orbita.

Para criar um projeto pelo Signal, envie “Quero criar um novo projeto chamado Gabarito”. O Orbita usa a pasta padrão e lista o projeto em **Projetos** no desktop.

O Signal usa uma sessão independente do Antigravity CLI para conversar e entender os pedidos. A escolha automática prioriza o Gemini Flash com esforço baixo quando disponível e usa o Hydrogen com esforço baixo como alternativa. Envie `!model` e responda com o número da lista. `0` volta para a escolha automática. A preferência vale para a conversa do Signal.

Pedidos claros de implementação, correção ou testes seguem para a missão escolhida. O desenvolvimento usa Gemini Neon ou Claude Opus. Enquanto o trabalho roda, você pode continuar conversando no Signal e consultar o andamento. A sessão de conversa usa a conta já conectada no Antigravity CLI do PC.

Abra o chat da missão no desktop para ver o tempo decorrido, a atividade mais recente e a resposta enquanto o trabalho iniciado pelo Signal roda. Se você pedir um novo chat e ditar um prompt, o Orbita coloca só o prompt no novo chat.

Na conversa **Anotações** pareada, você pode mandar “Tira um print da tela”, “Abra a Calculadora” ou “Leia o Chat 1 do projeto Gabarito”. O Orbita envia a captura como imagem, abre o programa no Windows ou consulta as mensagens do chat. Você também pode usar `!print`, `!abrir Calculadora` e `!chat Gabarito / Chat 1`. Para uma janela específica, envie `!print` seguido do título da janela visível. A captura precisa da sessão do Windows desbloqueada.

Envie um anexo com a legenda `Coloque este arquivo no Chat 1 do projeto Gabarito com o prompt: “Resuma o documento”`. Você também pode mandar o arquivo primeiro e a instrução em seguida. O anexo fica disponível no chat para download e o Signal recebe a resposta final do Antigravity ao concluir. Aceita imagens, PDF, DOCX, XLSX, CSV, texto e código, até 6 arquivos de 5 MB cada e 10 MB por pedido.

Você pode colar um prompt longo. Se o Signal o enviar como anexo automático, o Orbita recupera o texto e encaminha o pedido. Uma mensagem como `Envie no Chat 1 do Atlas: implemente a próxima etapa.` já indica o destino. Pedidos iniciados em Anotações recebem a resposta final ali mesmo, inclusive quando são apenas texto.

## Discord

O bot usa o gateway que o Orbita abre no PC. No Discord, o servidor privado é o espaço de chat em que o bot é adicionado. As conexões de mensagens funcionam enquanto o Orbita Desktop está aberto, inclusive na bandeja. Ao sair do aplicativo, ele encerra o gateway do perfil.

1. No Orbita, abra **Mensagens → Discord → Abrir portal do Discord**. Clique em **New Application** e dê o nome **Orbita Studio**. Na aba **Bot**, ative **Message Content Intent** e gere o token em **Reset Token**. Em **Installation**, use **Guild Install** com o escopo `bot` para adicionar o bot ao seu espaço privado no Discord. [Guia oficial do Discord](https://docs.discord.com/developers/quick-start/getting-started)
2. No Discord, ative **Modo desenvolvedor** e copie seu ID de usuário. No Orbita, cole o token e o ID de usuário e clique em **Salvar e conectar**. [Como copiar o ID](https://support.discord.com/hc/en-us/articles/206346498-Where-can-I-find-my-User-Server-Message-ID)
3. Abra o perfil **Orbita Studio** na lista de membros do seu espaço privado no Discord e escolha **Mensagem**. Envie `!vincular Atlas / Chat 1` uma vez para ligar esse chat à missão. Depois fale normalmente na mensagem privada. O Signal e o Discord podem ficar conectados no mesmo perfil.

Para conversar em um canal do servidor, coloque o ID exato do canal ou tópico em **Opções avançadas**. O bot precisa de permissão para ver o canal e enviar mensagens. Para tópicos, habilite também a permissão de enviar mensagens em tópicos.

## Inicialização, bandeja e indicadores

O comando `Abrir-OrbitaStudioNativo.cmd -IniciarComWindows` registra o Orbita para abrir ao entrar no Windows na conta atual. Fechar a janela mantém o Orbita e as conexões de mensagens na bandeja. Para encerrar de verdade, use **Sair do Orbita Studio** no menu do ícone da bandeja. Na barra superior, perto da engrenagem, os indicadores mostram CPU, RAM e espaço ocupado na unidade do Windows.

## Vídeo, Pesquisa e Áudio

Os três atalhos da barra lateral abrem os percursos da Central no navegador integrado. Use **Vídeo e minisséries** para preparar roteiro e storyboard, importar takes, substituir um take de uma cena e exportar com FFmpeg. **Pesquisa** reúne pergunta, fontes, afirmações e relatório. **Áudio** mede, revisa e vincula faixas às cenas.

A montagem local usa arquivos importados. Geração externa e qualquer uso de créditos dependem do serviço contratado e de suas autorizações. A instalação do FFmpeg e do FFprobe pode ser feita pelo PATH ou pelas variáveis `CENTRAL_FFMPEG` e `CENTRAL_FFPROBE`.

O catálogo português do Brasil cobre a navegação e os fluxos do Orbita. Textos de partes herdadas ainda podem aparecer em inglês. O chat aceita pedidos sem limite fixo de palavras no Orbita. O contexto aceito pelo modelo e os limites de tamanho dos anexos continuam se aplicando.

A revisão manual de diffs fica na interface web do Orbita. O perfil do cliente nativo guarda suas preferências e a cópia inicial das conversas. Os dados de projetos, os worktrees e os checkpoints ficam no diretório de dados do Orbita. Mensagens enviadas pela interface web depois da importação inicial podem aparecer apenas nela.

## Compilar e verificar

Na raiz do repositório, use o SDK .NET indicado em `global.json`, Git, Node 26 e `uv`. Publique os serviços do Orbita e prepare o cliente nativo.

```powershell
dotnet publish .\src\Orbita.Studio\Orbita.Studio.csproj -c Release -r win-x64 --self-contained -o .\entrega\Windows\OrbitaStudio-0.3.17-win-x64\studio
dotnet publish .\src\Central\Central.csproj -c Release -r win-x64 --self-contained -o .\entrega\Windows\OrbitaStudio-0.3.10-win-x64\producao
.\scripts\Preparar-OrbitaHermesNativo.ps1
.\Abrir-OrbitaStudioNativo.cmd
```

O segundo serviço fornece os percursos de Vídeo, Pesquisa e Áudio. O script de preparação baixa o checkout fixado em `src/Orbita.Hermes/upstream.json`, compila o aplicativo e instala o adaptador Python do gateway. A licença MIT e os avisos do Electron permanecem no checkout e na distribuição.

Para verificar o desktop com dados descartáveis, compile `tests/Orbita.Tests/Orbita.Tests.csproj` em Release e execute `node scripts/verificar-orbita-hermes-nativo.cjs`. O teste abre uma janela nativa com perfil temporário e valida contas, cotas, missões, mensagens e a bandeja.

O adaptador só entra em uso no perfil que aponta para o Orbita local. O Hermes Desktop fornece a interface, o histórico nativo e o transporte. O backend .NET continua orquestrando o Antigravity, o pool de contas, os worktrees e os checkpoints.
