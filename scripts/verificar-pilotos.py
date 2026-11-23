"""Percursos reais pelos formulários Razor e pelo aplicativo de orçamentos."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import html
import http.cookiejar
import io
import json
import os
import re
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
INPUT = ROOT / "pilotos" / "entradas"
RIGHTS = "Produção original deste piloto. Uso autorizado na demonstração local."
CANARY = b"CANARIO-privado-baaf21ee"


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def git(folder, *args, expected=0):
    command = ["git", "-c", "core.autocrlf=false", "-c", "user.name=Codex Piloto",
               "-c", "user.email=codex-piloto@example.invalid", "-c", "commit.gpgSign=false", *args]
    result = subprocess.run(command, cwd=folder, capture_output=True, encoding="utf-8", errors="replace")
    check(result.returncode == expected, f"Git {args[0]}: {result.returncode} {result.stdout} {result.stderr}")
    return result.stdout.strip()


def origin_port():
    with socket.socket() as handle:
        handle.bind(("127.0.0.1", 0))
        return handle.getsockname()[1]


class LocalApp:
    def __init__(self, executable, scratch, kind, label=None):
        self.executable = executable
        self.scratch = scratch
        self.kind = kind
        self.label = label or kind
        self.process = None
        self.turn = 0

    def start(self, root=None):
        self.turn += 1
        self.port = origin_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        data = root or self.scratch / (self.label + "-dados")
        self.data = data
        bootstrap = self.scratch / f"bootstrap-{self.turn}.txt"
        env = os.environ.copy()
        prefix = "CENTRAL" if self.kind == "central" else "ORCAMENTOS"
        env.update({prefix + "_DATA": str(data), prefix + "_PORT": str(self.port),
                    "Logging__LogLevel__Default": "Warning", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
        if self.kind == "central":
            env["CENTRAL_BOOTSTRAP_FILE"] = str(bootstrap)
        self.env = env
        log = self.scratch / f"{self.label}-{self.turn}.log"
        with log.open("wb") as out:
            self.process = subprocess.Popen([str(self.executable)], cwd=self.executable.parent, env=env, stdout=out, stderr=out)
        for _ in range(140):
            check(self.process.poll() is None, f"Serviço encerrou. Consulte {log}.")
            if self.kind == "central" and bootstrap.exists():
                url = bootstrap.read_text()
                if "#" in url:
                    bootstrap.unlink()
                    self.request("/entrar", b"", {"Origin": self.url, "X-Central-Bootstrap": url.split("#")[1]}, 204)
                    return
            if self.kind == "orcamentos":
                try:
                    self.get("/")
                    return
                except urllib.error.URLError:
                    pass
            time.sleep(.05)
        raise TimeoutError(str(log))

    def stop(self):
        if self.process is not None:
            if self.process.poll() is None:
                self.process.terminate()
                self.process.wait(timeout=8)
            self.process = None

    def request(self, path, body=None, headers=None, expected=200):
        req = urllib.request.Request(self.url + path, body, headers or {})
        try:
            response = self.opener.open(req, timeout=180)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data = response.read()
            if response.status != expected:
                message = re.search(rb'role="alert">([^<]+)', data)
                details = html.unescape(message[1].decode()) if message else data[:180].decode(errors="replace")
                raise AssertionError(f"{self.kind} {path}: HTTP {response.status}, esperado {expected}. {details}")
            return data

    def get(self, path, expected=200):
        return self.request(path, expected=expected)

    def form(self, page, handler="", fields=None, file=None, expected=200):
        content = self.get(page)
        token = attr(content, r'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        payload = {"__RequestVerificationToken": token, **(fields or {})}
        query = page + (("&" if "?" in page else "?") + "handler=" + handler if handler else "")
        headers = {"Origin": self.url}
        if file is None:
            headers["Content-Type"] = "application/x-www-form-urlencoded"
            body = urllib.parse.urlencode(payload, doseq=True).encode()
        else:
            boundary = uuid.uuid4().hex
            lines = []
            for name, value in payload.items():
                lines.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n".encode(),
                              str(value).encode(), b"\r\n"))
            filename, data = file
            lines.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
                          "Content-Type: application/octet-stream\r\n\r\n".encode(), data, f"\r\n--{boundary}--\r\n".encode()))
            headers["Content-Type"] = "multipart/form-data; boundary=" + boundary
            body = b"".join(lines)
        return self.request(query, body, headers, expected)


def attr(data, pattern):
    decoded = data.decode(errors="replace")
    match = re.search(pattern, decoded) or re.search(pattern, html.unescape(decoded))
    check(match, f"Campo ausente: {pattern}")
    return html.unescape(match[1])


def create(app, name, kind):
    result = app.form("/", fields={"name": name, "kind": kind, "classification": "interno", "key": uuid.uuid4().hex})
    return attr(result, r'href="(/projetos/[\da-f]{32}\?sec=trabalho)"').split("?")[0]


def task(app, page, title, predecessors=(), policy="local"):
    response = app.form(page, "Tarefa", {"title": title, "policy": policy, "predecessors": predecessors})
    return attr(response, r'data-task="([\da-f]{32})"[^>]*>\s*<strong>' + re.escape(title))


def move(app, page, task_id, action, note):
    version = attr(app.get(page), rf'data-task="{task_id}" data-version="(\d+)"')
    return app.form(page, "Avancar", {"task": task_id, "version": version, "action": action, "note": note})


def upload(app, page, name, data, origin="Entrada local do piloto", rights=RIGHTS, task_id="", key=None):
    result = app.form(page + "?sec=arquivos", "Importar",
                      {"origin": origin, "rights": rights, "task": task_id, "key": key or uuid.uuid4().hex}, (name, data))
    pattern = r'data-asset="([\da-f]{32})">\s*<a[^>]*>' + re.escape(name)
    asset = attr(result, pattern)
    check(digest(data).encode() in result, "Hash ausente no HTML.")
    return asset


def download(app, project, asset):
    return app.get(project + "?handler=Arquivo&asset=" + asset)


def quote_roundtrip(quotes, out):
    start = time.monotonic()
    quotes.start()
    key = uuid.uuid4().hex
    fields = {"cliente": "Maria Exemplo", "item": "Instalação de suporte", "quantidade": "3", "unitario": "47,90", "key": key}
    bad = quotes.form("/", fields={**fields, "quantidade": "0"}, expected=422)
    check(b"Orcamento #1" not in bad and b"Nenhum" in bad, "A entrada inválida criou um orçamento.")
    page = quotes.form("/", fields=fields)
    check("R$ 143,70" in html.unescape(page.decode()).replace("\xa0", " "), "Total na página.")
    quotes.form("/", fields=fields)
    quotes.form("/", fields={**fields, "unitario": "47,91"}, expected=422)
    quotes.stop()
    quotes.start()
    page = quotes.get("/")
    check(page.count(b"/orcamento/1") == 1 and b"Maria Exemplo" in page and b"/orcamento/2" not in page, "Persistência do orçamento.")
    pdf = quotes.get("/orcamento/1/pdf")
    check(pdf.startswith(b"%PDF-") and b"143,70" in pdf and "Instalação de suporte".encode("latin1") in pdf, "PDF divergente.")
    (out / "orcamento-1.pdf").write_bytes(pdf)
    quote_seconds = round(time.monotonic() - start, 3)
    return quote_seconds, digest(pdf)


def worktrees_prepare(scratch):
    repo = scratch / "oficina-repo"
    repo.mkdir()
    git(repo, "init", "-b", "main")
    shutil.copyfile(INPUT / "briefing-orcamentos.txt", repo / "briefing.txt")
    (repo / ".gitattributes").write_text("* text=auto\n", encoding="utf-8")
    (repo / ".gitignore").write_text("bin/\nobj/\n", encoding="utf-8")
    git(repo, "add", ".")
    git(repo, "commit", "-m", "Registrar briefing e critérios do orçamento")
    base = git(repo, "rev-parse", "HEAD")
    implementation = scratch / "worktree-implementacao"
    review = scratch / "worktree-aceite"
    git(repo, "worktree", "add", "-b", "tarefa/implementacao", str(implementation), base)
    git(repo, "worktree", "add", "-b", "tarefa/aceite", str(review), base)
    return repo, implementation, review, base


def worktrees_finish(repo, impl, review, base, out, dotnet):
    for name in ("Orcamentos.csproj", "Program.cs", "Quotes.cs", "Pdf.cs", "packages.lock.json"):
        shutil.copyfile(ROOT / "pilotos" / "Orcamentos" / name, impl / name)
    for name in ("Directory.Build.props", "global.json"):
        shutil.copyfile(ROOT / name, impl / name)
    git(impl, "add", ".")
    git(impl, "diff", "--cached", "--check")
    git(impl, "commit", "-m", "Entregar orçamento persistente e exportação PDF")
    (review / "ACEITE.md").write_text(
        "Criar o orçamento de Maria Exemplo, reiniciar e abrir o PDF.\n"
        "Aceitar se a interface e o PDF exibirem R$ 143,70. Quantidade zero e reenvio divergente devem falhar.\n",
        encoding="utf-8")
    git(review, "add", "ACEITE.md")
    git(review, "commit", "-m", "Registrar revisão e casos de aceite")
    git(repo, "merge", "--no-ff", "tarefa/implementacao", "-m", "Integrar implementação revisada")
    git(repo, "merge", "--no-ff", "tarefa/aceite", "-m", "Integrar critérios de revisão")
    head = git(repo, "rev-parse", "HEAD")
    changes = git(repo, "diff", "--check", base, head)
    check(changes == "", "O diff integrado tem erros.")
    diff = git(repo, "diff", base, head)
    (out / "orcamentos-revisado.patch").write_text(diff + "\n", encoding="utf-8")
    (out / "git-integrado.txt").write_text(
        git(repo, "log", "--oneline", "--graph", "main") + "\n\n" + git(repo, "diff", "--stat", base, head) + "\n",
        encoding="utf-8")
    git(repo, "bundle", "create", str(out / "orcamentos.bundle"), "main")
    git(repo, "bundle", "verify", str(out / "orcamentos.bundle"))
    subprocess.run([str(dotnet), "restore", "Orcamentos.csproj", "--locked-mode"], cwd=repo, check=True)
    build = subprocess.run([str(dotnet), "build", "Orcamentos.csproj", "-c", "Release", "--no-restore"], cwd=repo,
                           check=True, capture_output=True)
    (out / "build-orcamentos.txt").write_bytes(build.stdout)
    # Exercise the reviewed integrated branch with a separate process and store.
    integrated = repo / "bin" / "Release" / "net10.0" / "Orcamentos.exe"

    branch = repo.parent / "worktree-conflito"
    git(repo, "worktree", "add", "-b", "teste/conflito", str(branch), head)
    (branch / "briefing.txt").write_text("Revisão incompatível na worktree auxiliar.\n", encoding="utf-8")
    git(branch, "commit", "-am", "Alterar briefing na tarefa auxiliar")
    (review / "briefing.txt").write_text("Revisão incompatível na worktree de ensaio.\n", encoding="utf-8")
    git(review, "commit", "-am", "Alterar briefing no outro ensaio")
    git(branch, "merge", "tarefa/aceite", expected=1)
    check(git(branch, "diff", "--name-only", "--diff-filter=U") == "briefing.txt", "Conflito esperado.")
    git(branch, "merge", "--abort")
    check(git(repo, "rev-parse", "HEAD") == head and git(repo, "status", "--porcelain") == "", "Branch integrada alterada pelo conflito.")
    return head, integrated


def studio_pilot(app, scratch, media, out):
    project = create(app, "O Último Sinal", "estudio")
    studio = project + "?sec=estudio"
    script = (INPUT / "roteiro-farol.txt").read_text(encoding="utf-8")
    briefing = upload(app, project, "roteiro-farol.txt", script.encode())
    stage = task(app, project, "Montar episodio e revisar continuidade")
    app.form(studio, "Biblia", {"title": "O Último Sinal", "script": script,
                               "bible": "Público geral; animação 16:9, 24 s, noite azul e luz âmbar. Personagens: farol e barco. Sem vozes. "
                               "Trilha tonal e efeitos originais. Direitos para demonstração local. Produção e revisão do piloto por Codex."})
    descriptions = ["Costa à noite, farol âmbar e barco em movimento.",
                    "Aproximação do farol. O facho deve continuar âmbar.", "O barco cruza a boia. Som do sino."]
    for direction in descriptions:
        app.form(studio, "Plano", {"scene": 1, "sceneDescription": "Uma embarcação cruza a costa à noite.", "direction": direction, "frames": 192})
    shot_ids = re.findall(rb'data-shot="([\da-f]{32})"', app.get(studio))
    names = ["plano-1.mp4", "plano-2-vermelho.mp4", "plano-3.mp4", "plano-2-ambar.mp4"]
    take_ids = []
    for index, name in enumerate(names):
        asset = upload(app, project, name, (media / name).read_bytes(), "Animação gráfica original de Codex")
        shot_id = shot_ids[(0, 1, 2, 1)[index]].decode()
        page = app.form(studio, "Take", {"shot": shot_id, "asset": asset, "parameters": "Importado. Animação local original, 24 fps. Custo remoto zero."})
        take_ids.append(attr(page, r'data-take="([\da-f]{32})"[^>]*><summary>' + re.escape(name)))
    for role, name in [("trilha", "trilha.wav"), ("efeitos", "efeitos.wav")]:
        asset = upload(app, project, name, (media / name).read_bytes(), "Composição e síntese locais originais")
        app.form(studio, "Faixa", {"asset": asset, "role": role, "voice": "Sem voz; autoria do piloto", "gain": "1", "start": 0})
    cover = upload(app, project, "capa.png", (media / "capa.png").read_bytes())
    caption = upload(app, project, "legenda.srt", (INPUT / "legenda.srt").read_bytes())
    upload(app, project, "rascunho-privado.txt", CANARY, rights="Interno; fora da entrega.")
    for take in take_ids[:3]:
        app.form(studio, "RevisarTake", {"take": take, "select": "true", "notes": "Seleção para a primeira montagem e prova de continuidade."})
    initial = app.form(studio, "Edicao")
    edit_initial = attr(initial, r'data-edit="([\da-f]{32})"')
    app.form(studio, "Render", {"edit": edit_initial})
    app.form(studio, "RevisarEdicao", {"edit": edit_initial, "accept": "false", "notes": "Reprovado: o facho vermelho no plano 2 diverge da bíblia."})
    old_page = app.get(studio)
    old_link = attr(old_page, r'<a href="([^"]+)">Baixar master</a>')
    (out / "master-reprovado.mp4").write_bytes(app.get(old_link))
    move(app, project, stage, "pausar", "Retomar do checkpoint: reprovar take vermelho do plano 2, selecionar âmbar e renderizar a nova edição.")
    app.stop()
    app.start()
    check(b"Retomar do checkpoint" in app.get(project + "?sec=historico"), "Checkpoint do estúdio ausente.")
    check(download(app, project, briefing) == script.encode(), "Roteiro alterado após reinício.")
    move(app, project, stage, "retomar", "Continuar a partir da edição reprovada, mantendo planos 1 e 3.")
    app.form(studio, "RevisarTake", {"take": take_ids[1], "select": "false", "notes": "Facho vermelho quebra a continuidade da iluminação."})
    app.form(studio, "RevisarTake", {"take": take_ids[3], "select": "true", "notes": "Facho âmbar, mesma locação e direção compatível."})
    final_page = app.form(studio, "Edicao")
    edit_final = attr(final_page, r'data-edit="([\da-f]{32})"')
    check(edit_initial != edit_final, "A substituição precisa criar uma nova edição.")
    # Kill the running server and render process after the durable render state is visible.
    with ThreadPoolExecutor(max_workers=1) as worker:
        running = worker.submit(app.form, studio, "Render", {"edit": edit_final})
        until = time.monotonic() + 10
        while f'data-edit="{edit_final}" data-state="renderizando"'.encode() not in app.get(studio):
            check(not running.done() and time.monotonic() < until, "Não foi possível interromper o render em andamento.")
            time.sleep(.04)
        subprocess.run(["taskkill", "/PID", str(app.process.pid), "/T", "/F"], capture_output=True, check=True)
        app.process.wait(timeout=8)
        try:
            running.result(timeout=8)
        except (ConnectionError, urllib.error.URLError, TimeoutError, OSError):
            pass
        else:
            raise AssertionError("O render concluiu antes da interrupção injetada.")
    app.stop()
    app.start()
    pending = app.get(studio)
    check(f'data-edit="{edit_final}" data-state="renderizando"'.encode() in pending, "Timeline pendente não sobreviveu.")
    started = time.monotonic()
    rendered = app.form(studio, "Render", {"edit": edit_final})
    render_seconds = round(time.monotonic() - started, 3)
    check(f'data-edit="{edit_final}" data-state="revisao"'.encode() in rendered, "Render não entrou em revisão.")
    app.form(studio, "RevisarEdicao", {"edit": edit_final, "accept": "true",
                                       "notes": "Segunda edição. Planos 1 e 3 reaproveitados; facho do plano 2 corrigido. "
                                       "H.264/AAC e 24 s conferidos. Autoria local e uso de demonstração."})
    export_fields = {"edit": edit_final, "caption": caption, "cover": cover,
                     "metadata": "O Último Sinal · público geral. Animação, trilha e efeitos do piloto produzidos por Codex. Uso local."}
    app.form(studio, "Exportar", export_fields, expected=422)
    package = app.form(studio, "Exportar", {**export_fields, "authorized": "true"})
    with zipfile.ZipFile(io.BytesIO(package)) as zipped:
        manifest = json.loads(zipped.read("manifesto.json"))
        for path, sha in manifest["hashes"].items():
            entry = zipped.read(path)
            check(digest(entry) == sha and CANARY not in entry, f"Falha de integridade ou isolamento em {path}.")
        check("rascunho-privado.txt" not in zipped.namelist(), "Rascunho entrou na exportação.")
        timeline = json.loads(zipped.read("timeline.json"))
        check([c["take"] for c in timeline["clips"]] == [take_ids[0], take_ids[3], take_ids[2]], "Substituição afetou planos indevidos.")
        check(sum(c["frames"] for c in timeline["clips"]) == 576 and len(timeline["audio"]) == 2, "Timeline divergente.")
        master = zipped.read("master.mp4")
        check(digest(master) != digest((out / "master-reprovado.mp4").read_bytes()), "Masters iguais.")
        for file in ("master.mp4", "capa.png", "ffprobe.json", "timeline.json"):
            (out / file).write_bytes(zipped.read(file))
    (out / "pacote-editorial.zip").write_bytes(package)
    move(app, project, stage, "revisar", "Master final e pacote conferidos por hash.")
    move(app, project, stage, "aceitar", "Pacote local entregue após substituição do take.")
    return project, edit_final, render_seconds, digest(master)


def research_pilot(app, out):
    project = create(app, "Evidências de implementação", "pesquisa")
    page = project + "?sec=pesquisa"
    response = app.form(page, "Pergunta", {"question": "Quais requisitos do REST v1 são verificáveis por documentação oficial?",
                                            "criteria": "Fontes primárias; distinguir escopos, cotas e ciclo de ingestão. Consulta de 23/11/2026."})
    question = attr(response, r'data-question="([\da-f]{32})"')
    entries = [
        ("Autenticação", "authentication", "OAuth 2.0 scopes", "O REST v1 exige OAuth 2.0 de usuário. Chaves de API não são aceitas."),
        ("Limites e cotas", "rate-limits", "Per-project rate limits; Per-user rate limits", "Os limites da prévia se aplicam a projeto Cloud e usuário autenticado."),
        ("Fontes", "sources", "Resource representation; state", "Uma fonte pode estar em PROCESSING, ACTIVE, FAILED ou DELETING."),
    ]
    for name, slug, excerpt, claim in entries:
        response = app.form(page, "Fonte", {"question": question, "label": name, "locator": "https://developers.google.com/notebook/docs/" + slug,
                                              "author": "Google for Developers", "rights": "Referência pública e paráfrase.", "excerpt": excerpt})
        source = attr(response, r'data-source="([\da-f]{32})"><summary>' + name)
        response = app.form(page, "Afirmacao", {"source": source, "claim": claim, "inference": "false"})
        claim_ids = re.findall(rb'data-claim="([\da-f]{32})"', response)
        app.form(page, "RevisarPesquisa", {"claim": claim_ids[-1].decode(), "approved": "true", "notes": "Conferido na seção indicada por Codex."})
    report = app.get(project + "?handler=Relatorio")
    check(report.decode().count("Aprovada.") == 3, "Relatório sem três afirmações revisadas.")
    (out / "relatorio-pesquisa.md").write_bytes(report)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--central", required=True, type=Path)
    parser.add_argument("--orcamentos", required=True, type=Path)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--midia", required=True, type=Path)
    parser.add_argument("--destino", required=True, type=Path)
    args = parser.parse_args()
    out = args.destino.resolve()
    out.mkdir(parents=True)
    scratch = out / "execucao"
    scratch.mkdir()
    app = LocalApp(args.central.resolve(), scratch, "central")
    quotes = LocalApp(args.orcamentos.resolve(), scratch, "orcamentos")
    integrated = None
    try:
        app.start()
        check(app.get("/central.css").startswith(b":root"), "Folha de estilos ausente no release.")
        resource_page = app.get("/Recursos")
        observation = attr(resource_page, r'<textarea name="snapshot"[^>]*>(.*?)</textarea>')
        check(b"Desconhecido" in app.form("/Recursos", "Observar", {"snapshot": observation}), "Observação nula ausente.")
        app.form("/Recursos", "Observar", {"snapshot": observation[:-1] + ',"segredo":"campo-invalido"}'}, expected=422)
        try:
            app.request("/", b"x=1", {"Origin": "https://incorreta.invalid"}, expected=403)
            app.request("/", b"x=1", {"Origin": app.url, "Content-Type": "application/x-www-form-urlencoded"}, expected=400)
        except AssertionError:
            raise
        quote_project = create(app, "Oficina Horizonte", "software")
        briefing = (INPUT / "briefing-orcamentos.txt").read_bytes()
        artifact = upload(app, quote_project, "briefing-orcamentos.txt", briefing)
        other = create(app, "Projeto de controle", "software")
        app.get(other + "?handler=Arquivo&asset=" + artifact, expected=404)
        response = app.form(quote_project + "?sec=arquivos", "Importar", {"origin": "teste", "rights": RIGHTS, "key": "bad"},
                            ("vazio.txt", b""), expected=422)
        check(b"vazio.txt" not in response, "Upload inválido foi vinculado.")
        repo, work1, work2, sha_base = worktrees_prepare(scratch)
        predecessor = attr(app.get(quote_project), r'data-task="([\da-f]{32})"[^>]*>\s*<strong>Validar briefing-orcamentos.txt')
        task_id = task(app, quote_project, "Implementar orcamento e PDF", (predecessor,), policy="assistida")
        package = app.form(quote_project, "PacoteTarefa", {"task": task_id, "baseCommit": sha_base,
                           "objective": "Atender briefing versionado da Oficina Horizonte.", "acceptance": "Persistência, total exato e exportação depois de reinício."})
        (out / "pacote-tarefa.json").write_bytes(package)
        check(json.loads(package)["inputs"][0]["hash"] == digest(briefing), "Pacote não recebeu a entrada validada da dependência.")
        move(app, quote_project, task_id, "pausar", f"Retomar do checkpoint na base {sha_base}. Revisar worktrees, integrar e testar.")
        app.stop()
        app.start()
        check(download(app, quote_project, artifact) == briefing, "Briefing diferente após fechar e reabrir.")
        check(digest(briefing).encode() in app.get(quote_project + "?sec=arquivos"), "Hash não foi recuperado.")
        check(b"Retomar do checkpoint" in app.get(quote_project + "?sec=historico"), "Checkpoint de software ausente.")
        move(app, quote_project, task_id, "retomar", "Usar o briefing e a base persistidos.")
        head, integrated_exe = worktrees_finish(repo, work1, work2, sha_base, out, args.dotnet.resolve())
        os.environ["DOTNET_ROOT"] = str(args.dotnet.resolve().parent)
        integrated = LocalApp(integrated_exe, scratch, "orcamentos", "orcamentos-integrado")
        seconds_integrated, pdf_branch = quote_roundtrip(integrated, out)
        seconds_packaged, pdf_sha = quote_roundtrip(quotes, out)
        check(pdf_branch == pdf_sha, "Release e branch integrada produziram PDFs diferentes.")
        for file in ("orcamentos.bundle", "orcamentos-revisado.patch", "build-orcamentos.txt", "orcamento-1.pdf"):
            upload(app, quote_project, file, (out / file).read_bytes(), "Codex local; Git, build e execução revisados", task_id=task_id)
        move(app, quote_project, task_id, "revisar", f"Revisão Codex. Diff verificado, build e aceite pela interface. Commit {head}.")
        move(app, quote_project, task_id, "aceitar", "Documento exportado com R$ 143,70. Persistência confirmada após reiniciar os dois aplicativos.")
        film_project, edit, render_seconds, film_sha = studio_pilot(app, scratch, args.midia.resolve(), out)
        research_pilot(app, out)
        app.stop()
        snapshot = out / "snapshot-pilotos"
        subprocess.run([str(app.executable), "backup", str(snapshot)], cwd=app.executable.parent, env=app.env, check=True)
        restored = scratch / "dados restaurados"
        subprocess.run([str(app.executable), "restore", str(snapshot)], cwd=app.executable.parent,
                       env={**app.env, "CENTRAL_DATA": str(restored)}, check=True)
        app.start(restored)
        check(download(app, quote_project, artifact) == briefing, "Restore não preservou briefing.")
        check(b"Reconciliar efeitos posteriores" in app.get("/Diagnostico"), "Bloqueio remoto do restore ausente.")
        check(f'data-edit="{edit}" data-state="aprovada"'.encode() in app.get(film_project + "?sec=estudio"), "Restore perdeu a edição aprovada.")
        check(all(CANARY not in path.read_bytes() for path in scratch.glob("*.log")), "Canário em logs.")
        evidence = {"software": {"commit": head, "briefingSha256": digest(briefing), "pdfSha256": pdf_sha, "totalCentavos": 14370,
                                  "testeBranchSegundos": seconds_integrated, "testePacoteSegundos": seconds_packaged},
                    "estudio": {"edit": edit, "masterSha256": film_sha, "duracaoSegundos": 24, "frames": 576, "planos": 3,
                                "faixas": 2, "takeSubstituido": 1, "renderFinalSegundos": render_seconds},
                    "entregasPilotoAceitas": 2, "custoRemotoIncrementalBRL": 0, "custoPlenoBRL": None,
                    "esperaRemota": None, "revisaoHumanaMinutos": None, "commitAutor": "Codex Piloto",
                    "participacao": "Codex local nas decisões de piloto e nos envios automatizados pela interface. Aceite humano comercial pendente."}
        (out / "resultado.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(evidence, ensure_ascii=True))
    finally:
        app.stop()
        quotes.stop()
        if integrated is not None:
            integrated.stop()


if __name__ == "__main__":
    main()
