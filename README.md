# Central de Produção com IA

Central local em português para Windows x64. A aplicação organiza projetos, tarefas e decisões no SQLite, guarda arquivos por SHA-256 e permite montar um episódio com mídia importada. O diagnóstico apresenta o estado das integrações externas.

## Abrir no Windows

1. Extraia o ZIP da Central para uma pasta local. Compare seu SHA-256 com o arquivo .sha256 da entrega.
2. Abra Abrir-Central.cmd e mantenha a janela do launcher aberta enquanto usar a Central. O navegador padrão recebe uma sessão local. Para instalar no perfil Windows, execute `powershell -NoProfile -File .\Instalar.ps1` na pasta extraída.
3. Crie um projeto e importe um briefing em **Arquivos**. Em **Tarefas**, registre a próxima ação antes de fechar. Reabra pelo launcher e consulte o histórico.

O programa instalado ocupa `%LOCALAPPDATA%\CentralProducao\programa`. Os projetos ficam em `%LOCALAPPDATA%\CentralProducao\dados`. A variável `CENTRAL_DATA` permite apontar para outro diretório NTFS. Para o Estúdio, instale FFmpeg com libx264 e AAC e ffprobe no PATH ou configure `CENTRAL_FFMPEG` e `CENTRAL_FFPROBE` com caminhos absolutos.

## Compilar e reproduzir

Use o SDK .NET 10.0.404. A versão autocontida inclui o runtime 10.0.15 em win-x64. Rode `powershell -NoProfile -File scripts\Empacotar.ps1 -Destino <pasta-nova>`. Consulte [Evidências](docs/EVIDENCIAS.md), [Marcos](docs/MARCOS.md) e [Operação Windows](docs/OPERACAO.md).
