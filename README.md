# Orbita Studio

**Orbita Studio 0.3.17** reúne a programação com Antigravity CLI e os percursos de Vídeo, Pesquisa e Áudio no cliente Windows. O desktop integra Signal e Discord, pode iniciar com o Windows e continua na bandeja ao fechar a janela. Após preparar o cliente, abra `Abrir-OrbitaStudioNativo.cmd` na raiz do repositório. [Guia do cliente nativo](docs/ORBITA_CLIENTE_HERMES.md)

Para as versões anteriores, veja [criação de missões](docs/ORBITA_STUDIO_0.3.9.md), [anexos no chat](docs/ORBITA_STUDIO_0.3.8.md) e [abertura de aplicativos da missão](docs/ORBITA_STUDIO_0.3.7.md).

## Central de Produção e pacotes anteriores

Central local em português para Windows x64. Na tela inicial escolha Programação, Vídeo e minisséries, Pesquisa, Áudio, Distribuição ou Contas e recursos. Cada trabalho guarda pedido, conversa e evidências no projeto. **Continuar de onde parei** reabre os trabalhos recentes.

## Abrir no Windows

1. Extraia o ZIP da Central para uma pasta local. Compare seu SHA-256 com o arquivo .sha256 da entrega.
2. Abra Abrir-Central.cmd e mantenha a janela do launcher aberta enquanto usar a Central. O navegador padrão recebe uma sessão local. Para instalar no perfil Windows, execute `powershell -NoProfile -File .\Instalar.ps1` na pasta extraída.
3. Em **Programação → Criar sistema**, informe o pedido e revise o plano. Para execução local, selecione um modelo instalado no Ollama. Em **Vídeo e minisséries**, aprove roteiro e planos para abrir a montagem. [Guia da versão 0.2.0](docs/VERSAO_0.2.0.md)

O programa instalado ocupa `%LOCALAPPDATA%\CentralProducao\programa`. Os projetos ficam em `%LOCALAPPDATA%\CentralProducao\dados`. A variável `CENTRAL_DATA` permite apontar para outro diretório NTFS. Para o Estúdio, instale FFmpeg com libx264 e AAC e ffprobe no PATH ou configure `CENTRAL_FFMPEG` e `CENTRAL_FFPROBE` com caminhos absolutos.

## Compilar e reproduzir

Use o SDK .NET 10.0.404. A versão autocontida inclui o runtime 10.0.15 em win-x64. Rode `powershell -NoProfile -File scripts\Empacotar.ps1 -Destino <pasta-nova>`. Consulte [Evidências](docs/EVIDENCIAS.md), [Marcos](docs/MARCOS.md) e [Operação Windows](docs/OPERACAO.md).
