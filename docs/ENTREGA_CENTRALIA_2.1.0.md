# Central IA 2.1.0 para Windows

Extraia o ZIP inteiro e execute **Abrir-CentralIA.cmd**. O pacote Windows x64 inclui o runtime .NET. A Central abre no navegador local. Se a Central anterior ainda estiver aberta, feche-a e execute o launcher novamente. A reformulação usa por padrão a pasta de dados existente em %LOCALAPPDATA%\CentralIA. A montagem usa o FFmpeg instalado no Windows.

## Programação com uma conta Google pessoal

1. Abra **Programação**, selecione **Google pessoal · assistido** em **Execução** e clique em **Adicionar conta Google**. Cadastre nome e e-mail de A e B. Faça login no aplicativo oficial do Antigravity e confira o e-mail ativo por lá.
2. Selecione A, escolha o projeto, escreva o pedido e clique em **Criar sistema**. Use **Baixar contexto** para levar o pedido, o caminho da pasta, a revisão e o histórico ao cliente oficial.
3. Quando for transferir o trabalho, salve as alterações de A, clique em **Registrar progresso** e informe decisões, testes e o próximo passo. Escolha B como perfil de continuação. No cliente oficial, entre com B e abra a mesma pasta. Use o contexto atualizado para continuar.

O histórico registra a identidade informada no modo assistido. A troca automática com duas contas pessoais ainda está pendente. A FAQ do Antigravity informa que os planos de consumidor não dão suporte à rotação automática de contas. O ACP documenta o reaproveitamento do login em cache. [FAQ do Antigravity](https://antigravity.google/docs/faq#can-i-use-antigravity-cli-headless-mode-in-third-party-coding-agents-or-automate-account-switching) · [Autenticação do ACP](https://antigravity.google/docs/ide/extensions/acp/#authentication)

## Ensaio local

O ensaio usa duas identidades distintas de um simulador HTTP no próprio computador. Somente a pasta de teste marcada e a conexão de loopback habilitam esse modo. O teste usa Node.js, Edge via Playwright CLI, FFmpeg e FFprobe. O ensaio fez zero chamadas a serviços de geração do Google e consumiu zero créditos.

| Cenário | Resultado verificado |
| --- | --- |
| Um pedido, um trabalho | A criou a função soma e passou o primeiro teste. B leu os mesmos arquivos, importou a função numa extensão e passou os dois testes. O arquivo inicial do titular manteve o mesmo hash. |
| Cotas e reinício | A recebeu limite semanal e B recebeu uma pausa global, ambos injetados. A Central reiniciou durante a pausa e somente B voltou a executar. |
| Conta fixada | A ficou indisponível. Nenhum pedido de execução chegou a B. |
| Timeout após gravar | A Central manteve a tentativa pendente, mostrou a diferença em disco e bloqueou nova execução automática. |
| Vídeo | Uma consulta ambígua preservou a operação original. Sem a chave de origem, houve zero consultas com outra chave. Um envio ambíguo sem recibo gerou somente um POST. |
| Episódio | Duas conexões simuladas produziram cenas independentes. Apenas a segunda cena foi refeita. A exportação gerou MP4 H.264 com áudio AAC. |
| Windows | O launcher identificou a versão anterior na porta e devolveu instrução de recuperação. A versão nova leu o projeto e a conversa gravados pela anterior em um ambiente de teste. |

No repositório, os recibos, as capturas de tela e o MP4 ficam em `entrega/Rotacao-CentralIA`. O ensaio pode ser repetido com `scripts/verificar-rotacao-centralia.py`, passando em `--package` a pasta extraída, em `--evidence` a pasta de evidências e em `--legacy` o executável de teste da reformulação v2.0.0.
