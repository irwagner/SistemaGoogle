# Operação Windows

## Iniciar e retomar

Extraia o ZIP local em Windows x64 e verifique o SHA-256 publicado com Get-FileHash. Use Abrir-Central.cmd ou Instalar.ps1. Execute em conta Windows de usuário comum. A Central escuta uma porta dinâmica em 127.0.0.1 e abre a sessão do launcher no navegador padrão. Feche a janela do launcher para encerrar o serviço. Depois reabra pelo mesmo launcher e consulte a tarefa, o último checkpoint e o hash do arquivo no projeto.

Em Tarefas, o operador pode pausar, retomar e registrar revisão. Em Arquivos, cada importação registra origem, permissão e hash. Em Estúdio, importe a mídia, associe cada arquivo a um plano, selecione os takes, vincule as faixas e congele a timeline. Uma correção de take gera outra timeline. Um render interrompido volta à mesma edição e ao mesmo conjunto de arquivos. Um master validado entra em revisão visual e sonora. Informe os direitos e dados de entrega para exportar o ZIP.

Use Diagnóstico para conferir runtime, SQLite, ferramentas, espaço e estados de acesso. Os conectores externos aparecem com a etapa que ainda precisa de prova. Git é necessário para o fluxo de código. FFmpeg e ffprobe são necessários para renderizar e validar.

## Atualizar e fazer rollback

1. Encerre a Central, faça backup e verifique os hashes do novo pacote. Instale em pasta de programa, mantendo os dados em outro diretório.
2. Instalar.ps1 valida cada arquivo da versão, instala lado a lado e alterna atual.txt. Abra a nova versão e confira Diagnóstico, importação e uma edição existente.
3. Para voltar, encerre a Central e execute Instalar.ps1 -Rollback a partir da pasta extraída. A rotina verifica o manifesto da versão anterior. Se houver migration incompatível, restaure em pasta nova o snapshot de dados compatível e configure CENTRAL_DATA para usá-la.
4. Mantenha os dados e snapshots em local protegido. Para remover somente a aplicação, remova a pasta de programa após encerrar o serviço. A pasta de dados é independente.

## Backup, restauração e efeitos em aberto

Encerre o launcher. A partir da pasta da versão ativa, execute os comandos com caminhos absolutos. Escolha volume local cifrado para dados de cliente.

```powershell
$env:CENTRAL_DATA = 'D:\Central\dados'
& '.\app\Central.exe' backup 'D:\CentralBackups\snapshot-2026-11-23'
$env:CENTRAL_DATA = 'D:\Central\restauracao'
& '.\app\Central.exe' restore 'D:\CentralBackups\snapshot-2026-11-23'
& '.\Abrir-Central.cmd'
```

O snapshot usa a API SQLite para banco e lista de blobs. O comando valida integridade, referências e hashes. As cópias têm o conteúdo integral dos projetos e precisam da mesma proteção dos dados originais. O código pode ser restaurado a partir de um Git bundle importado como artefato. Se um trabalho tiver ocorrido após o snapshot, consulte as evidências da instalação anterior e o histórico do provedor com a conexão original. Registre os recibos e identifique as intenções que já tiveram efeito. A restauração sinaliza essa conciliação e conserva envios em estado ambíguo.

Um ID de fila local serve para localizar a intenção. Para comprovar geração ou upload, confronte conta, conexão e identificador remoto. Sem handle, compare evidência do fornecedor e deixe a reserva em aberto quando o resultado for inconclusivo. O domínio aceita reenvio da mesma intenção após prova de não execução. Cancelamento depois do envio conserva a conciliação e a reserva de cobrança.

## Ensaios externos

| Capacidade | Passo que depende do titular | Evidência a guardar |
|---|---|---|
| Antigravity | Autorizar uma conta elegível e um briefing de teste. Abrir o cliente oficial e confirmar perfil, modo de cobrança, catálogo, escopo e excedente desligado no produto. | Usar o pacote em worktree isolada. Em execução oficial de script, conferir o modo documentado, comparar a saída com a identidade da sessão e verificar /usage. Guardar versão, SHA do binário, diff, build, recibo e modelo quando observável. |
| Isolamento | Autorizar duas identidades quando a primeira tiver prova. Preparar cofres independentes, por exemplo dois usuários Windows ou VMs. | Provar sucesso com A e com B e ensaiar A indisponível com B padrão. A operação fixada em A deve permanecer em espera, com zero chamadas para B. |
| Notebook pessoal | Indicar Google Cloud project autorizado, conta pessoal e consentimento OAuth para a finalidade. Abrir Gemini Notebook uma vez e habilitar a API no projeto indicado. | Ler primeiro com o escopo mínimo. No ensaio de ingestão em REST v1, fixar usuário e projeto, registrar intenção antes do POST, guardar nomes de caderno e fonte e inspecionar até ACTIVE ou FAILED. |
| Flow | Autorizar perfil, entradas e teto em créditos. Abrir a superfície oficial, conferir a tabela vigente, número de saídas e efeito do excedente. | Operador gera e exporta o arquivo pelo produto, registra custo e dados observáveis no recibo e vincula o arquivo ao plano da Central. |
| Voz, transcrição e música | Autorizar uso e consentimento por tipo de ativo. | Fichas de prova independentes, com custo, falha negativa e aceite sonoro. |
| Canal | Autorizar explicitamente canal, vídeo e ensaio privado. | Guardar intenção e sessão de upload protegida. Reconsultar o mesmo item após interrupção e guardar seu ID. |

O cliente Antigravity compartilha a sessão entre superfícies da mesma instalação. Em reautenticação, confirme a conta original na interface oficial antes de liberar trabalho. Para OAuth Notebook, use escopos mínimos, navegador do sistema e o Google Cloud project indicado. A prévia é gratuita dentro dos limites documentados, com 100 leituras e 100 escritas por minuto por projeto e 20 criações de fontes por minuto por usuário, na consulta de 23/11/2026. O transporte experimental do código prepara chamadas de caderno, texto e inspeção. A prova com conta, o login integrado e o upload de arquivo ficam em aberto.

[Login Antigravity](https://antigravity.google/docs/install/) · [Modo de script](https://antigravity.google/docs/cli/headless/) · [Uso](https://antigravity.google/docs/cli/commands/usage/) · [Termos](https://antigravity.google/terms) · [Autenticação Notebook](https://developers.google.com/notebook/docs/authentication) · [Cotas Notebook](https://developers.google.com/notebook/docs/rate-limits) · [Créditos Flow](https://support.google.com/flow/answer/16526234?hl=pt-BR) · [Upload privado retomável do YouTube](https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol)
