# Orbita Studio 0.3.1

Extraia o pacote Windows x64 e abra `Orbita.Desktop.exe` ou `Abrir-OrbitaStudio.cmd`. O cliente usa WebView2 para mostrar o mesmo workspace do navegador em uma janela própria. O serviço local inicia junto e mantém a missão em andamento quando você fecha a janela. Abra o desktop novamente para acompanhar. O endereço padrão do workspace no navegador é `http://127.0.0.1:7784/`.

Se a porta estiver ocupada pela 0.3.0, aguarde a missão ativa terminar e encerre o serviço anterior antes de iniciar a 0.3.1 com a mesma pasta de dados. Os projetos e checkpoints usam o mesmo formato e `CENTRAL_DATA` continua funcionando. Para testes em paralelo, ajuste `ORBITA_PORT` e `CENTRAL_DATA` antes de iniciar o desktop.

## Modos por missão

- **Autônomo** é o padrão. O Orbita escolhe a execução automática do Antigravity quando o servidor ACP oferece esse modo. As ações comuns da missão seguem sem confirmar cada ferramenta. Se uma ação for recusada, o agente pode seguir por uma alternativa. Se faltar algo que dependa de você, a missão mostra **Precisa de você** e registra a pendência. Responda pelo chat para retomar. A disponibilidade do modo automático do CLI depende da conta e do suporte ao sandbox no Windows. [Modos do Antigravity](https://antigravity.google/docs/cli/modes)
- **Permissão total** inicia um turno de execução com a opção oficial de autoaprovação do Antigravity. Ela permite usar arquivos e comandos acessíveis à conta do Windows, inclusive fora do worktree. O modo é salvo na missão quando você envia o turno. Você pode voltar a **Autônomo** no próximo pedido. Enviar uma mensagem apenas para conversar continua usando o modo de planejamento quando ele está disponível. [Opções ACP](https://antigravity.google/docs/ide/extensions/acp/#flags)
- **Foco no chat** esconde o console para dar espaço à conversa. O botão e `Ctrl+J` alternam a visão. Plano, diffs e evidências continuam acessíveis pelas abas. `Ctrl+K` liga o `SKILL.md` para o próximo pedido.

## Testes

```powershell
dotnet run --project tests/Orbita.Tests/Orbita.Tests.csproj -c Release
dotnet build src/Orbita.Desktop/Orbita.Desktop.csproj -c Release
dotnet publish src/Orbita.Studio/Orbita.Studio.csproj -c Release -r win-x64 --self-contained -o entrega/OrbitaStudio-0.3.1/studio
python -X utf8 scripts/verificar-orbita.py --app entrega/OrbitaStudio-0.3.1/studio/Orbita.Studio.exe --fixture tests/Orbita.Tests/bin/Release/net10.0/Orbita.Tests.exe --evidence entrega/OrbitaStudio-0.3.1
```

O ensaio ACP comprova a política enviada a cada turno, a continuação depois de uma ferramenta recusada e a retomada após **Precisa de você**. Usa uma simulação local, Git real e um teste Node real. A janela desktop requer o Microsoft Edge WebView2 Runtime. [WebView2 para Windows](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
