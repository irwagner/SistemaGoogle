# Central IA 2.2.0 para Windows

Extraia o ZIP e execute **Abrir-CentralIA.cmd**. O pacote inclui o runtime .NET para Windows x64. Feche instâncias anteriores para usar a porta e a pasta de dados de costume. O build usa o mesmo formato dos projetos da versão 2.1.0. Se você iniciou a versão anterior com uma pasta de dados personalizada, use o mesmo valor de **CENTRAL_DATA** ao abrir esta versão.

## Programação com revisão

1. Em **Contas e modelos**, configure as APIs que você quer usar. **Claude API** recebe uma chave do Claude Console. **Google Gemini** recebe uma chave do Google AI Studio. Os campos para chaves ficam na Central local e usam o cofre do Windows.
2. Em **Programação → Planejar, revisar e executar**, escolha os pares conexão e modelo na ordem desejada. Selecione o projeto, autorize o uso das conexões escolhidas e envie o pedido. O planejamento tem acesso somente a ferramentas de leitura.
3. Use **Perguntar** para tirar uma dúvida sem alterar o plano. Envie uma instrução para atualizar a proposta. Clique em **Aprovar e executar** quando o plano estiver certo. Arquivos modificados, comandos, resultados e mudanças de modelo ficam na conversa.

A Central mantém o modelo ativo até receber uma resposta de cota HTTP 429 antes do início da saída. Ela consulta a próxima opção selecionada e preserva o histórico, o plano aprovado e os recibos das ferramentas. Quando todas estiverem em pausa, guarda os horários e verifica novamente. Um erro de autenticação ou uma resposta parcial pausa a execução para revisão. Uma ação de efeito incerto precisa de conferência antes da retomada. Gateways configurados pelo operador continuam responsáveis pelo roteamento interno e pelo faturamento do próprio serviço.

## Google pessoal

Em **Programação → Google pessoal · assistido**, **Adicionar conta Google** cadastra nome e e-mail. **Abrir cliente oficial** leva ao Antigravity para autenticar. Use **Baixar contexto** e abra a mesma pasta no Antigravity. **Registrar progresso** preserva a próxima etapa para o perfil seguinte. O modo assistido usa o cliente oficial e a troca de conta é feita pelo titular. [Login oficial](https://antigravity.google/docs/install) · [FAQ sobre a rotação de contas](https://antigravity.google/docs/faq)

## Referências

- O Hermes mantém conversa e resultados de ferramentas na mudança de provedor. A Central adotou uma fila por trabalho e guardou localmente o plano e as pausas. [Código e documentação do Hermes](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/fallback-providers.md)
- O Kiro documenta conversa, plano, revisão e histórico como partes do trabalho no editor. A Central usa os mesmos conceitos com sua interface e dados próprios. [Chat no Kiro](https://kiro.dev/docs/ide/chat/)
- O Antigravity Account Pool documenta a importância de reconhecer o limite antes da primeira saída no streaming. A Central aplica esse critério às APIs escolhidas e mantém os logins pessoais em modo assistido. [Código do Account Pool](https://github.com/jayaliyev/antigravity-account-pool/blob/main/server.mjs)
