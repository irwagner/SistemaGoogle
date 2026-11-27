"""Exercise the Orbita adapter with the real Hermes runtime and a fixture transport."""

import asyncio
import json
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace
from unittest import TestCase, main
from unittest.mock import AsyncMock, patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / ".orbita-hermes" / "upstream"))
from gateway.config import Platform
from gateway.platforms.base import BasePlatformAdapter
from gateway.run_inbound import GatewayInboundMixin
from plugins.platforms.discord import adapter as discord_adapter
from plugins.platforms.signal.adapter import SignalAdapter
from tui_gateway import (orbita_bridge as bridge, orbita_messaging as messaging, orbita_signal_chat as signal_chat,
                         orbita_signal_files as signal_files, orbita_signal_tools as tools)


MISSION = {"id": "1234567890abcdef", "projectId": "projeto", "name": "Missão de teste", "state": "pronta",
           "updated": "2026-11-26T10:00:00Z"}
LIGHT_MODELS = {"gemini-3.8-flash-low": "Gemini 3.8 Flash (Low)", "gemini-4-hydrogen-low": "Gemini 4 Hydrogen (Low)"}


class LocalOrbita:
    calls = []
    missions = []
    projects = []
    transcripts = {}
    results = {}

    def __init__(self, port):
        self.port = port

    def __enter__(self):
        return self

    def __exit__(self, *_):
        pass

    def call(self, method, route, **data):
        if method == "POST" and route.endswith("/turns/attachments"):
            data = {**data, "files": [(field, name, opened.read()) for field, (name, opened) in data["files"]]}
        self.calls.append((method, route, data))
        if method == "GET" and route.endswith("/signal-activity"):
            return {"turns": self.results.get(data["params"]["requestId"], [])}
        if method == "POST" and route == "/projects":
            project = {"id": "projeto-novo", "name": data["json"]["name"], "managed": True}
            self.projects.append(project)
            return project
        if method == "POST" and route == "/projects/projeto/missions":
            mission = MISSION | {"id": "abcdef1234567890", "name": data["json"]["name"]}
            self.missions.append(mission)
            return mission
        return {"missions": self.missions, "projects": self.projects}

    def events(self, mission_id, after=0, *, time_budget=None):
        self.calls.append(("EVENTS", mission_id, time_budget))
        return iter(self.transcripts.get(mission_id, ()))


class MessagingTests(TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.home = Path(directory.name)
        self.owner = SimpleNamespace(platform=Platform.TELEGRAM, user_id="101", chat_id="101", chat_type="dm", is_bot=False)
        self.signal = SimpleNamespace(platform=Platform.SIGNAL, user_id="+15551234567", chat_id="+15551234567", chat_type="dm", is_bot=False)
        self.discord = SimpleNamespace(platform=Platform.DISCORD, user_id="173829405617283901", chat_id="273829405617283901",
                                      chat_type="group", is_bot=False)
        LocalOrbita.calls.clear()
        LocalOrbita.missions = [MISSION]
        LocalOrbita.projects = [{"id": "projeto", "name": "Projeto"}]
        LocalOrbita.transcripts = {}
        LocalOrbita.results = {}
        for name, value in {"OrbitaClient": LocalOrbita, "get_hermes_home": lambda: self.home,
                            "binding_directory": lambda: self.home / "orbita-missions",
                            "respond": lambda *_: {"action": "answer", "reply": "Oi! Como posso ajudar?"},
                            "get_env_value_prefer_dotenv": lambda key: {
                                "TELEGRAM_ALLOWED_USERS": "101, *, @nome",
                                "SIGNAL_ALLOWED_USERS": "+15551234567, *",
                                "DISCORD_ALLOWED_USERS": self.discord.user_id,
                                "DISCORD_ALLOWED_CHANNELS": self.discord.chat_id,
                            }.get(key)}.items():
            self.enterContext(patch.object(messaging, name, value))
        self.enterContext(patch.object(bridge, "get_hermes_home", lambda: self.home))
        self.enterContext(patch.object(signal_chat, "get_hermes_home", lambda: self.home))
        self.enterContext(patch.object(signal_files, "get_hermes_home", lambda: self.home))

    def send(self, text, source=None, emit=lambda _: None, history=(), *, files=None):
        agent = SimpleNamespace(_interrupt_requested=False)
        return messaging.execute_message_turn(7784, source or self.owner, agent, text, [], emit, history, signal_files=files)

    def test_requires_same_paired_private_chat(self):
        for flags in [{"chat_type": "group"}, {"chat_id": "999"}, {"user_id": "999", "chat_id": "999"}, {"is_bot": True}]:
            sender = SimpleNamespace(**(vars(self.owner) | flags))
            with self.assertRaises(RuntimeError):
                self.send("!missoes", sender)
        self.assertFalse(LocalOrbita.calls)

    def test_selects_existing_mission_and_reuses_engine(self):
        self.assertIn("Escolha uma missão", self.send("Abra o aplicativo")[0])
        self.assertIn("12345678", self.send("!missoes")[0])
        self.assertIn("Missão de teste", self.send("!usar 12345678")[0])
        self.assertIn("Pronta", self.send("!status")[0])
        with patch.object(messaging, "execute_turn", return_value=("Trabalho salvo", "concluida")) as execute:
            self.send("!retomar")
            execute.assert_called_once()
            self.assertEqual(execute.call_args.args[1], "telegram_101")
            self.assertEqual(execute.call_args.kwargs, {"resume": True})
            self.assertEqual((self.home / "orbita-missions" / "telegram_101.txt").read_text(), MISSION["id"])
        self.send("!parar")
        self.assertEqual(LocalOrbita.calls[-1], ("POST", f"/missions/{MISSION['id']}/cancel", {"json": {}}))

    def test_reports_blockers_and_retries_deliveries_without_duplicates(self):
        blocked = MISSION | {"state": "aguardando-verificacao"}
        adapter = SimpleNamespace(send=AsyncMock(side_effect=[SimpleNamespace(success=False), SimpleNamespace(success=True)]))
        for _ in range(3):
            asyncio.run(messaging._send_alerts(Platform.TELEGRAM, adapter, "101", [blocked]))
        self.assertEqual(adapter.send.await_count, 2)
        chat, message = adapter.send.await_args.args
        self.assertEqual(chat, "101")
        self.assertIn("verificação da conta", message)
        self.assertIn("!usar 12345678", message)

    def test_message_runs_in_shared_mission_with_desktop_permissions(self):
        class CompletedOrbita(LocalOrbita):
            def events(self, mission, after=0):
                return iter(())

            def call(self, method, route, **data):
                if method == "GET" and route == f"/missions/{MISSION['id']}":
                    return {"state": "concluida"}
                return super().call(method, route, **data)

        self.send("!usar 12345678")
        (self.home / "orbita-permission.json").write_text("true", encoding="utf8")
        with patch.object(bridge, "OrbitaClient", CompletedOrbita), patch.object(bridge, "get_hermes_home", lambda: self.home):
            _, state = self.send("Rode os testes")
        self.assertEqual(state, "concluida")
        route, payload = next((route, data["json"]) for method, route, data in LocalOrbita.calls if method == "POST")
        self.assertEqual(route, f"/missions/{MISSION['id']}/turns")
        self.assertEqual((payload["text"], payload["fullAccess"], payload["execute"]), ("Rode os testes", True, True))

    def test_signal_note_to_self_and_private_alert(self):
        phone = "+15551234567"
        self.assertEqual(messaging.allowed_users(Platform.SIGNAL), {phone})
        owner = SimpleNamespace(platform=Platform.SIGNAL, user_id=phone, chat_id=phone, chat_type="dm", is_bot=False)
        self.assertIn("Missão selecionada", self.send("!usar 12345678", owner)[0])
        caller = self.home / "orbita-missions" / ("signal_" + messaging.local_identity(Platform.SIGNAL, phone) + ".txt")
        self.assertEqual(caller.read_text(), MISSION["id"])
        other = SimpleNamespace(**(vars(owner) | {"user_id": "+15559876543", "chat_id": "+15559876543"}))
        with self.assertRaises(RuntimeError):
            self.send("!status", other)
        adapter = SimpleNamespace(send=AsyncMock(return_value=SimpleNamespace(success=True)))
        asyncio.run(messaging._send_alerts(Platform.SIGNAL, adapter, phone, [MISSION | {"state": "aguardando-cota"}]))
        self.assertEqual(adapter.send.await_args.args[0], phone)
        self.assertIn('Como está a missão Missão de teste?', adapter.send.await_args.args[1])

    def test_signal_creates_named_project_without_a_selected_mission(self):
        LocalOrbita.missions = []
        tokens = []
        with patch.object(messaging, "execute_turn") as execute:
            reply, state = self.send("Quero criar um novo projeto chamado Gabarito", self.signal, tokens.append)
            self.assertEqual(tokens, [reply])
            self.assertIn('Criei o projeto “Gabarito” com a pasta padrão', reply)
            self.assertEqual(state, "pronta")
            self.assertIn("já está", self.send("Crie um projeto com o nome gabarito.", self.signal)[0])
            self.assertIn("Gabarito · Sem chats", self.send("Quais projetos eu tenho?", self.signal)[0])
            execute.assert_not_called()
        self.assertEqual([call for call in LocalOrbita.calls if call[0] == "POST"],
                         [("POST", "/projects", {"json": {"name": "Gabarito"}})])
        self.assertFalse((self.home / "orbita-missions").exists())

    def test_signal_keeps_the_name_and_only_creates_on_a_request(self):
        with patch.object(messaging, "execute_turn", return_value=("Como criar", "pronta")):
            self.send("Como faço para criar um projeto chamado Órbita de Café?", self.signal)
        self.assertIn("Qual é o nome", self.send("Quero criar um novo projeto", self.signal)[0])
        self.assertFalse(any(method == "POST" for method, _, _ in LocalOrbita.calls))
        self.send('Você pode criar um projeto chamado “Órbita de Café”?', self.signal)
        self.assertEqual(LocalOrbita.calls[-1], ("POST", "/projects", {"json": {"name": "Órbita de Café"}}))

    def test_signal_uses_repository_name_and_remembers_mission(self):
        LocalOrbita.missions = [MISSION | {"name": "Life", "workspace": "C:/fixture/worktree"},
                                MISSION | {"name": "NotaFiscal", "id": "abcdef1234567890"}]
        LocalOrbita.projects = [{"id": "projeto", "name": "Life"}]
        with patch.object(messaging, "_repository_name", side_effect=lambda workspace: "lifehub" if workspace else ""):
            self.assertIn("Como posso ajudar", self.send("Oi", self.signal)[0])
            self.assertIn("Life · Pronta", self.send("Como está o LifeHub?", self.signal)[0])
            self.assertIn("Life · Pronta", self.send("Como tá?", self.signal)[0])
            self.assertIn("Qual dessas missões", self.send("Quero trabalhar no projeto Inventado", self.signal)[0])
            self.assertIn("NotaFiscal", self.send("Quais projetos eu tenho?", self.signal)[0])

    def test_signal_resumes_pending_without_ignoring_additional_instructions(self):
        LocalOrbita.missions = [MISSION | {"name": "Life", "state": "aguardando-cota", "pending": "Rode os testes"}]
        self.send("Continua de onde parou no projeto Life", self.signal)
        self.assertEqual(LocalOrbita.calls[-1][1], f"/missions/{MISSION['id']}/resume")
        with patch.object(messaging, "respond", return_value={"action": "develop"}):
            text = "Continua de onde parou, mas só rode os testes do cadastro"
            self.send(text, self.signal)
        self.assertEqual(LocalOrbita.calls[-1][1], f"/missions/{MISSION['id']}/turns")
        self.assertEqual(LocalOrbita.calls[-1][2]["json"]["text"], text)

    def test_signal_question_is_conversational_and_streams_once(self):
        tokens = []
        LocalOrbita.missions = [MISSION | {"state": "executando"}]
        history = [{"role": "user", "content": "Estamos falando do cadastro"}]
        with patch.object(messaging, "respond", return_value={"action": "answer", "reply": "Aqui está a explicação."}) as ask:
            self.send("Me explica essa mudança", self.signal, tokens.append, history)
        self.assertEqual(ask.call_args.args[2], history)
        self.assertEqual(ask.call_args.args[3]["missoes"][0]["estado"], "Executando")
        self.assertFalse(any(method == "POST" for method, _, _ in LocalOrbita.calls))
        self.assertEqual(tokens, ["Aqui está a explicação."])

    def test_signal_switches_conversation_model_without_changing_development(self):
        with patch.object(signal_chat, "_catalog", return_value=LIGHT_MODELS):
            menu, _ = self.send("!model", self.signal)
            self.assertIn("1. Gemini 3.8 Flash (Low)", menu)
            self.assertIn("2. Gemini 4 Hydrogen (Low)", menu)
            self.assertIn("Gemini 4 Hydrogen (Low)", self.send("2", self.signal)[0])
            envelope = {"event": "result", "result": {"status": "SUCCESS", "structured_output": {"action": "develop"}}}
            result = SimpleNamespace(returncode=0, stdout=json.dumps(envelope), stderr="")
            with patch.object(signal_chat, "_cli", return_value=result) as cli, patch.object(messaging, "respond", signal_chat.respond):
                self.assertIn("Pedido iniciado", self.send("Teste o cadastro neste projeto", self.signal)[0])
            self.assertIn("gemini-4-hydrogen-low", cli.call_args.args[0])
            self.assertIn("--sandbox", cli.call_args.args[0])
            self.assertIn("Teste o cadastro", cli.call_args.kwargs["input_text"])
            self.assertTrue(LocalOrbita.calls[-1][2]["json"]["execute"])
            self.assertIsNone(LocalOrbita.calls[-1][2]["json"]["model"])
            self.assertIn("Gemini 3.8 Flash (Low)", self.send("!model 0", self.signal)[0])
        self.assertEqual(signal_chat._automatic({"gemini-4-hydrogen-low": "Hydrogen"}), "gemini-4-hydrogen-low")

    def test_signal_creates_a_new_chat_and_forwards_the_exact_prompt_once(self):
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        payload = 'Crie uma tela de cadastro com campos de nome e telefone.\nTeste o botão “Salvar”.'
        text = 'Vai no projeto Gabarito, abre um novo chat e coloca o seguinte prompt “' + payload + '”'
        result = {"action": "develop", "project_id": "projeto", "request": "Pedido resumido."}
        with patch.object(messaging, "respond", return_value=result):
            self.assertIn("Chat 2", self.send(text, self.signal)[0])
        self.assertEqual(LocalOrbita.calls[-2], ("POST", "/projects/projeto/missions", {"json": {"name": "Chat 2"}}))
        self.assertEqual(LocalOrbita.calls[-1][2]["json"]["text"], payload)
        self.assertEqual(LocalOrbita.calls[-1][2]["json"]["source"], "signal")

    def test_signal_delivers_a_screenshot_as_media_and_requires_private_pairing(self):
        path = self.home / "cache" / "screenshots" / "captura de teste.jpg"
        photo = tools.SignalPhoto(path, "Captura da janela Calculadora.")
        agent = SimpleNamespace(stream_delta_callback=None, _flush_messages_to_session_db=lambda *_: None)
        with patch.object(messaging, "capture_screen", return_value=photo) as capture:
            group = SimpleNamespace(**(vars(self.signal) | {"chat_type": "group"}))
            with self.assertRaises(RuntimeError):
                self.send("!print", group)
            capture.assert_not_called()
            result = bridge.run_gateway_turn(7784, "signal_fixture", agent, "Tira um print da janela Calculadora", [], [], self.signal)
            capture.assert_called_once_with("Calculadora")
        media, caption = BasePlatformAdapter.extract_media(result["final_response"])
        self.assertEqual([(Path(file), voice) for file, voice in media], [(path, False)])
        self.assertEqual(caption, photo.caption)
        self.assertEqual(result["messages"][-1]["content"], photo.caption)

    def test_signal_opens_a_program_without_using_the_mission_executor(self):
        LocalOrbita.missions = [MISSION | {"state": "executando"}]
        with patch.object(messaging, "open_program", return_value="Abri a Calculadora.") as launch:
            self.assertIn("Calculadora", self.send("Abra a Calculadora", self.signal)[0])
            self.send("Como abrir a Calculadora?", self.signal)
            launch.assert_called_once_with("Calculadora")
        self.assertFalse(any(method == "POST" for method, _, _ in LocalOrbita.calls))

    def test_signal_reads_only_the_requested_chat_even_with_repeated_titles(self):
        job = MISSION | {"name": "Chat 1", "state": "executando"}
        LocalOrbita.missions = [job, job | {"id": "2222222222", "name": "Chat 2"},
                                job | {"id": "3333333333", "projectId": "outro"}]
        LocalOrbita.projects = [{"id": "projeto", "name": "Gabarito"}, {"id": "outro", "name": "Outro"}]
        LocalOrbita.transcripts = {job["id"]: [
            {"kind": "usuario", "text": "Revise o cadastro", "at": MISSION["updated"], "sequence": 1},
            {"kind": "mensagem", "text": "O cadastro foi testado.", "at": MISSION["updated"], "sequence": 2},
        ]}
        self.assertIn("Qual chat", self.send("!chat Chat 1", self.signal)[0])
        self.assertIn("O cadastro foi testado", self.send("Leia o Chat 1 do projeto Gabarito", self.signal)[0])
        self.assertEqual([entry[1] for entry in LocalOrbita.calls if entry[0] == "EVENTS"], [job["id"]])
        self.assertIn("Qual é o projeto", self.send("Leia o Chat 8 do projeto Gabarito", self.signal)[0])
        with patch.object(messaging, "respond", side_effect=[{"action": "read_chat", "mission_id": job["id"]},
                                                             {"action": "answer", "reply": "Já tinha testado o cadastro."}]) as ask:
            self.assertIn("Já tinha testado", self.send("E o que foi feito nesse chat?", self.signal)[0])
        self.assertEqual(ask.call_args.args[3]["mensagens"][-1]["content"], "O cadastro foi testado.")

    def test_signal_forwards_a_document_and_delivers_the_matching_final_answer(self):
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        LocalOrbita.projects = [{"id": "projeto", "name": "Gabarito"}]
        cached = self.home / "cache" / "documents" / "cache.bin"
        cached.parent.mkdir(parents=True)
        cached.write_bytes(b"item,valor\ncaneta,10\n")
        caption = 'Coloque este arquivo no Chat 1 do projeto Gabarito com o prompt: “Some os valores.” e depois me manda o resultado.'
        fetched = SimpleNamespace(_fetch_attachment=AsyncMock(return_value=(str(cached), ".bin")))
        names = []
        urls, media_types = asyncio.run(SignalAdapter._collect_attachments(fetched, [
            {"id": "simulado", "size": cached.stat().st_size, "filename": "itens do Signal.csv", "contentType": "text/csv"}], names))
        event = SimpleNamespace(media_urls=urls, media_filenames=names, media_types=media_types,
                                raw_message={"orbita_attachment_count": 1}, text=caption)
        runner = SimpleNamespace(_consume_pending_native_image_paths=lambda key: [])
        with patch.object(bridge, "bridge_port", return_value=7784):
            prepared = asyncio.run(GatewayInboundMixin._prepare_inbound_message_text(runner, event=event, source=self.signal,
                                                                                      history=[], session_key="owner-chat"))
        self.assertEqual(prepared, caption)
        native = signal_files.consume_signal_files(runner, "owner-chat")
        self.assertIsNone(signal_files.consume_signal_files(runner, "owner-chat"))
        self.assertIn("Vou enviar o resultado", self.send(caption, self.signal, files=native)[0])
        _, route, request = LocalOrbita.calls[-1]
        self.assertEqual(route, f"/missions/{MISSION['id']}/turns/attachments")
        self.assertEqual(request["files"], [("files", "itens do Signal.csv", cached.read_bytes())])
        turn = json.loads(request["data"]["turn"])
        self.assertEqual(turn["text"], "Some os valores.")
        receipt = turn["requestId"]
        adapter = SimpleNamespace(send=AsyncMock(side_effect=[SimpleNamespace(success=False), SimpleNamespace(success=True)]))
        LocalOrbita.results[receipt] = [{"requestId": receipt, "terminalState": None, "reply": "Parcial"}]
        asyncio.run(messaging._send_results(7784, adapter, self.signal.user_id))
        adapter.send.assert_not_called()
        LocalOrbita.results[receipt] = [{"requestId": receipt, "terminalState": "concluida", "reply": "Total de 10."}]
        for _ in range(3):
            asyncio.run(messaging._send_results(7784, adapter, self.signal.user_id))
        self.assertEqual(adapter.send.await_count, 2)
        self.assertEqual(adapter.send.await_args.args, (self.signal.user_id, "Resultado final · Chat 1\n\nTotal de 10."))

    def test_signal_file_only_waits_for_the_following_prompt(self):
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        LocalOrbita.projects = [{"id": "projeto", "name": "Gabarito"}]
        file = self.home / "cache" / "documents" / "cached.bin"
        file.parent.mkdir(parents=True)
        file.write_text("Dados de teste", encoding="utf-8")
        inbound = ([{"path": str(file), "name": "dados.txt"}], False, signal_files._FILE_ONLY)
        self.assertIn("Recebi dados.txt", self.send(signal_files._FILE_ONLY, self.signal, files=inbound)[0])
        self.assertFalse(LocalOrbita.calls)
        result = self.send('Use este arquivo no Chat 1 do projeto Gabarito com o prompt: “Faça um resumo.”', self.signal)
        self.assertIn("Coloquei o pedido", result[0])
        self.assertEqual(json.loads(LocalOrbita.calls[-1][2]["data"]["turn"])["text"], "Faça um resumo.")

    def test_signal_automatic_huge_paste_remains_exact_text_and_subscribes_to_result(self):
        LocalOrbita.projects = [{"id": "projeto", "name": "Atlas"}]
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        request = "Implemente apenas a próxima etapa do Atlas.\n" + "Descrição das tarefas no Atlas.\n" * 285
        request = request.strip()
        full = f'Envia no Chat 1 do Atlas esse prompt: “{request}”'
        file = self.home / "cache" / "documents" / "cache.bin"
        file.parent.mkdir(parents=True)
        file.write_text(full, encoding="utf-8")
        for caption, content_type in [(full, "application/octet-stream"), (full[:1000], "text/x-signal-plain")]:
            runner = SimpleNamespace()
            event = SimpleNamespace(media_urls=[str(file)], media_filenames=[""], media_types=[content_type], raw_message={}, text=caption)
            self.assertEqual(signal_files.prepare_signal_files(runner, "paired", event), full)
            files = signal_files.consume_signal_files(runner, "paired")
            self.assertEqual(files[0], [])
            with patch.object(messaging, "respond") as classifier:
                self.assertIn("Vou enviar o resultado", self.send(caption, self.signal, files=files)[0])
                classifier.assert_not_called()
            method, path, data = LocalOrbita.calls[-1]
            self.assertEqual(path, f"/missions/{MISSION['id']}/turns")
            self.assertEqual(data["json"]["text"], request)
            self.assertTrue(data["json"]["requestId"])
        LocalOrbita.calls.clear()
        result = self.send("Manda no Chat 1 do projeto errado: Faça a revisão.", self.signal)[0]
        self.assertIn("Qual é o projeto", result)
        self.assertFalse(any(method == "POST" for method, _, _ in LocalOrbita.calls))

    def test_discord_and_signal_use_same_project_with_independent_final_destinations(self):
        LocalOrbita.projects = [{"id": "projeto", "name": "Atlas"}]
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        self.assertIn("Atlas / Chat 1", self.send("!vincular Atlas / Chat 1", self.discord)[0])
        with self.assertRaises(RuntimeError):
            self.send("!vincular Atlas / Chat 1", SimpleNamespace(**(vars(self.discord) | {"chat_id": "473829405617283901"})))
        with self.assertRaises(RuntimeError):
            self.send("!vincular Atlas / Chat 1", SimpleNamespace(**(vars(self.discord) | {"user_id": "573829405617283901"})))
        request = "Implemente a revisão.\n" + "Detalhe original.\n" * 510
        file = self.home / "cache" / "documents" / "paste.txt"
        file.parent.mkdir(parents=True)
        file.write_text(request, encoding="utf-8")
        runner = SimpleNamespace()
        attachment = SimpleNamespace(filename="message.txt", content_type="text/plain", size=file.stat().st_size)
        transport = SimpleNamespace(_discord_max_attachment_bytes=lambda: 5 * 1024 * 1024,
                                    _cache_discord_document=AsyncMock(return_value=file.read_bytes()))
        names = []
        with patch.object(discord_adapter, "cache_document_from_bytes_async", AsyncMock(return_value=str(file))):
            paths, kinds, _, _ = asyncio.run(discord_adapter.DiscordAdapter._collect_attachment_media(transport, [attachment], names))
        event = SimpleNamespace(media_urls=paths, media_filenames=names, media_types=kinds, raw_message={}, text="")
        signal_files.prepare_signal_files(runner, "discord", event, "discord")
        inbound = signal_files.consume_signal_files(runner, "discord")
        with patch.object(messaging, "respond", return_value={"action": "develop"}) as classify:
            self.assertIn("Vou enviar o resultado", self.send(inbound[2], self.discord, files=inbound)[0])
        self.assertEqual(classify.call_args.args[3]["missao_selecionada"], MISSION["id"])
        discord = LocalOrbita.calls[-1][2]["json"]
        self.assertEqual((discord["source"], discord["text"]), ("discord", request.strip()))
        self.send('Manda no Chat 1 do Atlas: “Execute o teste do Signal.”', self.signal)
        signal = LocalOrbita.calls[-1][2]["json"]
        self.assertEqual(signal["source"], "signal")
        for result, content in [(discord, "Resposta do Discord"), (signal, "Resposta do Signal")]:
            key = result["requestId"]
            LocalOrbita.results[key] = [{"requestId": key, "terminalState": "concluida", "reply": content}]
        signal_transport = SimpleNamespace(send=AsyncMock(return_value=SimpleNamespace(success=True)))
        discord_transport = SimpleNamespace(send=AsyncMock(return_value=SimpleNamespace(success=True)))
        gateway = SimpleNamespace(adapters={Platform.SIGNAL: signal_transport, Platform.DISCORD: discord_transport})
        with patch.object(messaging.asyncio, "sleep", AsyncMock(side_effect=asyncio.CancelledError)), self.assertRaises(asyncio.CancelledError):
            asyncio.run(messaging._watch(gateway, 7784))
        discord_transport.send.assert_awaited_once_with(self.discord.chat_id, "Resultado final · Chat 1\n\nResposta do Discord")
        signal_transport.send.assert_awaited_once_with(self.signal.user_id, "Resultado final · Chat 1\n\nResposta do Signal")

    def test_signal_cancels_only_on_clear_request(self):
        LocalOrbita.missions = [MISSION | {"name": "Life", "state": "executando"}]
        with patch.object(messaging, "execute_turn", return_value=("Entendi", "pronta")):
            self.send("Não pare o trabalho", self.signal)
        self.assertFalse(any(method == "POST" for method, _, _ in LocalOrbita.calls))
        self.assertIn("Pedi a interrupção", self.send("Pare a missão Life", self.signal)[0])
        self.assertEqual(LocalOrbita.calls[-1], ("POST", f"/missions/{MISSION['id']}/cancel", {"json": {}}))

    def test_discord_direct_chat_uses_only_the_owner_id_and_delivers_to_the_same_dm(self):
        LocalOrbita.projects = [{"id": "projeto", "name": "Atlas"}]
        LocalOrbita.missions = [MISSION | {"name": "Chat 1"}]
        dm = SimpleNamespace(**(vars(self.discord) | {"chat_type": "dm", "chat_id": "473829405617283901"}))
        with patch.object(messaging, "discord_channels", return_value=set()):
            for unexpected in ({"platform": Platform.DISCORD, "chat_type": "group"}, {"is_bot": True}, {"user_id": "573829405617283901"}):
                with self.assertRaises(RuntimeError):
                    self.send("!missoes", SimpleNamespace(**(vars(dm) | unexpected)))
            self.assertIsNone(messaging._discord_private_chat(dm.user_id))
            self.assertIn("Atlas / Chat 1", self.send("!vincular Atlas / Chat 1", dm)[0])
            self.assertEqual(messaging._discord_private_chat(dm.user_id), dm.chat_id)
            self.send('Manda no Chat 1 do Atlas: “Consulte o estado.”', dm)
            request = LocalOrbita.calls[-1][2]["json"]["requestId"]
            LocalOrbita.results[request] = [{"requestId": request, "terminalState": "concluida", "reply": "Consulta pronta"}]
            transport = SimpleNamespace(send=AsyncMock(return_value=SimpleNamespace(success=True)))
            gateway = SimpleNamespace(adapters={Platform.DISCORD: transport})
            with patch.object(messaging.asyncio, "sleep", AsyncMock(side_effect=asyncio.CancelledError)), self.assertRaises(asyncio.CancelledError):
                asyncio.run(messaging._watch(gateway, 7784))
            transport.send.assert_awaited_once_with(dm.chat_id, "Resultado final · Chat 1\n\nConsulta pronta")


if __name__ == "__main__":
    main()
