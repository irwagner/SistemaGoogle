"""Verifica o Orbita pelo navegador com um CLI ACP de ensaio e Git real."""
import argparse
import base64
import json
import os
import re
import runpy
import secrets
import shutil
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from http.server import ThreadingHTTPServer

utilities = runpy.run_path(str(Path(__file__).with_name("verificar-rotacao-centralia.py")))
Browser, free_pair, wait = (utilities[key] for key in ("Browser", "free_pair", "wait"))
pool_fixtures = runpy.run_path(str(Path(__file__).with_name("verificar-account-pool-centralia.py")))


def verify_pool_worker(app):
    PoolFixture = pool_fixtures["PoolFixture"]
    raw_request = pool_fixtures["raw_request"]
    verification_url = "https://accounts.google.com/v3/signin/continue?flowName=orbita-ensaio"

    class RotationFixture(PoolFixture):
        verification_required = True

        def do_POST(self):
            if (self.path == "/v1internal:retrieveUserQuotaSummary" and self.verification_required
                    and self.headers.get("Authorization") == "Bearer fixture-b"):
                self.rfile.read(int(self.headers["Content-Length"]))
                self.reply(403, {"error": {"details": [{"reason": "VALIDATION_REQUIRED",
                    "metadata": {"validation_url": verification_url}}]}})
                return
            super().do_POST()

    server = ThreadingHTTPServer(("127.0.0.1", 0), RotationFixture)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    with tempfile.TemporaryDirectory(prefix="orbita-pool-") as directory:
        root = Path(directory)
        key, lease = secrets.token_hex(32), secrets.token_hex(16)
        env = {**os.environ, "CENTRALIA_POOL_DATA": str(root), "CENTRALIA_POOL_KEY": key,
               "CENTRALIA_POOL_TEST_URL": f"http://127.0.0.1:{server.server_port}/"}
        log = root / "worker.log"
        with log.open("wb") as output:
            worker = subprocess.Popen(["node", str(app.parent / "AntigravityPool" / "server.mjs")], env=env,
                                       cwd=root, stdin=subprocess.PIPE, stdout=output, stderr=output,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            def ready():
                output = log.read_text(encoding="utf-8", errors="replace")
                assert worker.poll() is None, output
                match = re.search(r"CENTRALIA_POOL_PORT:(\d+)", output)
                return int(match[1]) if match else None

            port = wait(ready)

            def admin(path, value=None):
                headers = {"Content-Type": "application/json", "X-Agy-Pool-Key": key}
                data = None if value is None else json.dumps(value).encode()
                req = urllib.request.Request(f"http://127.0.0.1:{port}/api/{path}", data, headers)
                with urllib.request.urlopen(req, timeout=10) as response:
                    return json.load(response)

            admin("accounts/add", {})
            accounts = wait(lambda: rows if len(rows := admin("status")["accounts"]) == 2 else None)
            a, b = [row["id"] for row in accounts]
            selection = {"accounts": [a, b], "model": "opus"}
            blocked = admin("diagnostics/rotation", selection)
            assert [row["state"] for row in blocked["checks"]] == ["ready", "verification-required"]
            assert blocked["rotation"] == "blocked" and verification_url not in json.dumps(admin("status"))
            assert admin("accounts/verification", {"id": b})["url"] == verification_url
            RotationFixture.verification_required = False
            checked = admin("diagnostics/rotation", selection)
            assert [row["state"] for row in checked["checks"]] == ["ready", "ready"]
            assert (checked["rotation"], checked["from"], checked["to"]) == ("passed", "Conta 1", "Conta 2")
            assert not PoolFixture.calls, "O teste no painel não envia pedidos ao modelo"
            settings = {"projectId": "orbitaensaio", "path": str(root), "mode": "alternar", "accounts": [a, b], "leaseId": lease}
            assert admin("projects/reserve", settings)["session"]["accountIds"] == [a, b]
            assert not PoolFixture.launches, "A reserva do Orbita não deve abrir outro agente"
            PoolFixture.launches.append({"projectPath": str(root)})
            body = {"model": "claude-opus-5-5-thinking", "session": "orbita-teste", "step": 0}
            for index in (0, 1):
                body["step"] = index
                status, reply = raw_request(port, "/v1internal:streamGenerateContent", body, "fixture-a")
                assert status == 200, reply
                (root / reply["file"]).write_text(reply["code"], encoding="utf-8")
            assert admin("status")["session"]["account"] == b
            assert [(call["effective"], call["session"]) for call in PoolFixture.calls] == [("a", "orbita-teste"), ("a", "orbita-teste"), ("b", "orbita-teste")]
            assert admin("projects/release", {"leaseId": "0" * 32})["session"]["account"] == b
            assert admin("projects/release", {"leaseId": lease})["session"] is None
            print("Account Pool: verificação por conta, teste sem geração e troca na mesma sessão verificados.")
        finally:
            worker.stdin.close()
            try:
                worker.wait(timeout=8)
            except subprocess.TimeoutExpired:
                worker.kill()
                worker.wait(timeout=5)
            server.shutdown()
            server.server_close()


def verify_chat(browser, evidence, mission_dialog_open=False):
    if not mission_dialog_open:
        browser.code("""
            await page.getByRole('button',{name:'Simples',exact:true}).click();
            await page.getByRole('button',{name:'Nova missão',exact:true}).click();
        """)
    browser.code(f"""
        await page.getByLabel('Nome da missão').fill('Leitura e rolagem do chat');
        await page.getByRole('button',{{name:'Criar worktree'}}).click();
        await page.getByRole('heading',{{name:'Leitura e rolagem do chat',exact:true}}).waitFor();
        await page.getByRole('dialog').waitFor({{state:'hidden'}});
        await page.getByLabel('Mensagem para o Antigravity').fill('Ensaio de leitura do chat');
        await page.getByRole('button',{{name:'Enviar',exact:true}}).click();
        await page.getByRole('heading',{{name:'Resposta organizada',exact:true}}).waitFor();
        await page.locator('.message-body strong').getByText('Resultado parcial').waitFor();
        await page.locator('.message-body li').getByText('Teste executado').waitFor();
        await page.locator('.message-body pre code').getByText("console.log('ok');").waitFor();
        if (await page.locator('.message-body img').count()) throw new Error('HTML da resposta foi interpretado.');
        const scroll = page.locator('.workbench-body');
        const atEnd = () => page.waitForFunction(() => {{ const el = document.querySelector('.workbench-body'); return el.scrollTop > 0 && el.scrollHeight - el.clientHeight - el.scrollTop <= 3; }});
        await atEnd();
        await page.locator('.message.user').evaluate(el => {{ el.dataset.prova = 'mantida'; }});
        const before = await scroll.evaluate(el => {{ el.scrollTop = 100; return el.scrollTop; }});
        await page.getByRole('button',{{name:/Ir para a última mensagem/}}).waitFor();
        await page.getByText('Chegou outra parte da resposta.',{{exact:true}}).waitFor();
        if (Math.abs(await scroll.evaluate(el => el.scrollTop) - before) > 3) throw new Error('O chat deslocou a leitura durante o streaming.');
        if (await page.locator('.message.user').getAttribute('data-prova') !== 'mantida') throw new Error('O histórico foi recriado ao receber texto.');
        await page.getByRole('tab',{{name:'Atividade',exact:true}}).click();
        await page.getByRole('tab',{{name:'Conversa',exact:true}}).click();
        if (Math.abs(await scroll.evaluate(el => el.scrollTop) - before) > 3) throw new Error('O chat perdeu a posição ao trocar de aba.');
        await page.getByRole('button',{{name:/Ir para a última mensagem/}}).click();
        await page.getByText('Fim do streaming.',{{exact:true}}).waitFor();
        await atEnd();
        await page.getByRole('button',{{name:/Ir para a última mensagem/}}).waitFor({{state:'hidden'}});
        await scroll.evaluate(el => {{ el.scrollTop = 0; }});
        await page.screenshot({{ path: {json.dumps(str(evidence / 'orbita-chat.png'))} }});
        await page.setViewportSize({{width: 860, height: 720}});
        if (!(await scroll.evaluate(el => el.scrollHeight > el.clientHeight + 100))) throw new Error('A janela estreita perdeu a rolagem interna do chat.');
        await page.getByRole('button',{{name:/Ir para a última mensagem/}}).click();
        await atEnd();
        await page.setViewportSize({{width: 1440, height: 900}});
    """)


def verify_desktop(browser, root):
    browser.code("""
        await page.getByLabel('Nome da missão').fill('Abertura na área de trabalho');
        await page.getByRole('button',{name:'Criar worktree'}).click();
        await page.getByRole('heading',{name:'Abertura na área de trabalho',exact:true}).waitFor();
        await page.getByRole('dialog').waitFor({state:'hidden'});
        await page.getByLabel('Mensagem para o Antigravity').fill('Planeje uma soma');
        await page.getByRole('button',{name:'Enviar',exact:true}).click();
        await page.getByText('Proponho criar uma função soma e testar o resultado.',{exact:true}).waitFor();
        await page.locator('#mission-state').getByText('Pronta').waitFor();
    """)
    for path in (root / "dados" / "orbita" / "missoes").glob("*/missao.json"):
        mission = json.loads(path.read_text(encoding="utf-8"))
        if mission["name"] == "Abertura na área de trabalho":
            app_path = Path(mission["workspace"]) / "orbita-launch-probe.exe"
            shutil.copyfile(Path(os.environ["SystemRoot"]) / "System32" / "where.exe", app_path)
            break
    else:
        raise AssertionError("Missão de teste do desktop indisponível")
    browser.code("""
        await page.getByRole('button',{name:'Permissão total',exact:true}).click();
        await page.getByLabel('Mensagem para o Antigravity').fill('Ensaio de abrir app no Windows');
        await page.getByRole('button',{name:'Enviar',exact:true}).click();
        await page.locator('#conversation').getByText(/orbita-launch-probe.exe foi iniciado/).waitFor();
        await page.getByRole('tab',{name:'Atividade',exact:true}).click();
        await page.locator('#live-log').getByText(/orbita-launch-probe.exe foi iniciado/).waitFor();
        await page.getByRole('button',{name:'Abrir aplicativo',exact:true}).click();
        await page.getByLabel('Caminho do executável (.exe)').fill('arquivo-inexistente.exe');
        await page.getByRole('button',{name:'Abrir no Windows',exact:true}).click();
        await page.locator('#toast').getByText(/Informe um arquivo .exe dentro do worktree/).waitFor();
        await page.getByLabel('Caminho do executável (.exe)').fill('orbita-launch-probe.exe');
        await page.getByRole('button',{name:'Abrir no Windows',exact:true}).click();
        await page.locator('#toast').getByText(/orbita-launch-probe.exe foi iniciado/).waitFor();
    """)


def attachment_samples(root):
    png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=")
    (root / "captura.png").write_bytes(png)
    (root / "extrato.csv").write_text("nome,valor\nCSV-SENTINELA,40\n", encoding="utf-8")
    (root / "remover.txt").write_text("Este arquivo será retirado.", encoding="utf-8")
    (root / "invalido.png").write_bytes(b"arquivo que foi renomeado para png")
    text = b"BT /F1 12 Tf 72 720 Td (PDF-SENTINELA) Tj ET"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        f"<< /Length {len(text)} >>\nstream\n".encode() + text + b"\nendstream",
    ]
    pdf, offsets = bytearray(b"%PDF-1.4\n"), []
    for index, entry in enumerate(objects, 1):
        offsets.append(len(pdf))
        pdf += f"{index} 0 obj\n".encode() + entry + b"\nendobj\n"
    table = len(pdf)
    pdf += b"xref\n0 6\n0000000000 65535 f \n" + b"".join(f"{offset:010} 00000 n \n".encode() for offset in offsets)
    pdf += f"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{table}\n%%EOF\n".encode()
    (root / "proposta.pdf").write_bytes(pdf)
    with zipfile.ZipFile(root / "requisitos.docx", "w") as word:
        word.writestr("word/document.xml", '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>DOCX-SENTINELA</w:t></w:r></w:p></w:body></w:document>')
    with zipfile.ZipFile(root / "planilha.xlsx", "w") as excel:
        excel.writestr("xl/workbook.xml", '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Ensaio" sheetId="1" r:id="rId1"/></sheets></workbook>')
        excel.writestr("xl/_rels/workbook.xml.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>')
        excel.writestr("xl/worksheets/sheet1.xml", '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>XLSX-SENTINELA</t></is></c><c r="B1"><v>30</v></c></row></sheetData></worksheet>')
    return png


def verify_attachments(browser, root):
    png = attachment_samples(root)
    names = ["captura.png", "extrato.csv", "proposta.pdf", "requisitos.docx", "planilha.xlsx", "remover.txt"]
    selected = json.dumps([str(root / name) for name in names])
    browser.code(f"""
        await page.getByLabel('Nome da missão').fill('Anexos na conversa');
        await page.getByRole('button',{{name:'Criar worktree'}}).click();
        await page.getByRole('heading',{{name:'Anexos na conversa',exact:true}}).waitFor();
        await page.getByRole('dialog').waitFor({{state:'hidden'}});
        await page.locator('#file-picker').setInputFiles({selected});
        await page.getByRole('button',{{name:'Remover remover.txt',exact:true}}).click();
        await page.locator('#attachment-draft').getByText('captura.png',{{exact:true}}).waitFor();
        await page.getByLabel('Mensagem para o Antigravity').fill('Ensaio de arquivos anexados');
        await page.getByRole('button',{{name:'Enviar',exact:true}}).click();
        await page.locator('#conversation').getByText('Imagem e quatro documentos recebidos.',{{exact:true}}).waitFor();
        await page.locator('#attachment-draft').waitFor({{state:'hidden'}});
        await page.reload();
        await page.locator('#conversation').getByRole('button',{{name:'captura.png',exact:true}}).click();
        await page.getByRole('dialog').getByRole('img',{{name:'captura.png'}}).waitFor();
        await page.waitForFunction(() => document.querySelector('.attachment-preview').naturalWidth === 1);
        await page.getByRole('button',{{name:'Fechar',exact:true}}).click();
        const [download] = await Promise.all([
            page.waitForEvent('download'),
            page.locator('#conversation').getByRole('button',{{name:'proposta.pdf',exact:true}}).click()
        ]);
        if (download.suggestedFilename() !== 'proposta.pdf') throw new Error('Nome do PDF não foi preservado.');
        const focus = page.getByLabel('Mensagem para o Antigravity');
        await focus.evaluate((node, bytes) => {{
            const data = new DataTransfer();
            data.items.add(new File([Uint8Array.from(bytes)], 'colada.png', {{type:'image/png'}}));
            node.dispatchEvent(new ClipboardEvent('paste', {{clipboardData:data,bubbles:true,cancelable:true}}));
        }}, {json.dumps(list(png))});
        await page.locator('#attachment-draft').getByText('colada.png',{{exact:true}}).waitFor();
        await page.getByRole('button',{{name:'Enviar',exact:true}}).click();
        await page.locator('#conversation').getByText('Captura de tela recebida.',{{exact:true}}).waitFor();
        await page.locator('#mission-state').getByText('Pronta').waitFor();
        await page.locator('#file-picker').setInputFiles({json.dumps(str(root / 'invalido.png'))});
        await page.getByRole('button',{{name:'Enviar',exact:true}}).click();
        await page.locator('#toast').getByText(/imagem invalido.png está inválida/).waitFor();
        await page.locator('#attachment-draft').getByText('invalido.png',{{exact:true}}).waitFor();
        await page.getByRole('button',{{name:'Remover invalido.png',exact:true}}).click();
        await page.locator('#composer').evaluate(node => {{
            const data = new DataTransfer();
            data.items.add(new File(['nome,valor\\nCSV-SENTINELA,10\\n'], 'retomada.csv', {{type:'text/csv'}}));
            node.dispatchEvent(new DragEvent('drop', {{dataTransfer:data,bubbles:true,cancelable:true}}));
        }});
        await page.locator('#attachment-draft').getByText('retomada.csv',{{exact:true}}).waitFor();
        await focus.fill('Ensaio de retomar com anexo');
        await page.getByRole('button',{{name:'Executar',exact:true}}).click();
        await page.locator('#mission-state').getByText('Precisa de você').waitFor();
        await page.reload();
        await page.getByRole('button',{{name:'Continuar de onde parei'}}).click();
        await page.getByRole('button',{{name:'Retomar pedido pendente'}}).click();
        await page.locator('#conversation').getByText('Retomei com o CSV original.',{{exact:true}}).waitFor();
        await page.locator('#mission-state').getByText('Concluída').waitFor();
    """)
    mission = next((root / "dados" / "orbita" / "missoes").glob("*/missao.json"))
    saved = json.loads(mission.read_text(encoding="utf-8"))
    workspace = Path(saved["workspace"])
    assert not list(workspace.glob("*.png"))
    assert not saved["pendingAttachments"]
    assert len(list((mission.parent / "anexos").glob("*/anexo.json"))) == 7


def verify_projects(browser, root):
    first = root / "Projeto sem commit"
    empty = root / "Projeto sem índice"
    for directory in (first, empty):
        directory.mkdir()
        subprocess.run(["git", "init", "--initial-branch=main", str(directory)], check=True, capture_output=True)
        (directory / "rascunho.txt").write_text("Rascunho da pasta original", encoding="utf-8")
    tracked = first / "escopo.txt"
    tracked.write_text("Escopo preparado", encoding="utf-8")
    subprocess.run(["git", "-C", str(first), "add", "--", tracked.name], check=True, capture_output=True)
    index = (first / ".git" / "index").read_bytes()
    tracked.write_text("Edição posterior na pasta original", encoding="utf-8")
    browser.code(f"""
        await page.getByLabel('Nome da missão').fill('Missão do projeto original');
        await page.getByRole('button',{{name:'Criar worktree'}}).click();
        await page.getByRole('heading',{{name:'Missão do projeto original',exact:true}}).waitFor();
        await page.getByRole('dialog').waitFor({{state:'hidden'}});
        await page.getByRole('tab',{{name:'Alterações',exact:true}}).click();
        const newProject = async (name, directory) => {{
            await page.getByRole('button',{{name:'Novo projeto ou pasta',exact:true}}).click();
            await page.getByLabel('Nome do projeto').fill(name);
            await page.getByLabel(/Pasta Git existente/).fill(directory);
            await page.getByRole('dialog').getByRole('button',{{name:'Criar projeto',exact:true}}).click();
            await page.getByRole('dialog').getByText('Projeto ' + name,{{exact:true}}).waitFor();
        }};
        await newProject('NotaFiscal de ensaio', {json.dumps(str(first))});
        await page.getByRole('button',{{name:'Fechar',exact:true}}).click();
        const projects = page.getByLabel('Projeto',{{exact:true}});
        await projects.selectOption({{label:'Orbita · ensaio local'}});
        await page.getByRole('button',{{name:/Missão do projeto original/}}).click();
        await page.getByRole('tab',{{name:'Alterações',exact:true}}).click();
        await projects.selectOption({{label:'NotaFiscal de ensaio'}});
        await page.getByRole('button',{{name:'Criar missão',exact:true}}).click();
        await page.getByRole('dialog').getByText('Projeto NotaFiscal de ensaio',{{exact:true}}).waitFor();
        await page.getByLabel('Nome da missão').fill('Primeira missão no projeto vinculado');
        await page.getByRole('button',{{name:'Criar worktree'}}).click();
        await page.getByRole('heading',{{name:'Primeira missão no projeto vinculado',exact:true}}).waitFor();
        await page.getByRole('dialog').waitFor({{state:'hidden'}});
        await newProject('Git recém-iniciado', {json.dumps(str(empty))});
        await page.getByLabel('Nome da missão').fill('Primeira missão sem arquivos preparados');
        await page.getByRole('button',{{name:'Criar worktree'}}).click();
        await page.getByRole('heading',{{name:'Primeira missão sem arquivos preparados',exact:true}}).waitFor();
        await page.getByRole('dialog').waitFor({{state:'hidden'}});
    """)
    missions = {entry["name"]: entry for path in (root / "dados" / "orbita" / "missoes").glob("*/missao.json")
                if (entry := json.loads(path.read_text(encoding="utf-8")))}
    staged_copy = Path(missions["Primeira missão no projeto vinculado"]["workspace"])
    empty_copy = Path(missions["Primeira missão sem arquivos preparados"]["workspace"])
    assert (staged_copy / tracked.name).read_text(encoding="utf-8") == "Escopo preparado"
    assert not (staged_copy / "rascunho.txt").exists()
    assert {path.name for path in empty_copy.iterdir()} == {".git"}
    assert tracked.read_text(encoding="utf-8") == "Edição posterior na pasta original"
    assert (first / ".git" / "index").read_bytes() == index
    for directory in (first, empty):
        assert (directory / ".git" / "HEAD").read_text(encoding="utf-8").strip() == "ref: refs/heads/main"
        assert subprocess.run(["git", "-C", str(directory), "rev-parse", "--verify", "--quiet", "HEAD"], capture_output=True).returncode == 1


def verify(app, fixture, evidence, chat_only=False, desktop_only=False, attachments_only=False, projects_only=False):
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="orbita-ui-") as work:
        root = Path(work)
        port = free_pair()
        base = f"http://127.0.0.1:{port}"
        browser = Browser(root)
        env = {**os.environ, "CENTRAL_DATA": str(root / "dados"), "ORBITA_PORT": str(port),
               "ORBITA_AGY_PATH": str(fixture), "ORBITA_NO_BROWSER": "1"}
        log = root / "orbita.log"
        with log.open("wb") as output:
            server = subprocess.Popen([str(app), "--sem-navegador"], env=env, cwd=app.parent,
                                      stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            def ready():
                if server.poll() is not None:
                    raise AssertionError(log.read_text(encoding="utf-8", errors="replace"))
                try:
                    with urllib.request.urlopen(base, timeout=2) as response:
                        return re.search(r'name="orbita-token" content="([a-f0-9]{64})"', response.read().decode())[1]
                except (OSError, urllib.error.URLError):
                    return None

            token = wait(ready)
            request = urllib.request.Request(base + "/api/state")
            try:
                urllib.request.urlopen(request, timeout=2)
                raise AssertionError("API exposta sem autenticação local")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            browser.run("open", base, "--browser=msedge")
            if chat_only or desktop_only or attachments_only or projects_only:
                browser.code("""
                    await page.setViewportSize({width:1440,height:900});
                    await page.getByRole('button',{name:'Criar projeto',exact:true}).click();
                    await page.getByLabel('Nome do projeto').fill('Orbita · ensaio local');
                    await page.getByRole('dialog').getByRole('button',{name:'Criar projeto',exact:true}).click();
                """)
                if projects_only:
                    verify_projects(browser, root)
                    print("Orbita Windows: troca de projeto e criação de missões em Git sem commit verificadas; índice e originais preservados.")
                elif desktop_only:
                    verify_desktop(browser, root)
                    print("Orbita Windows: abertura via MCP, autorização por turno e botão direto verificados.")
                elif attachments_only:
                    verify_attachments(browser, root)
                    print("Orbita Windows: imagem ACP, texto de PDF, Word, Excel e CSV, colagem e histórico verificados.")
                else:
                    verify_chat(browser, evidence, mission_dialog_open=True)
                    print("Orbita Windows: formatação segura e rolagem do chat verificadas em streaming.")
                return
            browser.code(r"""
                await page.setViewportSize({width: 1440, height: 900});
                await page.getByRole('button', {name:'Criar projeto', exact:true}).click();
                await page.getByLabel('Nome do projeto').fill('Orbita · exemplo local');
                await page.getByRole('dialog').getByRole('button',{name:'Criar projeto', exact:true}).click();
                await page.getByLabel('Nome da missão').fill('Criar e testar uma função de soma');
                await page.getByRole('button',{name:'Criar worktree'}).click();
                await page.getByRole('heading',{name:'Criar e testar uma função de soma'}).waitFor();
                await page.getByRole('dialog').waitFor({state:'hidden'});
                if (await page.getByRole('button',{name:'Autônomo',exact:true}).getAttribute('aria-pressed') !== 'true') throw new Error('Autônomo deveria ser o padrão.');
                await page.getByRole('tab',{name:'Conversa',exact:true}).waitFor();
                await page.getByRole('complementary',{name:'Console Antigravity'}).waitFor({state:'hidden'});
                await page.getByLabel('Mensagem para o Antigravity').fill('Rascunho que deve continuar no chat.');
                await page.getByRole('button',{name:'Avançado',exact:true}).click();
                await page.getByRole('complementary',{name:'Console Antigravity'}).waitFor();
                if (await page.getByRole('button',{name:'Aprovar Plano',exact:true}).count()) throw new Error('A interface voltou a pedir aprovação do plano.');
                await page.getByRole('button',{name:'Simples',exact:true}).click();
                if (await page.getByLabel('Mensagem para o Antigravity').inputValue() !== 'Rascunho que deve continuar no chat.') throw new Error('Alternar layout descartou o rascunho.');
                await page.getByRole('button',{name:'Avançado',exact:true}).click();
                await page.getByRole('button',{name:'SPEC.md'}).click();
                await page.getByLabel('Conteúdo do arquivo').fill('# Ensaio ACP\nA soma precisa retornar 5.');
                await page.getByRole('button',{name:'Salvar', exact:true}).click();
                await page.keyboard.press('Control+k');
                await page.getByText('SKILL.md no próximo pedido').waitFor();
                await page.locator('#model').selectOption('neon');
                await page.getByLabel('Mensagem para o Antigravity').fill('Planeje criar uma soma pequena em JavaScript.');
                await page.getByRole('button',{name:'Enviar', exact:true}).click();
                await page.getByText('Proponho criar uma função soma', {exact:false}).waitFor();
                await page.getByRole('button',{name:'Enviar', exact:true}).waitFor();
                await page.getByLabel('Mensagem para o Antigravity').fill('Pode fazer.');
                await page.getByRole('button',{name:'Enviar', exact:true}).click();
                await page.locator('#active-account').getByText('Login direto do CLI', {exact:true}).waitFor();
                await page.getByRole('button',{name:'Pool em espera',exact:true}).waitFor();
                await page.getByRole('button',{name:'Simples',exact:true}).click();
                await page.locator('#active-account').getByText('Login direto do CLI', {exact:true}).waitFor();
                await page.locator('#mission-state').getByText('Concluída').waitFor();
                await page.locator('#active-account').getByText('Sem execução ativa', {exact:true}).waitFor();
                await page.getByText('Criei soma.mjs e o teste passou.').waitFor();
                await page.getByRole('tab',{name:'Alterações',exact:true}).click();
                await page.locator('#diff-code').getByText('export const soma', {exact:false}).waitFor();
                await page.getByRole('button',{name:'Avançado',exact:true}).click();
                await page.getByRole('tab',{name:'Diff & Evidências'}).click();
                await page.locator('#diff-code').getByText('export const soma', {exact:false}).waitFor();
                await page.locator('#evidence-list').getByText('Teste Node passou').waitFor();
                await page.getByRole('tab',{name:'Ao Vivo'}).click();
                await page.locator('#live-log').getByText('Snapshot anterior à execução guardado.').waitFor();
            """)
            review_output = browser.code(f"""
                await page.reload();
                await page.getByRole('heading',{{name:'Criar e testar uma função de soma'}}).waitFor();
                await page.getByText('Criei soma.mjs e o teste passou.').waitFor();
                if (await page.getByText('Mensagem já recebida').count()) throw new Error('O histórico foi duplicado.');
                await page.getByRole('button',{{name:'Simples',exact:true}}).click();
                await page.reload();
                await page.getByRole('tab',{{name:'Conversa',exact:true}}).waitFor();
                await page.getByText('Criei soma.mjs e o teste passou.').waitFor();
                if (await page.getByRole('button',{{name:'Simples',exact:true}}).getAttribute('aria-pressed') !== 'true') throw new Error('O layout simples não foi lembrado.');
                await page.screenshot({{ path: {json.dumps(str(evidence / 'orbita-simples.png'))} }});
                await page.setViewportSize({{width: 430, height: 850}});
                if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('O layout simples ultrapassou a janela.');
                await page.setViewportSize({{width: 1440, height: 900}});
                await page.getByRole('button',{{name:'Avançado',exact:true}}).click();
                await page.getByRole('tab',{{name:'Diff & Evidências'}}).click();
                await page.locator('#diff-code').getByText('export const soma', {{exact:false}}).waitFor();
                await page.screenshot({{ path: {json.dumps(str(evidence / 'orbita-avancado.png'))} }});
                await page.getByRole('button',{{name:'Aceitar Diff'}}).click();
                await page.locator('#mission-state').getByText('Diff aceito').waitFor();
                await page.getByRole('button',{{name:'Continuar de onde parei'}}).click();
                await page.getByRole('dialog').getByText(/Diff aceito/).waitFor();
                await page.getByRole('button',{{name:'Fechar',exact:true}}).click();
                await page.getByRole('button',{{name:'Reverter',exact:true}}).click();
                await page.getByRole('button',{{name:'Reverter missão',exact:true}}).click();
                await page.locator('#mission-state').getByText('Revertida').waitFor();
                await page.getByText('Worktree limpo.').waitFor();
                await page.getByRole('button',{{name:'Pool em espera'}}).click();
                await page.getByRole('button',{{name:'Iniciar pool Google'}}).waitFor();
                await page.getByRole('button',{{name:'Fechar',exact:true}}).click();
                await page.getByRole('button',{{name:'Nova missão',exact:true}}).click();
                await page.getByLabel('Nome da missão').fill('Intervenção e execução autônoma');
                await page.getByRole('button',{{name:'Criar worktree'}}).click();
                await page.getByRole('heading',{{name:'Intervenção e execução autônoma',exact:true}}).waitFor();
                await page.getByRole('button',{{name:'Permissão total',exact:true}}).click();
                await page.getByLabel('Mensagem para o Antigravity').fill('Intervenção de ensaio');
                await page.getByRole('button',{{name:'Executar',exact:true}}).click();
                await page.locator('#mission-state').getByText('Precisa de você').waitFor();
                await page.locator('#intervention').getByText('informe o nome de teste.').waitFor();
                await page.locator('#console-log').getByText('CLI de ensaio · permissão total').waitFor();
                await page.getByRole('button',{{name:'Autônomo',exact:true}}).click();
                await page.getByLabel('Mensagem para o Antigravity').fill('Use Teste como nome e continue.');
                await page.getByRole('button',{{name:'Enviar',exact:true}}).click();
                await page.locator('#mission-state').getByText('Concluída').waitFor();
                await page.locator('#console-log').getByText('CLI de ensaio · auto com sandbox').waitFor();
            """)
            browser.code("""
                await page.getByRole('button',{name:'Nova missão',exact:true}).click();
                await page.getByLabel('Nome da missão').fill('Diff em permissão total');
                await page.getByRole('button',{name:'Criar worktree'}).click();
                await page.getByRole('heading',{name:'Diff em permissão total',exact:true}).waitFor();
                await page.getByRole('button',{name:'Permissão total',exact:true}).click();
                await page.getByLabel('Mensagem para o Antigravity').fill('Crie o arquivo com permissão total e conclua.');
                await page.getByRole('button',{name:'Executar',exact:true}).click();
                await page.locator('#mission-state').getByText('Concluída').waitFor();
                await page.getByRole('tab',{name:'Diff & Evidências'}).click();
                await page.locator('#diff-status').getByText('Diff aceito',{exact:false}).waitFor();
                await page.locator('#console-log').getByText('Diff aceito automaticamente · Permissão total.').waitFor();
                await page.getByRole('button',{name:'Aceitar Diff',exact:true}).waitFor({state:'hidden'});
                if (!(await page.getByRole('button',{name:'Reverter',exact:true}).isEnabled())) throw new Error('O diff aceito precisa continuar reversível.');
            """)
            verify_chat(browser, evidence)
            registry = root / "dados" / "antigravity-pool" / "accounts.json"
            registry.parent.mkdir(parents=True)
            registry.write_text(json.dumps({"accounts": [
                {"id": "test-a", "label": "Conta A", "email": "a@example.test"},
                {"id": "test-b", "label": "Conta B", "email": "b@example.test"}
            ]}), encoding="utf-8")
            browser.code("""
                await page.locator('#open-pool').click();
                await page.getByRole('button',{name:'Pool em espera · 2 contas salvas',exact:true}).waitFor();
                await page.getByRole('dialog').getByText('a@example.test',{exact:false}).waitFor();
                await page.getByRole('dialog').getByText('b@example.test',{exact:false}).waitFor();
                await page.getByRole('button',{name:'Iniciar pool Google',exact:true}).waitFor();
            """)
            req = urllib.request.Request(base + "/api/state", headers={"X-Orbita-Token": token})
            with urllib.request.urlopen(req, timeout=4) as response:
                state = json.load(response)
            mission = next(m for m in state["missions"] if m["name"] == "Criar e testar uma função de soma")
            workspace = Path(mission["workspace"])
            assert mission["sessionId"] == "orbita-sessao-ensaio", (mission["sessionId"], mission["state"])
            assert mission["state"] == "revertida", (mission["state"], review_output[:4000])
            assert not (workspace / "soma.mjs").exists(), workspace
            assert not (Path(state["projects"][0]["workspace"]) / "soma.mjs").exists()
            automatic = next(m for m in state["missions"] if m["name"] == "Diff em permissão total")
            assert automatic["state"] == "concluida" and automatic["accepted"] == automatic["revision"] != automatic["baseline"]
            assert state["version"] == "0.3.9"
            print("Orbita Windows: formatação segura e rolagem do chat verificadas em streaming, além das verificações de execução e persistência.")
        except AssertionError:
            for path in (root / "dados" / "orbita" / "missoes").glob("*/missao.json"):
                mission = json.loads(path.read_text(encoding="utf-8"))
                events = path.with_name("eventos.jsonl")
                lines = events.read_text(encoding="utf-8").splitlines() if events.exists() else []
                status = [entry["text"] for line in lines
                          if (entry := json.loads(line))["kind"] == "estado"]
                print(f"{mission['name']} ({mission['state']}): {status[-1] if status else 'sem evento de estado'}", flush=True)
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
                if "Missão " in line and "interrompida" in line:
                    print(line.strip(), flush=True)
            raise
        finally:
            try:
                browser.run("close")
            finally:
                if server.poll() is None:
                    subprocess.run(["taskkill", "/F", "/T", "/PID", str(server.pid)], capture_output=True, check=True)
                    server.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("app", "fixture", "evidence"):
        parser.add_argument(f"--{name}", type=lambda value: Path(value).resolve(), required=True)
    scope = parser.add_mutually_exclusive_group()
    scope.add_argument("--chat-only", action="store_true")
    scope.add_argument("--desktop-only", action="store_true")
    scope.add_argument("--attachments-only", action="store_true")
    scope.add_argument("--projects-only", action="store_true")
    args = parser.parse_args()
    if not args.chat_only and not args.desktop_only and not args.attachments_only and not args.projects_only:
        verify_pool_worker(args.app)
    verify(args.app, args.fixture, args.evidence, chat_only=args.chat_only, desktop_only=args.desktop_only,
           attachments_only=args.attachments_only, projects_only=args.projects_only)
