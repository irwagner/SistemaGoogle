# Evidências e reprodução

## Pilotos locais

- **Oficina Horizonte.** Codex implementou e revisou uma aplicação de orçamento de um item. Criou duas worktrees e integrou implementação e aceite em main. Um conflito Git foi detectado e abandonado em worktree separada. O piloto pausou na Central depois de baixar o pacote com SHA da base. Após reiniciar, o hash foi reconferido, o trabalho retomou e o mesmo orçamento de R$ 143,70 foi verificado no aplicativo integrado e no binário publicado. Quantidade zero e reenvio divergente foram rejeitados. O Git bundle verifica a cadeia e permite clonar a branch entregue.
- **O Último Sinal.** Codex criou a animação original e duas faixas sintetizadas localmente, registrou bíblia e roteiro, importou arquivos e fez a revisão editorial do piloto. O primeiro master foi reprovado por continuidade. O processo da Central e o FFmpeg foram interrompidos durante o segundo render. Depois da reabertura, a mesma edição retomou e perdeu somente o render parcial. O master final preservou os planos 1 e 3 e substituiu o take do plano 2. Há 576 quadros, 24 fps, 1280 × 720, H.264 e AAC. O FFmpeg decodificou a saída inteira.
- **Pesquisa.** O relatório local vincula três afirmações a fontes primárias revisadas. A fixture HTTP de Notebook verifica a leitura de uma fonte PROCESSING até FAILED. A prova do serviço com OAuth e fonte ACTIVE depende do titular.
- **Restauração e isolamento.** O snapshot foi restaurado em diretório novo. A Central recuperou briefing e edição aprovada. Origem HTTP indevida, formulário sem antifalsificação, arquivo vazio e acesso cruzado foram rejeitados. O canário privado ficou fora do ZIP editorial e dos logs. A restauração de snapshot anterior ao envio bloqueou novas reservas remotas no teste de domínio.

Codex registrou o aceite técnico com dados fictícios. No percurso assistido do Antigravity ou Flow, o operador confirma conta e gasto, executa pela superfície oficial e importa recibo e resultado. A revisão humana comercial e a comparação com a linha de base seguem abertas.

## Medidas

Os tempos de teste e render constam em resultado.json. Houve uma entrega de software e uma editorial aceitas no piloto, com um take substituído. O gasto incremental remoto foi R$ 0. Receita, horas humanas, infraestrutura, rateio, revisão humana e custo pleno estão em aberto. Use a ficha de custos para converter medições de produção em custo por aceite.

## Reproduzir em Windows x64

1. Execute dotnet run --project tests/Central.Tests -c Release com o SDK 10.0.404. O relógio do broker é controlado.
2. Com Python 3.14, Pillow 12.3.0, Git e FFmpeg/ffprobe no PATH, execute python scripts/criar-midia.py .verificacao/midia.
3. Use scripts/Empacotar.ps1 para produzir os executáveis. Execute python scripts/verificar-pilotos.py --help e aponte para os dois EXEs, o SDK, a mídia e uma pasta nova. O ensaio usa formulários reais em 127.0.0.1 e interrompe os processos e o render local.
4. Confira resultado.json, PDF, laudo ffprobe e bundle. No ZIP editorial, manifesto.json registra o SHA de cada conteúdo. Cada pacote Windows acompanha o SHA-256.
5. Use [Operação Windows](OPERACAO.md) para diagnóstico, backup, restauração e rollback. O script scripts/Verificar-Instalacao.ps1 aceita dois ZIPs com versões diferentes, verifica seus hashes e exercita a troca de versão em uma pasta com espaços e acentos.

## Pendências de validação

Antigravity, Flow, Notebook, canais e produtos de áudio ainda precisam dos ensaios autorizados. Os efeitos remotos do broker foram ensaiados com dados e erros injetados em processo local. Windows limpo, disco cheio, processos executores isolados, cifragem de snapshot e OAuth/cofre integrados ficam em aberto. O episódio usa preset local 720p/24 e som sem voz.
