"""Use an independent Antigravity CLI turn for the private Signal conversation."""

import json
import os
import re
import shutil
import subprocess
from functools import lru_cache
from pathlib import Path
from time import monotonic

from hermes_constants import get_hermes_home


_AUTO = "auto"
_MODEL_ROW = re.compile(r"([a-z0-9][a-z0-9.-]*)[\t ]+(.+)")
_SETTINGS = json.dumps({"permissions": {"deny": [
    "command(*)", "read_file(*)", "write_file(*)", "mcp(*)", "read_url(*)", "execute_url(*)", "download_url(*)",
]}})
_SCHEMA = json.dumps({"type": "object", "properties": {
    "action": {"type": "string", "enum": ["answer", "create_project", "develop", "open_app", "read_chat", "capture_screen"]},
    "reply": {"type": "string"}, "name": {"type": "string"},
    "project_id": {"type": "string"}, "mission_id": {"type": "string"}, "request": {"type": "string"},
    "new_chat": {"type": "boolean"},
    "app": {"type": "string"},
    "window": {"type": "string"},
}, "required": ["action", "reply"]})
_INSTRUCTIONS = """Você é o atendente do Orbita no canal de mensagens indicado no contexto. Fale português do Brasil, de modo breve e natural.
O JSON abaixo contém os dados fornecidos pelo Orbita. Use os estados e nomes como referência, sem inventar acesso a arquivos ou trabalho concluído.
Classifique apenas a mensagem atual. O histórico ajuda a resolver referências e confirmações claras, mas não é um novo pedido.
Use answer para conversar, explicar, planejar sem implementar, consultar ou pedir o dado que falta. Escreva a resposta em reply.
Use open_app apenas se a mensagem atual pedir para abrir um programa no Windows. Copie em app o nome ou caminho fornecido pelo usuário, sem comandos nem argumentos. Para um aplicativo criado no projeto, indique mission_id ou project_id em vez de inventar um executável.
Use capture_screen apenas se a mensagem atual pedir uma captura de tela do PC do usuário. Preencha window apenas se o usuário nomear a janela que quer capturar. Perguntas sobre como tirar print e exemplos de comandos usam answer.
Use read_chat se o usuário pedir para ler, resumir ou responder com base em um chat de projeto. Indique mission_id ou project_id do chat solicitado. Se o contexto já incluir chat_lido, use answer, baseie-se somente nas mensagens fornecidas e diferencie relato do agente de verificação independente.
Use create_project com name apenas se o usuário pedir claramente para criar um projeto no Orbita e informar o nome. Uma ideia de aplicativo, pergunta sobre como criar ou exemplo não autoriza criar um projeto.
Use develop apenas para um pedido claro de implementar, editar, realizar testes ou executar trabalho de um projeto. Trate "Executar:" como pedido de execução. Escolha mission_id ou project_id exatos do contexto quando o alvo estiver claro. Use a missão selecionada apenas para referência implícita, nunca se o usuário pedir outro projeto. Se a mensagem atual confirma um pedido anterior claro, use request para recuperar o que foi autorizado, sem ampliar o escopo. Se houver dúvida ou faltar o alvo, pergunte em reply com action answer.
Se houver arquivos anexados e o usuário pedir para colocar um arquivo e um prompt no chat, ou realizar uma tarefa com o arquivo em um projeto, use develop. Em request coloque apenas o pedido que o chat deve receber. O Orbita encaminha os arquivos e devolve o resultado por conta própria. Se faltar o destino ou o pedido, pergunte em reply.
Só confirme que uma tarefa foi iniciada ou que o resultado foi cadastrado quando houver confirmação do Orbita. Uma pergunta sobre aviso futuro não inicia uma execução.
Se a pessoa mandar abrir um novo chat em um projeto e enviar um prompt, devolva o project_id, new_chat como true e apenas o prompt de trabalho em request. Para outros pedidos de execução, só preencha request se precisar separar instruções destinadas ao Signal ou recuperar o pedido confirmado. Preserve o texto do pedido, com seus detalhes, sem repeti-lo.
Os anexos só são acessíveis depois de encaminhar o trabalho à missão. Não invente o conteúdo de um anexo.
O Orbita verifica e executa ações separadamente. Não tente usar ferramentas nem afirmar que executou qualquer ação."""


def _cli(args, timeout, *, input_text=None):
    configured = os.environ.get("ORBITA_AGY_PATH")
    binary = configured if configured and Path(configured).is_file() else shutil.which("agy")
    if not binary:
        local = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "agy" / "bin" / "agy.exe"
        if not local.is_file():
            raise RuntimeError("Instale o Antigravity CLI oficial para conversar pelo Signal.")
        binary = str(local)
    workspace = get_hermes_home() / "orbita-signal-chat" / "workspace"
    workspace.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.pop("CLOUD_CODE_URL", None)
    try:
        return subprocess.run([binary, *args], cwd=workspace, env=env, input=input_text,
                              capture_output=True, encoding="utf-8", errors="replace", timeout=timeout,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except subprocess.TimeoutExpired as error:
        raise RuntimeError("O Antigravity demorou para responder no Signal. Tente novamente.") from error


@lru_cache(maxsize=1)
def _catalog(_minute):
    result = _cli(["models"], 15)
    models = {match[1]: match[2] for line in result.stdout.splitlines()
              if (match := _MODEL_ROW.fullmatch(line.strip())) and "-" in match[1]}
    if result.returncode or not models:
        raise RuntimeError("Não consegui listar os modelos. Confira o login do Antigravity CLI no PC.")
    return models


def _automatic(models):
    ranks = (
        lambda value: value.startswith("gemini-") and "-flash" in value and value.endswith("-low"),
        lambda value: "-hydrogen" in value and value.endswith("-low"),
        lambda value: value.startswith("gemini-") and ("-flash" in value or "-hydrogen" in value),
    )
    for match in ranks:
        choices = sorted((value for value in models if match(value)), reverse=True)
        if choices:
            return choices[0]
    return None


def _preferences(key):
    file = get_hermes_home() / "orbita-signal-chat" / f"{key}.json"
    try:
        return file, json.loads(file.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return file, {}


def _save(file, settings):
    file.parent.mkdir(parents=True, exist_ok=True)
    temporary = file.with_suffix(".tmp")
    temporary.write_text(json.dumps(settings), encoding="utf-8")
    temporary.replace(file)


def model_command(key, command, argument, platform="signal"):
    choose_number = command.isascii() and command.isdecimal() and not argument
    if command != "!model" and not choose_number:
        return None
    file, settings = _preferences(key)
    choices = settings.get("choices", [])
    if command != "!model":
        if not choices:
            return None
        argument = command
    models = _catalog(int(monotonic() // 60))
    chosen = settings.get("model", _AUTO)
    default = _automatic(models)
    automatic = models.get(default, "Gemini leve indisponível")
    label = "Discord" if platform == "discord" else "Signal"
    if not argument:
        ordered = sorted(models, key=lambda item: (item != default, models[item].casefold()))
        _save(file, {"model": chosen, "choices": ordered})
        current = f"Automático ({automatic})" if chosen == _AUTO else models.get(chosen, f"{chosen} (indisponível)")
        rows = [f"{index}. {models[model]}" for index, model in enumerate(ordered, 1)]
        return "\n".join([f"Modelo da conversa no {label} · {current}", "Responda com o número da lista.",
                          f"0. Automático ({automatic})", *rows])
    if re.fullmatch(r"[0-9]{1,3}", argument):
        index = int(argument)
        if index == 0:
            argument = _AUTO
        elif index <= len(choices):
            argument = choices[index - 1]
    if argument not in models and argument != _AUTO:
        return "Esse modelo não está na lista atual. Envie !model e escolha o número."
    _save(file, {"model": argument})
    return f"Modelo da conversa no {label} · {automatic if argument == _AUTO else models[argument]}"


def respond(key, text, history, context):
    file, settings = _preferences(key)
    selected = settings.get("model", _AUTO)
    if "choices" in settings:
        _save(file, {"model": selected})
    models = _catalog(int(monotonic() // 60))
    model = _automatic(models) if selected == _AUTO else selected
    if model not in models:
        raise RuntimeError("O modelo da conversa não está disponível. Envie !model para escolher outro.")
    transcript = [{"role": entry["role"], "content": entry["content"][-1800:]} for entry in history[-12:]
                  if entry.get("role") in {"user", "assistant"} and isinstance(entry.get("content"), str)]
    body = json.dumps({"contexto": context, "histórico": transcript, "mensagem_atual": text}, ensure_ascii=False)
    event = json.dumps({"event": "user", "message": {"content": _INSTRUCTIONS + "\n\n" + body}}) + "\n"
    process = _cli(["--model", model, "--mode", "plan", "--sandbox", "--settings", _SETTINGS,
                    "--disable-slash-commands", "--input-format", "stream-json", "--output-format", "stream-json",
                    "--json-schema", _SCHEMA, "--print-timeout", "45s"], 60, input_text=event)
    results = []
    for line in process.stdout.splitlines():
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if isinstance(row, dict) and row.get("event") == "result" and isinstance(row.get("result"), dict):
            results.append(row["result"])
    result = results[-1] if results else {}
    if str(result.get("response", "")).strip().lstrip("⚠️ ").casefold() == "verification required":
        raise RuntimeError("Confira a conta Google no Antigravity CLI e reenvie a mensagem pelo Signal.")
    if process.returncode or result.get("status") != "SUCCESS":
        error = (str(result.get("error", "")) + " " + process.stderr).casefold()
        if "authenticat" in error or "verification required" in error:
            raise RuntimeError("Confira a conta Google no Antigravity CLI e reenvie a mensagem pelo Signal.")
        if "quota" in error or "rate limit" in error or "429" in error:
            raise RuntimeError("O modelo da conversa ficou sem cota. Use !model para escolher outro.")
        raise RuntimeError("O Antigravity não conseguiu responder. Tente novamente ou use !model para trocar o modelo da conversa.")
    output = result.get("structured_output")
    if not isinstance(output, dict) or output.get("action") not in {"answer", "create_project", "develop", "open_app", "read_chat", "capture_screen"}:
        raise RuntimeError("Não consegui entender a resposta do Antigravity. Pode reformular o pedido?")
    return output
