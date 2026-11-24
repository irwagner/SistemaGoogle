"""Exercise the extracted Windows package with disposable data and a loopback connector."""

import argparse
import base64
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


CODE = "export function soma(a, b) { return a + b; }\n"
EXTENSION = "import { soma } from './calc.mjs';\nexport const dobroDaSoma = (a, b) => soma(a, b) * 2;\n"
TEST_A = """import test from 'node:test';
import assert from 'node:assert/strict';
import { soma } from './calc.mjs';
test('A soma dois numeros', () => assert.equal(soma(4, 5), 9));
"""
TEST_B = TEST_A + """import { dobroDaSoma } from './extended.mjs';
test('B usa a soma de A', () => assert.equal(dobroDaSoma(4, 5), 18));
"""
TOKENS = {"fixture-code-a": "ensaio-a", "fixture-code-b": "ensaio-b"}
MEDIA = {"fixture-media-a": "video-a", "fixture-media-b": "video-b"}


def before(seconds):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()


class Fixture(BaseHTTPRequestHandler):
    events = []
    passed_b_limit = set()
    allow_download = False
    clip = b""

    def log_message(self, *_):
        pass

    def send_json(self, status, content):
        payload = json.dumps(content).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if self.path == "/identity":
            identity = TOKENS.get(self.headers.get("Authorization", "").removeprefix("Bearer "))
            if identity is None:
                self.send_json(401, {})
                return
            self.send_json(200, {"identity": identity, "model": "codigo-ensaio", "subjects": [f"projeto-{identity}"]})
            return
        if "/operations/" in self.path:
            identity = MEDIA.get(self.headers.get("x-goog-api-key"))
            self.events.append({"operation": self.path, "method": "GET", "identity": identity})
            if not self.allow_download and self.path.endswith("op-a"):
                self.send_json(503, {})
                return
            video = base64.b64encode(self.clip).decode()
            self.send_json(200, {"done": True, "response": {"generatedVideos": [{"bytesBase64Encoded": video}]}})
            return
        self.send_json(404, {})

    def do_POST(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while size := int(self.rfile.readline().strip(), 16):
                chunks.append(self.rfile.read(size))
                self.rfile.readline()
            self.rfile.readline()
            raw = b"".join(chunks)
        else:
            raw = self.rfile.read(int(self.headers["Content-Length"]))
        request = json.loads(raw)
        if self.path == "/step":
            self.step(request)
            return
        if self.path.endswith(":predictLongRunning"):
            identity = MEDIA.get(self.headers.get("x-goog-api-key"))
            prompt = request["instances"][0]["prompt"]
            self.events.append({"method": "POST", "identity": identity, "prompt": prompt})
            if "ambiguo" in prompt:
                self.send_json(503, {})
                return
            count = sum(event.get("method") == "POST" and event.get("identity") == identity for event in self.events)
            suffix = f"-{count}" if count > 1 else ""
            self.send_json(200, {"name": f"models/veo-ensaio/operations/op-{identity[-1]}{suffix}"})
            return
        self.send_json(404, {})

    def step(self, r):
        identity = TOKENS.get(self.headers.get("Authorization", "").removeprefix("Bearer "))
        if identity is None:
            self.send_json(401, {})
            return
        self.events.append({"project": r["project"], "task": r["task"], "attempt": r["attempt"],
                            "step": r["step"], "identity": identity, "files": r["hashes"],
                            "prior_tests": r["receipts"], "next": r["next"]})
        reply = {"identity": identity}
        if r["pedido"] == "Timeout local":
            assert identity == "ensaio-a"
            reply.update(writes=[{"path": "parcial.mjs", "content": "export const gravado = true;\n"}], lostReceipt=True)
        elif r["step"] == 0:
            assert identity == "ensaio-a" and r["files"]["do-titular.txt"] == "preservar este arquivo\n"
            reply.update(writes=[{"path": "calc.mjs", "content": CODE}, {"path": "tests.mjs", "content": TEST_A}],
                         next="Estender soma com dobroDaSoma e repetir os testes.", test=True)
        elif identity == "ensaio-a":
            reply.update(subject="projeto-ensaio-a", group="gemini-codigo", window="semanal", reset=before(180))
            self.send_json(429, reply)
            return
        elif r["task"] not in self.passed_b_limit:
            self.passed_b_limit.add(r["task"])
            reply.update(subject="ensaio-b", group="codigo", window="global", reset=before(8))
            self.send_json(429, reply)
            return
        else:
            assert r["files"]["calc.mjs"] == CODE and r["files"]["tests.mjs"] == TEST_A
            assert len(r["receipts"]) == 1 and "# pass 1" in r["receipts"][0]["result"]
            reply.update(writes=[{"path": "extended.mjs", "content": EXTENSION},
                                 {"path": "tests.mjs", "content": TEST_B, "expected": r["hashes"]["tests.mjs"]}],
                         next="Entrega verificada.", test=True, done=True)
        self.send_json(200, reply)


def free_pair():
    for _ in range(30):
        with socket.socket() as main, socket.socket() as preview:
            port = 16000 + secrets.randbelow(20000)
            try:
                main.bind(("127.0.0.1", port))
                preview.bind(("127.0.0.1", port + 1))
                return port
            except OSError:
                continue
    raise RuntimeError("Nenhum par de portas de ensaio disponível.")


def wait(check, seconds=20):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = check()
        if value:
            return value
        time.sleep(0.07)
    raise AssertionError(f"Prazo excedido em {check!r}")


class App:
    def __init__(self, root, package, fixture, data_name):
        self.root = root
        self.package = package
        self.data = root / data_name
        self.data.mkdir()
        self.port = free_pair()
        self.base = f"http://127.0.0.1:{self.port}"
        self.env = dict(os.environ, CENTRAL_PORT=str(self.port), CENTRAL_DATA=str(self.data), CENTRAL_NO_BROWSER="1")
        if fixture:
            (self.data / ".centralia-laboratorio").touch()
            self.env["CENTRALIA_LAB_ENDPOINT"] = fixture
        self.env["DOTNET_ROOT"] = str(Path(os.environ["LOCALAPPDATA"]) / "CentralProducaoBuild/sdk-10.0.404")
        self.command = ["cmd.exe", "/d", "/c", "Abrir-CentralIA.cmd", "--sem-navegador"]
        self.process = None
        self.token = None

    def launch(self, legacy=None):
        command = [str(legacy), "--sem-navegador"] if legacy else self.command
        self.log = self.root / f"log-{self.port}-{time.time_ns()}.txt"
        with self.log.open("wb") as log:
            self.process = subprocess.Popen(command, cwd=self.package, env=self.env, stdout=log, stderr=log,
                                            creationflags=subprocess.CREATE_NO_WINDOW)
        def ready():
            if self.process.poll() is not None:
                raise AssertionError(self.log.read_text(encoding="utf-8", errors="replace"))
            try:
                with urllib.request.urlopen(self.base, timeout=2) as response:
                    html = response.read().decode()
                return re.search(r'token: "([a-f0-9]{32})"', html).group(1)
            except (ConnectionError, urllib.error.URLError, TimeoutError):
                return None
        self.token = wait(ready)
        return self

    def close(self):
        if self.process and self.process.poll() is None:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(self.process.pid)], capture_output=True, check=True)
        if self.process:
            self.process.wait()
            self.process = None

    def api(self, path, body=None, method=None, status=200):
        headers = {"X-Central-Token": self.token, "Content-Type": "application/json"}
        raw = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.base + "/api" + path, raw, headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=8) as response:
                data, http = response.read(), response.status
        except urllib.error.HTTPError as error:
            data, http = error.read(), error.code
        assert http == status, (path, http, data)
        return json.loads(data)

    def thread(self, pid, tid):
        return self.api(f"/projects/{pid}/threads/{tid}")["thread"]

    def profile(self, label, identity):
        fixture = identity.startswith("ensaio-")
        return self.api("/google-accounts", {"name": label, "identity": identity, "fixture": fixture,
                                             "key": f"fixture-code-{identity[-1]}" if fixture else ""})["profile"]["id"]


class Browser:
    def __init__(self, root):
        self.root = root
        self.session = f"centralia-rotacao-{os.getpid()}"
        self.cli = shutil.which("playwright-cli.cmd")
        assert self.cli, "Instale Playwright CLI para verificar a interface."

    def run(self, *args):
        result = subprocess.run(["cmd.exe", "/d", "/c", self.cli, f"-s={self.session}", *args],
                                cwd=self.root, capture_output=True, text=True, encoding="utf-8",
                                errors="replace", timeout=50)
        assert result.returncode == 0 and "### Error" not in result.stdout, result.stdout + result.stderr
        return result.stdout

    def code(self, body):
        script = self.root / "browser-check.js"
        script.write_text("async (page) => {\n" + body + "\n}\n", encoding="utf-8")
        return self.run("run-code", "--filename", str(script))


def wait_state(app, pid, tid, state):
    return wait(lambda: (c := app.thread(pid, tid)["continuation"])["state"] == state and c)


def program(app, browser, fixture, evidence):
    a = app.profile("Conta A · ensaio", "ensaio-a")
    b = app.profile("Conta B · ensaio", "ensaio-b")
    p = app.api("/projects", {"name": "Calculadora compartilhada", "kind": "codigo"})
    pid = p["id"]
    details = app.api(f"/projects/{pid}")
    (Path(details["workspace"]) / "do-titular.txt").write_bytes(b"preservar este arquivo\n")
    browser.run("open", app.base, "--browser=msedge")
    browser.code(f"""
        await page.locator('#execution-mode').selectOption('alternar');
        await page.locator('#execution-account').selectOption('{a}');
        await page.locator('#execution-account').selectOption('{b}');
        await page.locator('#home-project').selectOption('{pid}');
        await page.locator('#home-prompt').fill('Crie uma calculadora e depois estenda a soma.');
        await page.locator('#home-send').click();
        await page.waitForURL('**/#/p/{pid}/c/*');
        await page.locator('#continuity-status').waitFor();
    """)
    threads = app.api(f"/projects/{pid}")["threads"]
    assert len(threads) == 1
    tid = threads[0]["id"]
    def waiting_for_both():
        state = app.thread(pid, tid)["continuation"]
        if state["state"] in {"conciliar", "pausado", "autenticar", "concluido"}:
            raise AssertionError((state, fixture.events))
        return state["state"] == "limite" and state.get("wake") and len(state["pauses"]) == 2 and state
    paused = wait(waiting_for_both)
    app.close()
    app.launch()
    c = wait_state(app, pid, tid, "concluido")
    stage_a, stage_b = [h for h in c["history"] if h["status"] == "confirmado"]
    assert [stage_a["requested"], stage_b["requested"]] == [a, b]
    assert [stage_a["effective"], stage_b["effective"]] == ["ensaio-a", "ensaio-b"]
    assert stage_a["after"] == stage_b["before"] and stage_a["after"]["calc.mjs"] == stage_b["after"]["calc.mjs"]
    assert stage_a["before"]["do-titular.txt"] == stage_b["after"]["do-titular.txt"]
    assert "# pass 1" in stage_a["result"] and "# pass 2" in stage_b["result"]
    events = [e for e in fixture.events if e.get("task") == tid]
    assert [(e["identity"], e["step"]) for e in events] == [("ensaio-a", 0), ("ensaio-a", 1), ("ensaio-b", 1), ("ensaio-b", 1)]
    assert all(e["project"] == pid for e in events) and len(app.api("/state")["projects"]) == 1
    assert len(app.thread(pid, tid)["messages"]) == 1
    browser.code(f"""
        await page.goto('{app.base}/?verificacao=retomada#/p/{pid}/c/{tid}');
        await page.setViewportSize({{width: 1440, height: 900}});
        await page.getByText('Concluído', {{ exact: true }}).waitFor();
        await page.getByText('2 · confirmado · Conta B · ensaio · ensaio-b', {{ exact: true }}).waitFor();
        await page.getByText('1 · confirmado · Conta A · ensaio · ensaio-a', {{ exact: true }}).waitFor();
        await page.locator('[data-ptab="files"]').click();
        await page.getByText('extended.mjs', {{ exact: true }}).click();
        await page.locator('#panel-body').getByText(/dobroDaSoma/).waitFor();
        await page.screenshot({{path: {json.dumps(str(evidence / "programacao-a-b.png"))}}});
    """)
    evidence_data = {"project": pid, "task": tid, "submitted_messages": 1, "project_count": 1,
                     "paused_before_restart": paused["pauses"], "wake_before_restart": paused["wake"],
                     "connector_calls": events, "receipts": c["history"], "checkpoint": c["files"]}
    return a, b, pid, evidence_data


def recovery(app, fixture, a, b):
    p = app.api("/projects", {"name": "Conciliação de disco", "kind": "codigo"})["id"]
    url = f"/projects/{p}/threads"
    fixed_a = app.api("/providers", {"id": a, "enabled": False})
    assert any(item["id"] == b and item["enabled"] for item in fixed_a["providers"])
    fixed = app.api(url, {"text": "Conta fixada", "execution": "fixa", "accounts": [a]})["id"]
    wait_state(app, p, fixed, "autenticar")
    assert not any(event.get("task") == fixed for event in fixture.events)
    app.api("/providers", {"id": a, "enabled": True})
    ambiguous = app.api(url, {"text": "Timeout local", "execution": "alternar", "accounts": [a, b]})["id"]
    c = wait_state(app, p, ambiguous, "conciliar")
    assert c.get("pending") and "timeout" in c["issue"].lower()
    app.close()
    app.launch()
    original = app.api(f"{url}/{ambiguous}/inspect")
    assert "parcial.mjs" not in original["before"] and "parcial.mjs" in original["current"]
    app.api(f"{url}/{ambiguous}/resume", {}, status=400)
    calls = [event["identity"] for event in fixture.events if event.get("task") == ambiguous]
    assert calls == ["ensaio-a"]
    return {"fixed_calls": [], "fixed_state": "autenticar", "disk_attempt": original, "disk_calls": calls}


def verify_video(app, fixture, endpoint, project, browser, evidence):
    clip = app.root / "clip.mp4"
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=blue:s=320x180:r=24",
                    "-t", "4", "-c:v", "libx264", "-pix_fmt", "yuv420p", str(clip)], check=True, capture_output=True)
    fixture.clip = clip.read_bytes()
    media = []
    for suffix in ("a", "b"):
        result = app.api("/providers", {"name": f"Vídeo {suffix.upper()} · ensaio", "kind": "gemini",
                                       "baseUrl": endpoint + "v1beta", "apiKey": f"fixture-media-{suffix}", "enabled": True})
        media.append(result["providers"][-1]["id"])
    a, b = media
    app.api("/settings/media", {"videoModel": "veo-ensaio", "providerId": a}, "PUT")
    app.close()
    flow_dir = app.data / "projetos" / project / "flows"
    flow_ids = ("ensaiovideo", "envioambiguo")
    for fid, visuals in zip(flow_ids, [("cena conhecida A", "cena independente B"), ("envio ambiguo",)]):
        path = flow_dir / fid / "storyboard.json"
        path.parent.mkdir(parents=True)
        scenes = [{"id": f"cena{i + 1:02d}", "title": f"Cena {i + 1}", "visual": visual, "narration": "Narração de ensaio.",
                   "seconds": 4} for i, visual in enumerate(visuals)]
        path.write_text(json.dumps({"projectId": project, "id": fid, "title": "Episódio de ensaio", "status": "pronto",
                                    "transitionSeconds": 0, "episodeList": [{"id": "episodio", "title": "Teste", "scenes": scenes}]}),
                        encoding="utf-8")
    app.launch()
    flow, loose = [f"/projects/{project}/flows/{fid}" for fid in flow_ids]
    scene_a = f"{flow}/scenes/cena01"
    scene_b = f"{flow}/scenes/cena02"
    unknown = f"{loose}/scenes/cena01"

    def scene(url, i=0):
        return app.api(url)["episodeList"][0]["scenes"][i]

    app.api(scene_a + "/video", {"confirm": True, "providerId": a})
    app.api(scene_b + "/video", {"confirm": True, "providerId": b})
    known = wait(lambda: (s := scene(flow)).get("operation") and not s.get("busy") and s)
    independent = wait(lambda: (s := scene(flow, 1)).get("video") and s)
    assert known["operation"] == f"{a}|models/veo-ensaio/operations/op-a"
    assert "Vídeo B" in independent["videoOrigin"]
    app.api("/providers", {"id": a, "enabled": False})
    app.close()
    app.launch()
    original_queries = len(fixture.events)
    app.api(scene_a + "/resume-video", {})
    preserved = wait(lambda: (s := scene(flow)).get("error") and not s.get("busy") and s)
    assert "original" in preserved["error"] and preserved["operation"] == known["operation"]
    assert preserved["videoIntent"] == known["videoIntent"] and len(fixture.events) == original_queries
    app.api(scene_a + "/video", {"confirm": True, "providerId": b}, status=400)
    app.api(scene_a, method="DELETE", status=400)
    assert len(fixture.events) == original_queries
    app.api(unknown + "/video", {"confirm": True, "providerId": b})
    unconfirmed = wait(lambda: (s := scene(loose)).get("videoIntent") and not s.get("busy") and s)
    assert not unconfirmed.get("operation") and unconfirmed["videoIntent"]["provider"] == b
    app.api(unknown + "/video", {"confirm": True, "providerId": b}, status=400)
    assert sum("ambiguo" in event.get("prompt", "") for event in fixture.events) == 1

    browser.code(f"""
        await page.goto('{app.base}/?verificacao=video#/p/{project}/f/{flow_ids[0]}');
        await page.getByText('Operação original preservada', {{exact: false}}).waitFor();
        if (!(await page.locator('[data-sact="video"]').first().isDisabled())) throw Error('Segundo envio habilitado.');
        await page.screenshot({{path: {json.dumps(str(evidence / "video-operacao-preservada.png"))}}});
    """)
    fixture.allow_download = True
    app.api("/providers", {"id": a, "enabled": True})
    app.api(scene_a + "/resume-video", {})
    wait(lambda: scene(flow).get("video"))
    assert scene(flow, 1)["video"] == independent["video"]
    app.api(scene_b + "/video", {"confirm": True, "providerId": b})
    second_take = wait(lambda: (s := scene(flow, 1)).get("video") != independent["video"] and s.get("video") and s)
    assert len(second_take["takes"]) == 2 and len(scene(flow)["takes"]) == 1
    app.api(flow + "/render", {"subtitles": True})
    exported = wait(lambda: (s := app.api(flow)).get("exports") and not s.get("rendering") and s, 65)
    file = flow_dir / flow_ids[0] / exported["exports"][0]["path"]
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "stream=codec_name,codec_type:format=duration",
                            "-of", "json", str(file)], check=True, capture_output=True, text=True)
    details = json.loads(probe.stdout)
    assert {s["codec_name"] for s in details["streams"]} == {"h264", "aac"}
    assert 7.9 <= float(details["format"]["duration"]) <= 8.2
    shutil.copyfile(file, evidence / "episodio-de-ensaio.mp4")
    return {"known_operation": known["operation"], "original_unavailable_requests": 0,
            "unknown_operation_provider": b, "unknown_submissions": 1,
            "independent_scene_origin": independent["videoOrigin"], "first_scene_takes": 1, "second_scene_takes": 2,
            "export": details, "connector_calls": [e for e in fixture.events if "method" in e]}


def verify_assisted(app, browser, evidence):
    app.launch()
    assert not app.api("/state")["laboratorio"]
    a = app.profile("Perfil A informado", "a@example.test")
    browser.code(f"""
        await page.goto('{app.base}/');
        if (!(await page.locator('#execution-mode option[value=alternar]').evaluate(el => el.disabled)))
            throw Error('Ensaio habilitado em produção.');
        await page.locator('#execution-mode').selectOption('assistido');
        await page.locator('#home-add-google').click();
        await page.locator('#google-name').fill('Perfil B informado');
        await page.locator('#google-identity').fill('b@example.test');
        await page.getByText('Salvar perfil', {{exact: true}}).click();
        await page.locator('#google-profile-form').waitFor({{state: 'hidden'}});
    """)
    b = next(p["id"] for p in app.api("/state")["settings"]["providers"] if p.get("declaredIdentity") == "b@example.test")
    pid = app.api("/projects", {"name": "Projeto assistido", "kind": "codigo"})["id"]
    app.api(f"/projects/{pid}/threads", {"text": "modo bloqueado", "execution": "alternar", "accounts": [a, b]}, status=400)
    browser.code(f"""
        await page.reload();
        await page.locator('#execution-mode').selectOption('assistido');
        await page.locator('#execution-account').selectOption('{a}');
        await page.locator('#home-project').selectOption('{pid}');
        await page.locator('#home-prompt').fill('Continuar este projeto no cliente oficial.');
        await page.locator('#home-send').click();
        await page.getByText('Aguardando operador', {{exact: true}}).waitFor();
        await page.getByText('Registrar progresso', {{exact: true}}).click();
        await page.locator('#receipt-tests').fill('Exemplo local de teste e decisão declarados.');
        await page.locator('#receipt-next').fill('Continuar com o perfil B na mesma pasta.');
        await page.locator('#receipt-profile').selectOption('{b}');
        await page.getByText('Salvar progresso', {{exact: true}}).click();
        await page.getByText('Continuar com o perfil B na mesma pasta.', {{exact: false}}).first().waitFor();
        await page.screenshot({{path: {json.dumps(str(evidence / "google-pessoal-assistido.png"))}}});
    """)
    tid = app.api(f"/projects/{pid}")["threads"][0]["id"]
    package = app.api(f"/projects/{pid}/threads/{tid}/checkpoint")
    assert package["profile"] == b and package["task"] == tid and package["project"] == pid
    assert package["history"][0]["requested"] == a and not package["history"][0].get("effective")
    return {"project": pid, "task": tid, "operator_declared_profiles": 2, "attested_identities": 0,
            "continuation_uses_same_workspace": package["workspace"] == app.api(f"/projects/{pid}")["workspace"]}


def run_main(package, evidence, legacy, expected_version):
    with tempfile.TemporaryDirectory(prefix="centralia-rotacao-") as directory:
        work = Path(directory)
        browser = Browser(work)
        fixture = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
        threading.Thread(target=fixture.serve_forever, daemon=True).start()
        endpoint = f"http://127.0.0.1:{fixture.server_port}/"
        app = App(work, package, endpoint, "laboratorio")
        old = App(work, package, None, "anterior")
        evidence.mkdir(parents=True, exist_ok=True)
        try:
            old.launch(legacy)
            assert old.api("/state")["version"] == "2.0.0"
            saved_project = old.api("/projects", {"name": "Projeto da versão anterior", "kind": "codigo"})["id"]
            saved_thread = old.api(f"/projects/{saved_project}/threads", {"mode": "agente"})["id"]
            collision = subprocess.run(old.command, cwd=package, env=old.env, capture_output=True, timeout=10)
            assert collision.returncode == 1 and b"Outra inst" in collision.stderr
            assert old.api("/state")["version"] == "2.0.0"
            old.close()
            old.port = free_pair()
            old.base = f"http://127.0.0.1:{old.port}"
            old.env["CENTRAL_PORT"] = str(old.port)
            app.launch()
            assert app.api("/state")["version"] == expected_version
            assert app.api("/state")["laboratorio"]
            a, b, project, sequencing = program(app, browser, Fixture, evidence)
            print("Programação A/B e reinício verificados", flush=True)
            checks = recovery(app, Fixture, a, b)
            print("Fixação e timeout em disco verificados", flush=True)
            media = verify_video(app, Fixture, endpoint, project, browser, evidence)
            assisted = verify_assisted(old, browser, evidence)
            assert old.thread(saved_project, saved_thread)["id"] == saved_thread
            result = {"version": expected_version, "connector": "simulador HTTP local", "real_google_calls": 0,
                      "launcher": {"legacy": "2.0.0", "legacy_collision_exit": collision.returncode, "new": expected_version,
                                   "legacy_project_and_thread_preserved": True},
                      "programming": sequencing, "recovery": checks, "video": media, "assisted": assisted}
            (evidence / "resultados.json").write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            print("Vídeo, modo assistido e evidências verificados", flush=True)
        finally:
            app.close()
            old.close()
            browser.run("close")
            fixture.shutdown()
            fixture.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=lambda p: Path(p).resolve(), required=True)
    parser.add_argument("--evidence", type=lambda p: Path(p).resolve(), required=True)
    parser.add_argument("--legacy", type=lambda p: Path(p).resolve(), required=True)
    parser.add_argument("--version", default="2.1.0")
    parsed = parser.parse_args()
    run_main(parsed.package, parsed.evidence, parsed.legacy, parsed.version)
