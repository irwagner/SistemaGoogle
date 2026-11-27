# Orbita Studio 0.3.8

Clique em **Anexar** na conversa, arraste os arquivos até a caixa de mensagem ou cole uma captura de tela com `Ctrl+V`. Você pode remover arquivos antes de enviar. Use **Enviar** para pedir uma análise ou **Executar** para pedir que o Antigravity use os anexos no trabalho.

- **Imagens** em PNG, JPEG, WebP e GIF vão ao Antigravity como conteúdo visual, conforme as capacidades do CLI.
- **Documentos** em PDF com texto selecionável, Word (DOCX), Excel (XLSX), CSV e arquivos de texto ou código são lidos como texto. Para PDF digitalizado, anexe imagens das páginas. Para documentos longos, o Orbita sinaliza quando o texto enviado é limitado.
- São até 6 arquivos por mensagem, com 5 MB por arquivo e 10 MB no total. Os originais ficam no histórico da missão e podem ser abertos ou baixados pelo nome do anexo.

O pacote Windows inclui [PdfPig 0.1.16](https://www.nuget.org/packages/PdfPig/), licenciado sob Apache 2.0, para extrair o texto de PDFs localmente.
