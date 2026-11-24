"""Execução verificável das jornadas novas pelos mesmos formulários da Central."""

import argparse
import importlib.util
import io
import json
import re
import subprocess
import time
import zipfile
from pathlib import Path


legacy_spec = importlib.util.spec_from_file_location("pilotos", Path(__file__).with_name("verificar-pilotos.py"))
legacy = importlib.util.module_from_spec(legacy_spec)
legacy_spec.loader.exec_module(legacy)
LocalApp, check, attr = legacy.LocalApp, legacy.check, legacy.attr
MODEL = "qwen3:4b-instruct-2507-q4_K_M"


def create_work(app, area, mode, project, title, request, existing=""):
    path = "/area/" + area + "?mode=" + mode
    page = app.form(path, fields={"mode": mode, "project": existing, "projectName": project, "title": title,
                                   "request": request, "priority": 5, "key": legacy.uuid.uuid4().hex})
    return attr(page, r'action="(/trabalhos/[a-f0-9]+)\?handler=Mensagem"')


def state(app, path):
    return json.loads(app.get(path + "?handler=Estado"))


def wait_for(app, path, wanted, deadline=420):
    end = time.monotonic() + deadline
    while time.monotonic() < end:
        result = state(app, path)
        if result["state"] in wanted:
            return result
        check(result["state"] != "falhou", result["note"])
        time.sleep(.45)
    raise TimeoutError("Estado esperado: " + str(wanted))


def act(app, path, handler, fields=None, expected=200):
    return app.form(path, handler, {"revision": state(app, path)["revision"], **(fields or {})}, expected=expected)


def textarea(page, field):
    return attr(page, r'<textarea name="' + field + r'"[^>]*>([\s\S]*?)</textarea>').strip()


def fetch_file(app, page, name):
    pattern = r'<strong>' + re.escape(name) + r'</strong>[\s\S]*?href="([^"]+)">Baixar arquivo'
    link = attr(page, pattern)
    return app.get(link)


def project_id(app, path):
    return attr(app.get(path), r'<a href="/projetos/([a-f0-9]{32})">[^<]+</a>')


def add_file(app, path, name, contents, role="referencia"):
    return app.form(path, "Importar", {"role": role, "origin": "Produção local para o ensaio", "rights": "Uso autorizado no piloto"},
                    (name, contents))


def programming(app, out):
    home = app.get("/")
    check(b"Criar sistema" in home and b"Continuar de onde parei" in home, "Entrada de Programação ausente.")
    request = ("Crie um sistema local chamado Caderno de Tarefas. Permita cadastrar tarefas por texto, "
               "marcar como concluídas, excluir e conservar os dados ao recarregar, usando localStorage. "
               "Use formulário rotulado 'Nova tarefa' e botão 'Adicionar'. "
               "Use um checkbox por tarefa para concluir. Ao excluir, confirme com um botão 'Excluir'.")
    path = create_work(app, "programacao", "criar", "Caderno de Tarefas", "Sistema de tarefas domésticas", request)
    act(app, path, "Rascunho", {"field": "pedido", "body": request + " Interface em português."}, expected=204)
    app.stop()
    app.start()
    page = app.get(path)
    check("Interface em português." in textarea(page, "body"), "Rascunho do pedido não sobreviveu ao reinício.")
    act(app, path, "Pedido", {"body": textarea(page, "body")})
    act(app, path, "Planejar", {"model": MODEL, "action": "modelo"})
    wait_for(app, path, {"plano"})
    plan = textarea(app.get(path), "plan") + "\n\nAceite adicional: testar adição, marcação e persistência com teclado."
    act(app, path, "Plano", {"plan": plan, "executor": "ollama", "model": MODEL, "action": "confirmar"})
    wait_for(app, path, {"rodando"}, 18)
    app.stop()
    app.start()
    check(state(app, path)["state"] == "pausado", "Fila não recuperou a execução interrompida.")
    act(app, path, "Acao", {"action": "retomar"})
    for attempt in range(3):
        result = wait_for(app, path, {"revisao", "falhou"})
        if result["state"] == "revisao":
            break
        check(attempt < 2, result["note"])
        act(app, path, "Acao", {"action": "retomar"})
    page = app.get(path)
    check(b"Ollama local" in page and b"testes.txt" in page and b"alteracoes.patch" in page, "Trabalho sem evidências.")
    for name in ("index.html", "testes.txt", "alteracoes.patch", "sistema.zip", "entrega.bundle"):
        (out / name).write_bytes(fetch_file(app, page, name))
    if b"especificacao.json" in page:
        (out / "especificacao.json").write_bytes(fetch_file(app, page, "especificacao.json"))
    (out / "plano.txt").write_text(plan, encoding="utf-8")
    act(app, path, "Acao", {"action": "aceitar"})
    check(state(app, path)["state"] == "aceito", "Aceite não persistiu.")
    print("OK Programação → Criar sistema · pedido, rascunho, plano de Ollama, edição, aprovação, interrupção, Git, testes e aceite", flush=True)
    return path


def video(app, out, media):
    path = create_work(app, "video", "serie", "Produção costeira", "O Último Sinal",
                       "CANARIO-VIDEO. Um pequeno barco passa diante de um farol. Minissérie sem diálogo.")
    story = {
        "script": "O barco cruza a costa. O farol ilumina a passagem. O barco encontra a boia.",
        "characters": "Farol e barco fictícios. Manter facho âmbar. Animação e áudio autorais locais.",
        "shot1": "Apresentar costa e movimento do barco.",
        "shot2": "Cortar para o facho do farol.",
        "shot3": "Terminar com a passagem pela boia e som de sino."
    }
    app.form(path, "Roteiro", {**story, "approved": "false"})
    app.stop()
    app.start()
    check(textarea(app.get(path), "shot2") == story["shot2"], "Storyboard não persistiu.")
    work = app.form(path, "Roteiro", {**story, "approved": "true"})
    studio = attr(work, r'href="(/projetos/[a-f0-9]+\?sec=estudio)">Abrir montagem e comparar takes')
    project = studio.split("?")[0]
    scene_ids = re.findall(rb'data-shot="([a-f0-9]+)"', app.get(studio))
    check(len(scene_ids) == 3, "Aprovação não criou os três planos.")
    flow = json.loads(app.form(studio, "Flow", {"shot": scene_ids[1].decode(), "outputs": 2, "unitCost": 10}))
    check(flow["expectedCredits"] == 20 and flow["mode"] == "assistida", "A ficha não calculou a previsão de créditos.")
    (out / "ficha-flow-cena-2.json").write_text(json.dumps(flow, ensure_ascii=False, indent=2), encoding="utf-8")
    names = ("plano-1.mp4", "plano-2-vermelho.mp4", "plano-3.mp4", "plano-2-ambar.mp4")
    takes = []
    for i, name in enumerate(names):
        asset = legacy.upload(app, project, name, (media / name).read_bytes(), "Animação autoral produzida localmente")
        fields = {"shot": scene_ids[(0, 1, 2, 1)[i]].decode(), "asset": asset, "parameters": "Importação local aprovada para o ensaio."}
        result = app.form(studio, "Take", fields)
        take = attr(result, r'data-take="([a-f0-9]+)"[^>]*><summary>' + re.escape(name))
        takes.append(take)
        if i < 3:
            app.form(studio, "RevisarTake", {"take": take, "select": "true", "notes": "Seleção inicial para comparação."})
        else:
            extra = app.form(studio, "Take", fields)
            check(extra.count(f'data-take="{take}"'.encode()) == 1, "Take duplicado ao repetir o vínculo.")
    for role, name in (("trilha", "trilha.wav"), ("efeitos", "efeitos.wav")):
        asset = legacy.upload(app, project, name, (media / name).read_bytes(), "Áudio autoral local")
        app.form(studio, "Faixa", {"asset": asset, "role": role, "voice": "Composição autoral, sem voz.", "gain": 1, "start": 0})
    caption = legacy.upload(app, project, "legenda.srt", (legacy.INPUT / "legenda.srt").read_bytes())
    cover = legacy.upload(app, project, "capa.png", (media / "capa.png").read_bytes())
    initial = attr(app.form(studio, "Edicao"), r'data-edit="([a-f0-9]+)"')
    app.form(studio, "TrocarTake", {"take": takes[3], "reason": "Facho âmbar corrige a continuidade da cena 2."})
    revised = attr(app.form(studio, "Edicao"), r'data-edit="([a-f0-9]+)"')
    check(revised != initial, "A refação não criou outra montagem.")
    app.stop()
    app.start()
    app.form(studio, "Render", {"edit": revised})
    app.form(studio, "RevisarEdicao", {"edit": revised, "accept": "true", "notes": "24 segundos, três planos e áudio revisados para o ensaio."})
    rendered = app.form(studio, "Exportar", {"edit": revised, "caption": caption, "cover": cover,
                       "metadata": "O Último Sinal, estreia local. Animação e som autorais.", "authorized": "true"})
    with zipfile.ZipFile(io.BytesIO(rendered)) as package:
        timeline = json.loads(package.read("timeline.json"))
        check([entry["take"] for entry in timeline["clips"]] == [takes[0], takes[3], takes[2]], "A troca afetou 1 ou 3.")
        check(len(timeline["audio"]) == 2 and sum(entry["frames"] for entry in timeline["clips"]) == 576, "Duração ou som incorretos.")
        for name in ("master.mp4", "timeline.json", "ffprobe.json"):
            (out / name).write_bytes(package.read(name))
    (out / "episodio-editorial.zip").write_bytes(rendered)
    work_page = app.get(path)
    master = attr(work_page, r'<option value="([a-f0-9]+)">master-[^<]+\.mp4</option>')
    app.form(path, "VinculoVideo", {"asset": master, "toParent": "true"})
    check(state(app, path)["state"] == "aceito", "Master não foi publicado no projeto.")
    print("OK Vídeo e minisséries · ideia, roteiro, 3 planos, troca somente na cena 2, 2 faixas, restart e MP4 de 24 s", flush=True)
    return path


def other_areas(app, video_path, out, media):
    project = project_id(app, video_path)
    audio = create_work(app, "audio", "montar", "", "Revisar trilha autoral", "Medir e vincular a trilha à cena 1.", project)
    page = add_file(app, audio, "trilha-local.wav", (media / "trilha.wav").read_bytes())
    asset = attr(page, r'<input name="asset" value="([a-f0-9]+)" type="hidden">')
    page = app.form(audio, "RevisarAudio", {"asset": asset, "role": "trilha", "approved": "true", "note": "Síntese autoral local aprovada."})
    check(b"24,00 s" in page, "Duração medida ausente.")
    app.form(audio, "EnviarAudio", {"asset": asset, "episode": video_path.split("/")[-1], "scene": 1})

    research = create_work(app, "pesquisa", "investigar", "", "Referências de montagem", "Qual deve ser a cor do facho no plano 2?", project)
    sources = app.form(research, "Fonte", {"label": "Guia do estúdio", "locator": "https://ffmpeg.org/ffmpeg-filters.html",
                        "author": "Equipe FFmpeg", "rights": "Documentação técnica pública", "excerpt": "Controle dos filtros de vídeo."})
    source = attr(sources, r'data-source="([a-f0-9]+)"')
    claim_page = app.form(research, "Afirmacao", {"source": source, "claim": "A cor aprovada para continuidade é âmbar.", "inference": "true"})
    claim = attr(claim_page, r'data-claim="([a-f0-9]+)"')
    app.form(research, "RevisaoFonte", {"claim": claim, "approved": "true", "notes": "Conferido no storyboard."})
    report = app.get(research + "?handler=Relatorio")
    check("Aprovada." in report.decode() and b"https://ffmpeg.org" in report, "Relatório não preservou a referência.")
    (out / "investigacao.md").write_bytes(report)

    distribution = create_work(app, "distribuicao", "preparar", "", "Pacote de estreia", "Preparar entrega local do episódio.", project)
    page = add_file(app, distribution, "capa-local.png", (media / "capa.png").read_bytes())
    cover = attr(page, r'<option value="([a-f0-9]+)">capa-local.png</option>')
    page = add_file(app, distribution, "legenda-local.srt", (legacy.INPUT / "legenda.srt").read_bytes())
    caption = attr(page, r'<option value="([a-f0-9]+)">legenda-local.srt</option>')
    master = attr(page, r'<option value="([a-f0-9]+)">master-[^<]+\.mp4</option>')
    page = app.form(distribution, "Entrega", {"media": master, "cover": cover, "caption": caption,
                    "title": "O Último Sinal", "description": "Episódio e créditos autorais", "channel": "Arquivo local", "visibility": "local"})
    delivery = attr(page, r'data-delivery="([a-f0-9]+)"')
    app.form(distribution, "AprovarEntrega", {"delivery": delivery})
    package = app.get(distribution + "?handler=PacoteCanal&delivery=" + delivery)
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        check(set(archive.namelist()) == {"master.mp4", "capa.png", "legenda.srt", "entrega.json"},
              "Pacote de canal inválido.")
    (out / "pacote-canal.zip").write_bytes(package)
    app.form("/Recursos", "Registrar", {"product": "flow", "connection": "flow-ensaio", "account": "Perfil fictício",
        "profile": "local", "version": "painel-ensaio", "model": "Vídeo", "subject": "família-teste", "bucket": "video",
        "window": "mensal", "unit": "creditos", "status": "available", "remaining": 15, "source": "Fixture da interface"})
    print("OK Pesquisa, Áudio, Distribuição e Contas · relatório revisado, faixa com duração, vínculo por cena, pacote e cota", flush=True)


def assisted(app, out):
    repo = out / "repo-operador"
    repo.mkdir()
    legacy.git(repo, "init", "-b", "main")
    (repo / "notas.txt").write_text("Versão inicial\n", encoding="utf-8")
    legacy.git(repo, "add", ".")
    legacy.git(repo, "commit", "-m", "Base do ensaio")
    base = out / "repo-base.bundle"
    legacy.git(repo, "bundle", "create", str(base), "main")
    path = create_work(app, "programacao", "corrigir", "Repositório demonstrativo", "Corrigir uma nota",
                       "Corrija o texto de notas.txt preservando o restante do repositório.")
    add_file(app, path, "repo-base.bundle", base.read_bytes(), "repositorio")
    act(app, path, "Planejar", {"action": "modelo-editavel"})
    act(app, path, "Plano", {"action": "confirmar", "executor": "assistido", "plan": textarea(app.get(path), "plan")})
    wait_for(app, path, {"aguardando"})
    task_package = json.loads(fetch_file(app, app.get(path), "pacote-tarefa.json"))
    check(task_package["base"] == legacy.git(repo, "rev-parse", "HEAD"), "Pacote não fixou a base do Git bundle.")
    (repo / "notas.txt").write_text("Versão corrigida\n", encoding="utf-8")
    patch = subprocess.run(["git", "-c", "core.autocrlf=false", "diff", "--", "notas.txt"],
                           cwd=repo, capture_output=True, check=True).stdout
    add_file(app, path, "operador.patch", patch, "patch")
    add_file(app, path, "recibo.txt", b"Operador do ensaio alterou a nota no Git local e conferiu sua leitura.", "recibo")
    act(app, path, "Receber")
    wait_for(app, path, {"revisao"})
    tests = fetch_file(app, app.get(path), "testes.txt")
    check(b"PASSOU" in tests and task_package["base"].encode() in tests, "A devolução não foi aplicada na base fixada.")
    (out / "testes-assistido.txt").write_bytes(tests)
    act(app, path, "Acao", {"action": "aceitar"})
    print("OK Programação assistida · base Git bundle, pacote, patch e recibo, verificação e aceite", flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--central", required=True, type=Path)
    parser.add_argument("--destino", required=True, type=Path)
    parser.add_argument("--base", type=Path, help="Pasta dos dados copiados ou migrados")
    parser.add_argument("--midia", type=Path)
    parser.add_argument("--somente-video", action="store_true")
    parser.add_argument("--demais-areas", action="store_true")
    parser.add_argument("--assistido", action="store_true")
    parser.add_argument("--verificar-migracao", action="store_true")
    args = parser.parse_args()
    out = args.destino.resolve()
    out.mkdir(parents=True)
    app = LocalApp(args.central.resolve(), out, "central")
    start = app.start
    if args.base:
        app.start = lambda: start(args.base.resolve())
    try:
        app.start()
        paths = {}
        if args.verificar_migracao:
            old = attr(app.get("/"), r'<a href="(/projetos/[a-f0-9]+)">Oficina Horizonte</a>')
            files = app.get(old + "?sec=arquivos")
            briefing = attr(files, r'<a href="([^"]+)">briefing-orcamentos\.txt</a>')
            check(app.get(briefing) == (legacy.INPUT / "briefing-orcamentos.txt").read_bytes(), "Arquivo da 0.1.0 divergiu.")
            check(json.loads(app.get("/api/v1/saude"))["migrations"] >= 9, "O schema da 0.1.0 não migrou.")
            print("OK migração da 0.1.0 · projeto e bytes do briefing preservados", flush=True)
        if not args.somente_video:
            paths["programacao"] = programming(app, out)
        if args.midia:
            paths["video"] = video(app, out, args.midia.resolve())
            if args.demais_areas:
                other_areas(app, paths["video"], out, args.midia.resolve())
        if args.assistido:
            assisted(app, out)
        if len(paths) == 2:
            program_page = app.get(paths["programacao"])
            check(b"CANARIO-VIDEO" not in program_page and b"plano-2-ambar" not in program_page, "O vídeo contaminou o trabalho de programação.")
            check(b"Caderno de Tarefas" not in app.get(paths["video"]), "O pedido de programação contaminou o vídeo.")
            program_asset = attr(program_page, r'asset=([a-f0-9]{32})')
            app.get(paths["video"] + "?handler=Arquivo&asset=" + program_asset, expected=404)
        (out / "trabalhos.json").write_text(json.dumps(paths), encoding="utf-8")
    finally:
        app.stop()


if __name__ == "__main__":
    main()
