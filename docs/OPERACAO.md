# Operação Windows

O [guia da versão 0.2.0](VERSAO_0.2.0.md) descreve a navegação por trabalho e as autorizações por produto.

## Iniciar e retomar

Extraia o ZIP local em Windows x64 e verifique o SHA-256 publicado com Get-FileHash. Use Abrir-Central.cmd ou Instalar.ps1. Execute em conta Windows de usuário comum. A Central escuta uma porta dinâmica em 127.0.0.1 e abre a sessão do launcher no navegador padrão. Feche a janela do launcher para encerrar o serviço. Depois reabra pelo mesmo launcher e consulte a tarefa, o último checkpoint e o hash do arquivo no projeto.

Em Tarefas, o operador pode pausar, retomar e registrar revisão. Em Arquivos, cada importação registra origem, permissão e hash. Em Estúdio, importe a mídia, associe cada arquivo a um plano, selecione os takes, vincule as faixas e congele a timeline. Uma correção de take gera outra timeline. Um render interrompido volta à mesma edição e ao mesmo conjunto de arquivos. Um master validado entra em revisão visual e sonora. Informe os direitos e dados de entrega para exportar o ZIP.

Use Diagnóstico para conferir runtime, SQLite, ferramentas, espaço e estados de acesso. Os conectores externos aparecem com a etapa que ainda precisa de prova. Git é necessário para o fluxo de código. FFmpeg e ffprobe são necessários para renderizar e validar.

## Atualizar e fazer rollback

1. Encerre a Central, faça backup e verifique os hashes do novo pacote. Instale em pasta de programa, mantendo os dados em outro diretório.
2. Instalar.ps1 valida cada arquivo da versão, instala lado a lado e alterna atual.txt. Abra a nova versão e confira Diagnóstico, importação e uma edição existente.
3. Para voltar, encerre a Central e execute Instalar.ps1 -Rollback a partir da pasta extraída. A rotina verifica o manifesto da versão anterior. A 0.1.0 usa o schema 4. Para reabri-la após a migração da 0.2.0, restaure o snapshot anterior em outra pasta e inicie a versão anterior com CENTRAL_DATA configurada para essa pasta. Guarde o banco novo para preservar os trabalhos posteriores.
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

Para cópia cifrada portátil, use **Central.exe backup-cifrado CAMINHO**. Para recuperar, aponte CENTRAL_DATA para uma pasta nova e use **Central.exe restaurar-cifrado CAMINHO**. A senha é pedida sem eco. Guarde-a separadamente. A restauração autentica os trechos e verifica o manifesto. Reserve espaço temporário para a cópia autenticada. Tokens e sessões ficam no cofre DPAPI fora da pasta de domínio.

Um ID de fila local serve para localizar a intenção. Para comprovar geração ou upload, confronte conta, conexão e identificador remoto. Sem handle, compare evidência do fornecedor e deixe a reserva em aberto quando o resultado for inconclusivo. O domínio aceita reenvio da mesma intenção após prova de não execução. Cancelamento depois do envio conserva a conciliação e a reserva de cobrança.

## Ensaios externos

| Capacidade | Passo que depende do titular | Evidência a guardar |
|---|---|---|
| Antigravity | Autorizar uma conta elegível e um briefing de teste. No cliente oficial, confirmar perfil, modo de cobrança, catálogo e escopo. | Usar o pacote em worktree pelo operador. Guardar versão, diff, build e recibo. Em plano consumidor, a FAQ limita o modo headless a scripts diretos e CI e exclui wrappers de terceiros e rotação automática. |
| Isolamento | Autorizar perfis das superfícies que permitem integração com terceiros. | Provar sucesso com A e B. Ensaiar A indisponível com B padrão, esperando zero chamadas a B. |
| Notebook pessoal | Indicar Google Cloud project autorizado, conta pessoal e consentimento OAuth para a finalidade. Abrir Gemini Notebook uma vez e habilitar a API no projeto indicado. | Ler primeiro com o escopo mínimo. No ensaio de ingestão em REST v1, fixar usuário e projeto, registrar intenção antes do POST, guardar nomes de caderno e fonte e inspecionar até ACTIVE ou FAILED. |
| Flow | Autorizar perfil, entradas e teto em créditos. Abrir a superfície oficial, conferir a tabela vigente, número de saídas e efeito do excedente. | Operador gera e exporta o arquivo pelo produto, registra custo e dados observáveis no recibo e vincula o arquivo ao plano da Central. |
| Voz, transcrição e música | Autorizar uso e consentimento por tipo de ativo. | Fichas de prova independentes, com custo, falha negativa e aceite sonoro. |
| Canal | Autorizar explicitamente canal, vídeo e ensaio privado. | Guardar intenção e sessão de upload protegida. Reconsultar o mesmo item após interrupção e guardar seu ID. |

Em reautenticação do Antigravity, confirme a conta original no cliente oficial. Para Notebook, a Central usa OAuth desktop, efeito enfileirado e projeto Cloud fixado. O ensaio com conta do titular deve comprovar as permissões e a chegada da fonte a ACTIVE.

[Login Antigravity](https://antigravity.google/docs/install/) · [Modo de script](https://antigravity.google/docs/cli/headless/) · [Uso](https://antigravity.google/docs/cli/commands/usage/) · [Termos](https://antigravity.google/terms) · [Autenticação Notebook](https://developers.google.com/notebook/docs/authentication) · [Cotas Notebook](https://developers.google.com/notebook/docs/rate-limits) · [Créditos Flow](https://support.google.com/flow/answer/16526234?hl=pt-BR) · [Upload privado retomável do YouTube](https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol)
