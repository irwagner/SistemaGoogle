# Central 0.2.0 para Windows

## Abrir e continuar

Extraia o ZIP em pasta local. Compare o SHA-256 com o arquivo ao lado do ZIP e abra **Abrir-Central.cmd**. Para instalar em seu perfil Windows, execute **Instalar.ps1** na pasta extraída. A pasta de programas é independente dos dados. A Central migra o banco existente ao abrir. Antes de atualizar, feche a 0.1.0 e faça um snapshot para o caso de rollback.

- **Programação → Criar sistema.** Nomeie o projeto e descreva o que o sistema deve fazer. Escolha um modelo Ollama instalado, gere o plano, edite e aprove. A fila mostra a etapa, as tentativas, os arquivos, o diff e o laudo do Git. Para cadastros locais, o modelo cria a especificação e o renderizador da Central monta o HTML autocontido. A origem fica nos arquivos. Para um Git existente, importe um Git bundle e use o percurso assistido. O pacote fixa a base e o retorno aceita patch e recibo.
- **Vídeo e minisséries.** Escolha vídeo avulso ou série. Escreva a ideia, salve roteiro, personagens e três planos, aprove o storyboard e abra a montagem. Selecione uma tomada por plano, adicione áudio, registre a timeline, faça o render e revise. Para refazer uma cena, adicione a alternativa apenas no plano correspondente, use **Substituir só neste plano** e gere outra timeline. A edição anterior fica no histórico.
- **Pesquisa, Áudio e Distribuição.** Abra um trabalho e vincule os arquivos relevantes. Pesquisa exporta relatório com referências. Áudio mede WAV/MP3 e vincula faixas aprovadas a cenas do mesmo projeto. Distribuição guarda aprovação, destino, visibilidade e pacote. Use **Disponibilizar master no projeto** no trabalho de vídeo para usar o arquivo aprovado em distribuição.

O Ollama fica em 127.0.0.1:11434 com um modelo instalado. Git é usado para worktrees, commits, patches e bundles. FFmpeg e ffprobe são usados na montagem, decodificação e medição de duração. Consulte Diagnóstico e Contas e recursos para ferramentas, acessos, observações de cota, custos e métricas.

## Autorizações externas

| Produto | Ação do titular | Finalidade e efeito |
|---|---|---|
| Antigravity pessoal | Escolher a conta no cliente oficial e autorizar o briefing e a revisão base. Conferir conta, modelo e modo de cobrança na tela. | A Central exporta pacote e recebe patch e recibo. O operador executa no cliente. A CLI tem modo direto para scripts e CI. O plano consumidor limita wrappers de terceiros e rotação automática. |
| Gemini Notebook pessoal | Escolher conta pessoal e criar cliente OAuth desktop em projeto Cloud com a API habilitada. Cadastrar perfil em **Contas e recursos → Autorizações Google** e entrar pelo Google com a conta fixada. | Na investigação, autorizar o texto ou arquivo, ID do caderno e projeto Cloud. A fila consulta o identificador original até ACTIVE ou FAILED. Confirme o custo e a política vigente do projeto antes do primeiro teste. |
| Flow | Conferir no cliente oficial perfil, preço por saída, quantidade de saídas e direitos das referências. Autorizar créditos para uma cena. | A ficha registra o teto esperado. Gere no cliente oficial e importe a tomada e o recibo para o plano escolhido. |
| YouTube | Escolher projeto com YouTube Data e Analytics, cliente OAuth desktop, conta titular e ID do canal. Autorizar o master, destino privado, cota, categoria e classificação infantil. | A Central guarda a sessão para retomada e verifica canal e visibilidade pelo identificador confirmado. As métricas usam a autorização do mesmo canal. |
| Voz, imagem, transcrição e música | Autorizar fornecedor, modalidade, licença e teto para cada efeito. | A Central aceita importação local com proveniência. A geração de cada capacidade depende do ensaio da superfície escolhida. |

Credenciais e sessões ficam protegidas pelo DPAPI no perfil Windows. Restaurar para outra pasta exige nova autorização. Os ensaios locais usam dados fictícios. [FAQ Antigravity](https://antigravity.google/docs/faq) · [Google OAuth desktop](https://developers.google.com/identity/protocols/oauth2/native-app) · [Gemini Notebook](https://developers.google.com/notebook/docs/sources) · [Uploads YouTube](https://developers.google.com/youtube/data/guides/using_resumable_upload_protocol)
