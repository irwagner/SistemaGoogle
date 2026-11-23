# Dependências e compatibilidade

Data de referência 23/11/2026. Os arquivos release.json e os arquivos packages.lock.json registram hashes do conteúdo instalado e dos pacotes NuGet.

| Componente | Versão usada | Licença ou termos | Onde está |
|---|---|---|---|
| SDK .NET | 10.0.404, win-x64, SHA-512 oficial 330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee | MIT e avisos de terceiros | Máquina de compilação |
| .NET e ASP.NET Core | 10.0.15, win-x64 | MIT e avisos de terceiros | Dentro dos dois pacotes |
| Microsoft.Data.Sqlite | 10.0.15 | MIT | Dentro dos dois pacotes |
| SQLite nativo | 3.53.3, medido pela versão publicada | Domínio público | Dentro dos dois pacotes |
| SQLitePCLRaw | 2.1.13 | Apache 2.0 para o wrapper e domínio público para SQLite | Dentro dos dois pacotes |
| Git for Windows | 2.55.0.windows.3 | GPLv2, com componentes de licenças próprias | PATH ou CENTRAL_GIT |
| FFmpeg e ffprobe | 9.0.2-full_build-www.gyan.dev | O build ensaiado usa configuração GPL com libx264 | PATH ou CENTRAL_FFMPEG e CENTRAL_FFPROBE |
| Python e Pillow | Python 3.14 e Pillow 12.3.0 | PSF e HPND | Somente reprodução dos ensaios e da mídia original |

Hashes das ferramentas externas efetivamente usadas, em SHA-256.

| Executável | Hash |
|---|---|
| git.exe | 7b7971dd13f0c3a284e538601f2f9770b3a87dfac772fb52d68141c67ed22364 |
| ffmpeg.exe | 589e50b766d251afdf181dd664d40bd94407e200b019989fd7468c7d118a28d0 |
| ffprobe.exe | 02264dfa4f56baaa31e443ece8aa16d397f6b008200e7fd184d22b9009e75fd3 |

Fontes oficiais. [SDK e runtime .NET](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [Licença .NET](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) · [Licença ASP.NET Core](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt) · [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.15) · [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) · [SQLite](https://sqlite.org/copyright.html) · [Git](https://git-scm.com/about/free-and-open-source) · [FFmpeg](https://ffmpeg.org/legal.html)

Git, FFmpeg e ffprobe são dependências externas do Windows. A distribuição local inclui código próprio, binários .NET e SQLite e os respectivos avisos do pacote. SQLitePCLRaw Copyright 2014–2024 SourceGear, LLC.
