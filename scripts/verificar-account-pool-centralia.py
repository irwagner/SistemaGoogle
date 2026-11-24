"""Drive the bundled pool through the Central UI with a disposable Windows-login fixture."""

import argparse
import base64
import json
import os
import runpy
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


common = runpy.run_path(str(Path(__file__).with_name("verificar-rotacao-centralia.py")))
App, Browser, wait = (common[n] for n in ("App", "Browser", "wait"))
NATIVE = "gemini:antigravity"


def credential(letter):
    data = {"token": {"access_token": "fixture-" + letter, "expiry": "2099-12-01T12:00:00Z"}}
    return {"UserName": letter, "Persist": 2, "Blob": base64.b64encode(json.dumps(data).encode()).decode()}


class PoolFixture(BaseHTTPRequestHandler):
    credentials = {NATIVE: credential("a")}
    logins = iter(["b", "c"])
    calls = []
    launches = []

    def log_message(self, *_):
        pass

    def reply(self, status, value):
        content = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_GET(self):
        if self.path == "/test/identity":
            letter = (self.headers.get("Authorization") or "")[-1]
            self.reply(200, {"email": f"{letter}@example.test"})
        else:
            self.reply(404, {})

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        if self.path == "/test/bridge":
            action = request["action"]
            name = request["target"]
            if action == "exists":
                value = "true" if name in self.credentials else "false"
            elif action == "copy":
                self.credentials[name] = self.credentials[request["source"]]
                value = "OK"
            elif action == "read":
                value = json.dumps(self.credentials[name])
            elif action == "delete":
                self.credentials.pop(name, None)
                value = "OK"
            else:
                assert action == "write"
                self.credentials[name] = request["payload"]
                value = "OK"
            self.reply(200, {"value": value})
        elif self.path == "/test/launch":
            if request["mode"] == "Login":
                self.credentials[NATIVE] = credential(next(self.logins))
            else:
                self.launches.append(request)
            self.reply(200, {"pid": os.getpid()})
        elif self.path == "/v1internal:retrieveUserQuotaSummary":
            quotas = {"gemini-5h": .72, "gemini-weekly": .43, "3p-5h": .62, "3p-weekly": .51}
            self.reply(200, {"buckets": [{"id": key, "remainingFraction": value} for key, value in quotas.items()]})
        elif self.path == "/v1internal:streamGenerateContent":
            letter = self.headers["Authorization"][-1]
            session = request["session"]
            step = request["step"]
            assert request["model"] == "claude-opus-5-5-thinking"
            self.calls.append({"effective": letter, "session": session, "step": step})
            workspace = Path(self.launches[-1]["projectPath"])
            if step == 0:
                assert letter == "a"
                self.reply(200, {"code": "export const soma = (a, b) => a + b;\n", "file": "base.mjs"})
            elif letter == "a":
                self.reply(429, {"error": {"code": 429, "message": "Individual quota reached. Resets in 5m"}})
            elif letter == "b":
                assert "soma" in (workspace / "base.mjs").read_text(encoding="utf-8")
                self.reply(200, {"code": "import { soma } from './base.mjs';\nexport const dobro = (a, b) => soma(a, b) * 2;\n", "file": "ext.mjs"})
            else:
                self.reply(400, {"error": "Conta não selecionada"})
        else:
            self.reply(404, {})


def raw_request(port, target, data, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    payload = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}{target}", payload, headers)
    try:
        with urllib.request.urlopen(req, timeout=12) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def run(package, evidence):
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="centralia-agy-") as directory:
        root = Path(directory)
        browser = Browser(root)
        server = ThreadingHTTPServer(("127.0.0.1", 0), PoolFixture)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        app = App(root, package, f"http://127.0.0.1:{server.server_port}/", "dados")
        try:
            app.launch()
            assert app.api("/state")["version"] == "2.3.0"
            pid = app.api("/projects", {"name": "Ensaio do Account Pool", "kind": "codigo"})["id"]
            workspace = Path(app.api(f"/projects/{pid}")["workspace"])
            browser.run("open", app.base + "/#/contas", "--browser=msedge")
            browser.code("""
                await page.getByRole('button', {name:'Iniciar pool Google'}).click();
                await page.locator('#antigravity-panel').getByText('a@example.test', {exact:false}).first().waitFor();
                await page.getByRole('button', {name:'Entrar com outra conta Google'}).click();
                await page.locator('#antigravity-panel').getByText('b@example.test', {exact:false}).first().waitFor();
                await page.getByRole('button', {name:'Entrar com outra conta Google'}).click();
                await page.locator('#antigravity-panel').getByText('c@example.test', {exact:false}).first().waitFor();
            """)
            pool = app.api("/antigravity/status")["pool"]
            assert PoolFixture.credentials[NATIVE] == credential("a")
            assert len(PoolFixture.credentials) == 4 and all("recovery" not in x for x in PoolFixture.credentials)
            a, b, c = [account["id"] for account in pool["accounts"]]
            browser.code(f"""
                await page.locator('#agy-project').selectOption('{pid}');
                await page.locator('#agy-choose-account').selectOption('{a}');
                await page.locator('#agy-choose-account').selectOption('{b}');
                await page.locator('#agy-resume').check();
                await page.locator('#agy-launch').click();
                await page.locator('#agy-session').waitFor();
            """)
            launch = PoolFixture.launches[-1]
            assert Path(launch["projectPath"]) == workspace and launch["resume"]
            assert raw_request(launch["port"], "/api/status", None)[0] == 403
            body = {"model": "claude-opus-5-5-thinking", "session": "mesmo-chat-cli", "step": 0}
            assert raw_request(launch["port"], "/v1internal:streamGenerateContent", body, "wrong")[0] == 401
            assert PoolFixture.calls == []
            for i in (0, 1):
                body["step"] = i
                status, reply = raw_request(launch["port"], "/v1internal:streamGenerateContent", body, "fixture-a")
                assert status == 200, (status, reply)
                (workspace / reply["file"]).write_text(reply["code"], encoding="utf-8")
            check = subprocess.run(["node", "--input-type=module", "-e",
                                    "import { dobro } from './ext.mjs'; if (dobro(4,5)!==18) process.exit(1); console.log('18');"],
                                   cwd=workspace, capture_output=True, text=True, check=True)
            assert check.stdout.strip() == "18"
            pool = app.api("/antigravity/status")["pool"]
            assert pool["session"]["projectId"] == pid and pool["session"]["account"] == b
            assert pool["session"]["accountIds"] == [a, b] and c not in pool["session"]["accountIds"]
            assert [(call["effective"], call["step"]) for call in PoolFixture.calls] == [("a", 0), ("a", 1), ("b", 1)]
            assert {call["session"] for call in PoolFixture.calls} == {"mesmo-chat-cli"}
            browser.code(f"""
                await page.locator('.agy-account-grid article').filter({{hasText: 'b@example.test'}})
                    .getByText('Conta ativa', {{exact:true}}).waitFor();
                await page.getByText('Atividade do Antigravity', {{exact:true}}).click();
                await page.getByText(/assumiu claude-opus/).first().waitFor();
                await page.locator('#antigravity-panel').screenshot({{ path: {json.dumps(str(evidence / "pool-a-b.png"))} }});
            """)

            app.close()
            app.launch()
            app.api("/antigravity/start", {})
            status = app.api("/antigravity/status")["pool"]
            assert status["lastProject"]["accountIds"] == [a, b]
            browser.code(f"""
                await page.goto('{app.base}/?verificacao=retomada#/contas');
                await page.locator('#agy-resume:checked').waitFor();
                if (await page.locator('#agy-project').inputValue() !== '{pid}') throw new Error('Projeto não foi restaurado.');
                if (await page.locator('[aria-label="Ordem das contas"] li').count() !== 2) throw new Error('Contas não foram restauradas.');
            """)
            app.api(f"/antigravity/projects/{pid}/launch", {"mode": "fixa", "accounts": [a], "resume": True})
            status, _ = raw_request(PoolFixture.launches[-1]["port"], "/v1internal:streamGenerateContent", body, "fixture-a")
            assert status == 429 and len(PoolFixture.calls) == 3
            files = sorted(p.name for p in workspace.iterdir())
            assert files == ["base.mjs", "ext.mjs"]
            report = {"project": pid, "same_cli_session": "mesmo-chat-cli", "accounts": ["A", "B", "C excluída"],
                      "inference_calls": PoolFixture.calls, "fixed_mode_fallback_calls": 0, "files": files,
                      "node_test_value": 18, "real_google_calls": 0}
            (evidence / "resultados.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            print("Account Pool integrado. Cadastro, troca de A para B, retomada e modo fixado verificados.")
        finally:
            app.close()
            browser.run("close")
            server.shutdown()
            server.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=lambda s: Path(s).resolve())
    parser.add_argument("--evidence", required=True, type=lambda s: Path(s).resolve())
    args = parser.parse_args()
    run(args.package, args.evidence)
