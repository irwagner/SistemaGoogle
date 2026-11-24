# Central IA 2.3.0 com Antigravity Account Pool

Extraia o pacote Windows e execute **Abrir-CentralIA.cmd**. Em **Contas e modelos**, abra o primeiro painel, **Antigravity Account Pool**. O componente roda no Windows e usa Node.js 20 ou superior, PowerShell 7 e o Antigravity CLI oficial.

1. Clique em **Iniciar pool Google**. Se o CLI tiver um login Google ativo, a Central tenta confirmar a identidade e cadastrar essa conta.
2. Clique em **Entrar com outra conta Google**. Termine o login na janela do Antigravity. O painel mostra o e-mail confirmado assim que concluir o cadastro. Repita para as demais.
3. No mesmo painel, escolha um projeto e as contas na ordem desejada. Use **Fixar uma conta** com um perfil, ou **Alternar ao atingir limite** com dois ou mais. Clique em **Abrir projeto no Antigravity CLI** e envie o pedido na janela que abrir.
4. Ao retomar uma pasta já trabalhada, marque **Continuar a última conversa desta pasta**. A atividade e as cotas de Gemini e Claude/GPT ficam no painel.

O pool usa a sessão e o modelo selecionados no Antigravity CLI. Ao receber um limite individual antes da resposta começar, pode enviar a mesma solicitação pela próxima conta elegível escolhida para aquele projeto. Com todas em pausa, o CLI recebe o limite e você pode retomar a sessão depois. O Flow da Central usa suas próprias conexões de geração de vídeo. [Antigravity CLI oficial](https://antigravity.google/docs/cli/reference/)

O Account Pool é comunitário e o Google informa que os planos pessoais não dão suporte a essa rotação. Um teste controlado real exige as contas autorizadas no CLI e pode usar a cota da conta seguinte. As evidências do pacote usam somente identidades e respostas simuladas. [FAQ do Antigravity](https://antigravity.google/docs/faq) · [Verificação prevista pelo projeto](https://github.com/jayaliyev/antigravity-account-pool#verify-account-switching)

## Origem e integração

O proxy, a política de cotas e a ponte com o cofre do Windows partem do commit abaixo. A Central fornece o painel, restringe a sessão às contas selecionadas, protege a API local e confere o login que iniciou a conexão. A integração abre o CLI com as permissões normais e mantém a configuração do CLI intacta. Os segredos ficam fora dos projetos e dos recibos da Central. [Código de origem](https://github.com/jayaliyev/antigravity-account-pool/tree/1b749f0ba30eba3c849b096cdd63cb054ec08004) · [Licença MIT](https://github.com/jayaliyev/antigravity-account-pool/blob/1b749f0ba30eba3c849b096cdd63cb054ec08004/LICENSE)
