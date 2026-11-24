"""Check the reviewed workflow against a loopback OpenAI-compatible fixture."""

import argparse
import json
import runpy
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


common = runpy.run_path(str(Path(__file__).with_name("verificar-rotacao-centralia.py")))
App, Browser, wait = (common[name] for name in ("App", "Browser", "wait"))


class Models(BaseHTTPRequestHandler):
    calls = []
    primary_writes = 0
    recovery = False
    quota_hits = set()

    def log_message(self, *_):
        pass

    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        model = req["model"]
        messages = req["messages"]
        tools = {t["function"]["name"] for t in req.get("tools", [])}
        current = "\n".join(str(m.get("content") or "") for m in messages)
        question = "Responda à última pergunta" in messages[0]["content"]
        planning = question or "preparando um plano" in messages[0]["content"]
        target = "interrupted" if "ensaio interrompido" in current else "quota" if "fila duas contas" in current else "main"
        self.calls.append({"model": model, "planning": planning, "case": target, "tools": sorted(tools)})
        assert self.headers.get("Authorization") == f"Bearer fake-{model.split('-')[-1]}"
        if question:
            assert tools == {"list_files", "read_file", "search_files"}
            self.reply(content="Os testes verificam a função e sua extensão.")
        elif planning:
            assert tools == {"list_files", "read_file", "search_files"}
            text = "1. Criar função base.\n2. Criar extensão e conferir arquivos."
            if "Acrescente uma validação" in current:
                text += "\n3. Acrescentar validação."
            self.reply(content=text)
        elif target == "interrupted":
            if self.recovery:
                self.reply(content="Retomei após reabrir a conversa.")
            else:
                self.reply(content="Resposta parcial para inspeção.", finish=None)
        elif target == "quota":
            if model in self.quota_hits:
                assert model == "gemini-b"
                self.reply(content="Segunda conexão retomou após renovar a cota.")
            else:
                self.quota_hits.add(model)
                self.limit(60 if model == "opus-a" else 6)
        elif model == "opus-a":
            assert "Acrescentar validação" in messages[0]["content"]
            if "call-base" not in json.dumps(messages):
                Models.primary_writes += 1
                self.reply(tool=("call-base", "base.js", "export const soma = (a, b) => a + b;\n"))
            else:
                self.limit(80)
        elif "call-extension" not in json.dumps(messages):
            assert "Criado base.js" in current
            assert "Acrescente uma validação" in current and "Acrescentar validação" in messages[0]["content"]
            assert "call-base" not in json.dumps(messages)
            self.reply(tool=("call-extension", "ext.js", "import { soma } from './base.js';\nexport const dobra = (a, b) => soma(a, b) * 2;\n"))
        elif "call-test" not in json.dumps(messages):
            self.reply(command='node --input-type=module -e "import(\'./ext.js\').then(({dobra})=>{if(dobra(4,5)!==18)process.exit(1);console.log(\'SOMA_AND_DOBRA_OK\');})"')
        else:
            assert "SOMA_AND_DOBRA_OK" in current, current
            self.reply(content="Entrega concluída a partir dos arquivos de A.")

    def limit(self, seconds):
        self.send_response(429)
        self.send_header("Retry-After", str(seconds))
        self.end_headers()
        self.wfile.write(b'{"error":{"message":"rate limit"}}')

    def reply(self, content=None, tool=None, finish="stop", command=None):
        delta = {"content": content}
        if tool is not None or command is not None:
            finish = "tool_calls"
            call_id, name, args = ("call-test", "run_command", {"command": command}) if command else (
                tool[0], "write_file", {"path": tool[1], "content": tool[2]})
            delta = {"tool_calls": [{"index": 0, "id": call_id,
                                    "function": {"name": name, "arguments": json.dumps(args)}}]}
        data = f"data: {json.dumps({'choices': [{'delta': delta, 'finish_reason': finish}]})}\n\n"
        if finish:
            data += "data: [DONE]\n\n"
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        self.wfile.write(data.encode())


def reviewed(app, pid, tid, status):
    return wait(lambda: (w := app.thread(pid, tid)["reviewedWork"])["state"] == status and w)


def verify(package, evidence):
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="centralia-plano-") as directory:
        root = Path(directory)
        fixture = ThreadingHTTPServer(("127.0.0.1", 0), Models)
        threading.Thread(target=fixture.serve_forever, daemon=True).start()
        app = App(root, package, None, "dados")
        browser = Browser(root)
        try:
            app.launch()
            assert app.api("/state")["version"] == "2.2.0"
            names = [("Claude A", "opus-a"), ("Gemini B", "gemini-b"), ("Terceiro fora da ordem", "other-c")]
            routes = []
            for name, model in names:
                result = app.api("/providers", {"name": name, "kind": "openai", "baseUrl": f"http://127.0.0.1:{fixture.server_port}/v1",
                                               "defaultModel": model, "apiKey": f"fake-{model.split('-')[-1]}"})
                routes.append({"providerId": result["providers"][-1]["id"], "model": model})
            pid = app.api("/projects", {"name": "Sistema com revisão", "kind": "codigo"})["id"]
            path = f"/projects/{pid}/threads"
            app.api(path, {"text": "bloqueado", "execution": "revisao", "routes": routes[:2]}, status=400)
            assert not Models.calls
            browser.run("open", app.base, "--browser=msedge")
            browser.code(f"""
                await page.locator('#execution-mode').selectOption('revisao');
                await page.locator('#execution-route').selectOption({{label:'Claude A · opus-a'}});
                await page.locator('#execution-route').selectOption({{label:'Gemini B · gemini-b'}});
                await page.locator('#home-project').selectOption('{pid}');
                await page.locator('#confirm-apis').check();
                await page.locator('#home-prompt').fill('Crie o sistema de ensaio.');
                await page.locator('#home-send').click();
                await page.getByText('Aprovar e executar', {{ exact: true }}).waitFor();
            """)
            tid = app.api(f"/projects/{pid}")["threads"][0]["id"]
            first = reviewed(app, pid, tid, "revisao")
            assert app.api(f"/projects/{pid}/files") == []
            browser.code("""
                await page.locator('#chat-input').fill('O que os testes vão verificar?');
                await page.locator('#btn-ask').click();
                await page.getByText('Os testes verificam a função e sua extensão.', {exact:true}).waitFor();
            """)
            assert reviewed(app, pid, tid, "revisao")["planId"] == first["planId"]
            browser.code(f"""
                await page.locator('#chat-input').fill('Acrescente uma validação');
                await page.locator('#btn-send').click();
                await page.locator('#reviewed-slot').getByText('Acrescentar validação.', {{ exact: true }}).waitFor();
                await page.screenshot({{path:{json.dumps(str(evidence / 'plano-revisado.png'))}}});
            """)
            app.api(f"{path}/{tid}/plan/approve", {"id": first["planId"]}, status=400)
            assert app.api(f"/projects/{pid}/files") == []
            browser.code(f"""
                await page.locator('#approve-plan').click();
                await page.getByText('Permitir comando', {{ exact: true }}).click();
                await page.locator('#reviewed-status').getByText('Concluído', {{ exact: true }}).waitFor();
                await page.locator('[data-ptab=files]').click();
                await page.getByText('ext.js', {{ exact: true }}).click();
                await page.locator('#panel-body').getByText(/dobra/).waitFor();
                await page.screenshot({{path:{json.dumps(str(evidence / 'execucao-a-b.png'))}}});
            """)
            c = reviewed(app, pid, tid, "concluido")
            seq = [event["model"] for event in Models.calls if not event["planning"]]
            assert seq == ["opus-a", "opus-a", "gemini-b", "gemini-b", "gemini-b"] and Models.primary_writes == 1
            assert any(m["role"] == "tool" and m.get("ok") and "SOMA_AND_DOBRA_OK" in m.get("content", "")
                       for m in app.thread(pid, tid)["messages"])
            assert c["approved"] and c["active"] == 1 and c["pauses"]["0"]["reset"]
            assert len(app.api(f"/projects/{pid}")["threads"]) == 1

            interrupted = app.api(path, {"text": "ensaio interrompido", "execution": "revisao", "routes": routes[:2], "confirmApis": True})["id"]
            w = reviewed(app, pid, interrupted, "revisao")
            app.api(f"{path}/{interrupted}/plan/approve", {"id": w["planId"]})
            reviewed(app, pid, interrupted, "pausado")
            paused = app.thread(pid, interrupted)
            assert any("Resposta parcial" in m.get("content", "") for m in paused["messages"] if m["role"] == "notice")
            assert [c["model"] for c in Models.calls if c["case"] == "interrupted" and not c["planning"]] == ["opus-a"]
            app.close()
            app.launch()
            assert reviewed(app, pid, interrupted, "pausado")["approved"]
            Models.recovery = True
            app.api(f"{path}/{interrupted}/plan/resume", {})
            reviewed(app, pid, interrupted, "concluido")

            queued = app.api(path, {"text": "fila duas contas", "execution": "revisao", "routes": routes[:2], "confirmApis": True})["id"]
            w = reviewed(app, pid, queued, "revisao")
            app.api(f"{path}/{queued}/plan/approve", {"id": w["planId"]})
            waiting = reviewed(app, pid, queued, "aguardando-cota")
            assert len(waiting["pauses"]) == 2 and waiting["wake"]
            app.close()
            app.launch()
            reviewed(app, pid, queued, "concluido")
            queue_calls = [c["model"] for c in Models.calls if c["case"] == "quota" and not c["planning"]]
            assert queue_calls == ["opus-a", "gemini-b", "gemini-b"]
            (evidence / "resultados.json").write_text(json.dumps({
                "version": "2.2.0", "real_google_or_claude_api_calls": 0, "project": pid, "task": tid,
                "work": c, "execution_order": seq, "primary_writes": Models.primary_writes,
                "interrupted_task": interrupted, "queued_task": queued, "queue_execution_order": queue_calls, "requests": Models.calls
            }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print("Plano revisado, troca por 429, preservação do chat e retomada verificados.")
        finally:
            app.close()
            browser.run("close")
            fixture.shutdown()
            fixture.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=lambda s: Path(s).resolve())
    parser.add_argument("--evidence", required=True, type=lambda s: Path(s).resolve())
    args = parser.parse_args()
    verify(args.package, args.evidence)
