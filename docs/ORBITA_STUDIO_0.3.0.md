# Orbita Studio 0.3.0

Cliente local para Windows x64. Extraia o ZIP e execute `Abrir-OrbitaStudio.cmd`. O painel abre em `http://127.0.0.1:7784/`. O ZIP é autocontido para .NET. Para iniciar uma missão, instale também Git e Antigravity CLI com ACP. O Account Pool usa Node.js 20 ou superior e PowerShell 7. O FFmpeg e o FFprobe são detectados no `PATH` ou pelos caminhos absolutos `CENTRAL_FFMPEG` e `CENTRAL_FFPROBE`.

## Trabalhar no Orbita

1. Crie um projeto ou aponte para a raiz de um repositório Git. Depois crie uma missão. O Orbita abre uma branch e um worktree a partir do `HEAD` da pasta escolhida. Mudanças soltas no repositório original ficam na pasta original.
2. Converse no painel **Plano**. Use **Executar** para começar diretamente. Depois de discutir o pedido, envie **Pode fazer**. **Aprovar Plano** é um atalho para esse mesmo comando. O Orbita envia o pedido para o `agy`, que planeja, implementa e testa.
3. Veja **Ao Vivo** para acompanhar os eventos ou **Diff & Evidências** para conferir as alterações. **Aceitar Diff** registra a revisão na branch da missão. Para voltar ao estado inicial, **Reverter** preserva primeiro uma revisão do estado atual e restaura o worktree. A integração da branch da missão ao repositório original fica a cargo de quem usa o Git.
4. Em **Continuar de onde parei**, confira os checkpoints e retome pedidos pendentes. A retomada reabre a sessão ACP salva. `Ctrl+K` inclui o procedimento do `SKILL.md` no próximo pedido.
5. Em **Contas**, inicie o pool e conclua o login oficial na janela do Antigravity. Marque as contas na ordem desejada. Uma conta fixa a sessão. Duas ou mais habilitam a rotação no serviço do pool quando o transporte do CLI usa esse serviço. Sem seleção, o cliente ACP usa o login atual do `agy`.

O Orbita guarda arquivos de contexto, eventos, checkpoints, metadados e worktrees em `orbita` abaixo de `%LOCALAPPDATA%\CentralProducao\dados` ou de `CENTRAL_DATA`. A escolha de uma pasta existente preserva o original. Com `CENTRAL_DATA` apontando para uma Central IA 2.x, o explorador também encontra projetos no formato `projetos/<id>/projeto.json`. O histórico do Orbita fica em seu próprio diretório.

## Estrutura .NET 10

| Projeto | Responsabilidade |
| --- | --- |
| `Orbita.Core` | Tipos do domínio, persistência local e contratos `IAgentEngine`, `IQuotaPolicy`, `IGitSnapshotService` e `ICheckpointStore` |
| `Orbita.Engine` | Cliente JSON-RPC do `agy acp`, conexão e retomada de sessão, normalização de eventos, Git worktrees e snapshots |
| `Orbita.Pool` | Política de disponibilidade por família e janela, inicialização e encerramento do processo de Account Pool, cotas e reserva local de sessão |
| `Orbita.Studio` | ASP.NET Core em loopback, API HTTP, SSE e interface web local |
| `Orbita.Tests` | Ensaios do armazenamento, ACP com um processo controlado, Git real e limites de cota |

O `MissionRunner` reserva a execução, registra o pedido e cria o snapshot. `IAgentEngine` transmite `sessao`, `mensagem`, `plano`, `ferramenta` e `permissao`. O orquestrador grava esses eventos e acrescenta os de `git`, `estado` e `usuario`. A UI lê `GET /api/missions/{id}/events?after={sequencia}` com token local no cabeçalho. Os eventos numerados permitem reconectar o navegador e reconstruir chat e evidências. A execução pertence ao processo do Studio, então recarregar ou fechar a aba não cancela o trabalho. O encerramento do Studio cancela a sessão e tenta salvar o estado antes de liberar o pool.

O processo do pool reaproveita o Account Pool da Central. A configuração de rede usada para uma sessão fica no ambiente daquele processo `agy`. A recuperação de credenciais de um login interrompido continua sob responsabilidade do worker original. `IQuotaPolicy` calcula disponibilidade para o painel, e o worker aplica a margem e a regra de troca no tráfego da sessão.

## Prioridade e verificação

| Ordem | Percurso | Como verificar |
| --- | --- | --- |
| 1 | Projeto, worktree, conversa e execução no chat | Criar missão, editar `SPEC.md`, conversar, enviar `Pode fazer` e checar plano, saída, arquivos e teste. Recarregar a UI e conferir a mesma missão. |
| 2 | Persistência, retomada e reversão | Forçar interrupção, verificar checkpoint e pedido pendente, retomar pelo histórico e checar o diff. Aceitar uma revisão, reverter e confirmar que o Git mantém a revisão anterior. |
| 3 | Antigravity real com Neon, Opus e Account Pool | Conferir catálogo numa conta elegível, testar cada modelo e confirmar quais conexões usam o pool. Testar separadamente mudança de modelo e mudança de conta. O cliente não anuncia suporte antes de o servidor ACP expor o modelo. |
| 4 | Ferramentas subordinadas e mídia | Conectar um servidor MCP por vez. Validar chamadas, evidências e escopo de escrita no worktree. Para FFmpeg, verificar as duas ferramentas e depois exportar um episódio de ensaio. |

O esqueleto entrega o percurso de programação, incluindo chat ACP, leitura do painel do pool, cotas e ações de Git. A integração por ACP depende do catálogo real do cliente. O Google restringe modelos de terceiros, inclusive Opus, a editores específicos, e a rotação automática de contas pessoais fica fora do suporte dos planos de consumo. O ensaio automático usa um processo ACP simulado para testar o produto sem gastar créditos. Operação real com duas contas, Opus nesse cliente, Hermes MCP, Browser Worker, a execução do Estúdio de Vídeo e as rotas de Pesquisa e Áudio ainda precisam de validação ou implementação. [Catálogo ACP](https://antigravity.google/docs/ide/extensions/acp/#models-and-thinking-level) · [FAQ do Antigravity](https://antigravity.google/docs/faq#can-i-use-antigravity-cli-headless-mode-in-third-party-coding-agents-or-automate-account-switching)

## Compilar e testar

Execute os comandos a partir da raiz do repositório. O teste pelo navegador requer Python, Playwright CLI, Edge, Node.js e Git.

```powershell
dotnet build src/Orbita.Studio/Orbita.Studio.csproj -c Release
dotnet run --project tests/Orbita.Tests/Orbita.Tests.csproj -c Release
node src/CentralIA/AntigravityPool/quota-policy.test.mjs
dotnet publish src/Orbita.Studio/Orbita.Studio.csproj -c Release -r win-x64 --self-contained -o entrega/OrbitaStudio/app
python -X utf8 scripts/verificar-orbita.py --app entrega/OrbitaStudio/app/Orbita.Studio.exe --fixture tests/Orbita.Tests/bin/Release/net10.0/Orbita.Tests.exe --evidence entrega/OrbitaStudio
```
