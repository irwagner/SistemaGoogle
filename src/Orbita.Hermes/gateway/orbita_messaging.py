"""Connect paired Hermes messaging accounts to Orbita missions."""

import asyncio
import hashlib
import json
import logging
import re
import subprocess
import unicodedata
from contextlib import ExitStack
from functools import lru_cache
from pathlib import Path, PureWindowsPath
from time import time

import httpx
from gateway.config import Platform
from hermes_cli.config import get_env_value_prefer_dotenv
from hermes_constants import get_hermes_home

from .orbita_bridge import (ACTIVE, MISSION_ID, OrbitaClient, as_transcript, binding_directory, bridge_port, desktop_full_access,
                            execute_turn, save_binding, turn_preferences)
from .orbita_signal_chat import model_command, respond
from .orbita_signal_files import _FILE_ONLY, clear_files, keep_files, pending_files, queued_results, result_subscription, validate_files
from .orbita_signal_tools import SignalPhoto, capture_screen, open_program


_LOGGER = logging.getLogger(__name__)
_TELEGRAM_ID = re.compile(r"[1-9][0-9]{0,19}")
_DISCORD_ID = re.compile(r"[1-9][0-9]{16,19}")
_SIGNAL_ID = re.compile(r"(?:\+[1-9][0-9]{6,14}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})")
_CHANNELS = {
    Platform.TELEGRAM: ("TELEGRAM_ALLOWED_USERS", _TELEGRAM_ID),
    Platform.SIGNAL: ("SIGNAL_ALLOWED_USERS", _SIGNAL_ID),
    Platform.DISCORD: ("DISCORD_ALLOWED_USERS", _DISCORD_ID),
}
_STATES = {
    "pronta": "Pronta",
    "conversando": "Respondendo",
    "executando": "Executando",
    "concluida": "Concluída",
    "pendente": "Pendente",
    "interrompida": "Interrompida",
    "aguardando-cota": "Aguardando cota",
    "aguardando-verificacao": "Aguardando verificação do Google",
    "precisa-de-voce": "Requer sua intervenção",
}
_BLOCKERS = {
    "aguardando-verificacao": "O Google pediu a verificação da conta. Conclua pelo painel Contas do Antigravity no PC.",
    "aguardando-cota": "O modelo ficou sem cota.",
    "precisa-de-voce": "O agente precisa de uma informação ou ação sua.",
    "pendente": "A execução foi interrompida por um erro.",
    "interrompida": "A execução foi interrompida. O trabalho ficou salvo.",
}
_HELP = (
    "!missoes — listar as missões do Orbita\n"
    "!usar ID — escolher a missão\n"
    "!status — ver em que ponto está\n"
    "!retomar — retomar um pedido pendente\n"
    "!parar — interromper a execução\n\n"
    "Depois de escolher, escreva o pedido normalmente. Use Executar: para agir no modo padrão. "
    "Com Permissão total ativa no Orbita, os pedidos entram em execução diretamente."
)
_LIST = re.compile(r"(?:(?:quais|que) (?:sao os |sao as )?(?:projetos|missoes)(?: (?:tem|tenho|eu tenho|temos|estao disponiveis))?|(?:mostre|mostra|liste|lista|me mostra) (?:os |as |meus |minhas )?(?:projetos|missoes))")
_STATUS = re.compile(r"(?:(?:me diz|me diga) )?(?:como (?:esta|ta|anda|vai|ficou)(?: a missao| o projeto| o trabalho| a execucao)?|(?:qual (?:e )?o )?(?:status|andamento)|em que pe (?:esta|ta))")
_SWITCH = re.compile(r"(?:vamos(?: trabalhar)?|(?:quero|vou) trabalhar|(?:abra|abre|use|selecione)(?: a missao| o projeto)?)")
_SWITCH_WITH_NAME = re.compile(r"^(?:vamos(?: trabalhar)?|(?:quero|vou) trabalhar|(?:pode )?(?:continu[ae]|continuar|retom[ae]|retomar)|use|selecione) (?:no|na|em|para|pro|pra|o projeto|a missao) .+")
_RESUME = re.compile(r"(?:pode )?(?:continu[ae]|continuar|retom[ae]|retomar|prosseguir|prossig[ae])(?: de onde (?:parou|paramos)| o trabalho| a missao| a execucao)?")
_STOP = re.compile(r"(?:pode )?(?:pare|para|parar|interromp[ae]|cancele)(?: agora| a execucao| a missao| o trabalho)?")
_CREATE_PROJECT = re.compile(
    r"(?:(?:eu )?(?:quero|gostaria de) criar|(?:voc[eê] )?pode criar|cri[ae]) +"
    r"(?:(?:um|o) +)?(?:novo +)?projeto(?: +novo)?"
    r"(?: +(?:chamado|com (?:o )?nome(?: de)?|de nome)(?: +|: *)(?P<name>[^\r\n]+))?[.!?]?",
    re.IGNORECASE,
)
_FORWARDED_REQUEST = re.compile(
    r"\b(?:abr[ae]|cri[ae]|inici[ae])\s+(?:(?:um|uma|o|a)\s+)?(?P<new>nov[oa]\s+)?(?:chat|conversa)\b"
    r"(?:\s+e|,)\s+(?:coloc[ae]|cole|envi[ae]|mand[ae]|use)\s+(?:lá\s+)?(?:o\s+)?"
    r"(?:seguinte\s+prompt|prompt\s+a\s+seguir|prompt)\s*[:：]?\s*(?P<request>.+)",
    re.IGNORECASE | re.DOTALL,
)
_CAPTURE = re.compile(
    r"(?:print|(?:(?:voc[eê]\s+)?(?:pode|consegue)\s+)?(?:me\s+)?(?:tire?|tira(?:r)?|faça|faz(?:er)?|d[aá]r?|mande|manda(?:r)?|envie|enviar|captura(?:r)?)\s+"
    r"(?:um[a]?\s+)?(?:print|screenshot|captura de tela))(?:\s+(?:da|do|de)\s+(?:(?:janela|app|aplicativo)\s+)?(?P<window>.*?))?"
    r"(?:\s+(?:pra mim|para mim|aqui)|\s+e\s+(?:me\s+)?manda\s+(?:aqui|pra mim))?[.!?]*",
    re.IGNORECASE,
)
_OPEN_APP = re.compile(r"(?:(?:pode|consegue)\s+)?(?:abr[ae]|abrir|inici[ae]|iniciar)\s+(?:(?:o|a)\s+)?(?P<app>.+?)[!?]*", re.IGNORECASE)
_READ_CHAT = re.compile(r"(?:leia|lê|ler|mostre|me mostre|me mostra|manda|me manda)\s+(?:(?:o|a)\s+)?(?P<subject>hist[oó]rico(?:\s+do\s+chat)?|chat|conversa)(?P<query>.*?)[.!?]*", re.IGNORECASE)
_APP_FROM_PROJECT = re.compile(r"(?:app|aplicativo|programa)(?:\s+(?:do projeto|da missão)\s+(.+))", re.IGNORECASE)
_SKIP_PROGRAMS = {".git", ".venv", "venv", "node_modules", "obj", "packages", ".orbita"}
_FILE_REFERENCE = re.compile(r"\b(?:anexo|anexos|arquivo|arquivos|pdf|planilha|documento|foto|imagem)\b")
_FILE_PROMPT = re.compile(r"\b(?:com|e)\s+(?:o\s+)?(?:seguinte\s+)?(?:prompt|pedido)\s*[:：]\s*(?P<request>.+)", re.IGNORECASE | re.DOTALL)
_DIRECT_FORWARD = re.compile(
    r"^(?:envia(?:r)?|envie|manda(?:r)?|mande|execut[ae]|executar|coloque|coloca|escreva)\b[^\r\n]{0,85}?"
    r"\b(?P<target>chat\s+\d+[^:\r\n.?!\"“‘\[]*)\s*(?:[:：.\r\n]+\s*|(?=[\"“‘\[]))(?P<request>.+)",
    re.IGNORECASE | re.DOTALL,
)
_RETURN_WHEN_READY = re.compile(r"^quando\b.*\b(?:resultado|me avisa|avise|avisa)\b")


def _normalized(text, *, split_camel=True):
    if split_camel:
        text = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", text)
    letters = (char for char in unicodedata.normalize("NFKD", text.casefold()) if not unicodedata.combining(char))
    return " ".join(re.findall(r"[a-z0-9]+", "".join(letters)))


@lru_cache(maxsize=48)
def _repository_name(workspace):
    if not workspace:
        return ""
    try:
        process = subprocess.run(["git", "-C", workspace, "config", "--local", "--get", "remote.origin.url"],
                                 capture_output=True, text=True, timeout=2,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        slug = re.search(r"[/\\:]([a-zA-Z0-9_.-]+?)(?:\.git)?/?$", process.stdout.strip())
        return slug[1] if process.returncode == 0 and slug else ""
    except (OSError, subprocess.TimeoutExpired):
        return ""


def _aliases(mission, project):
    project_folder = PureWindowsPath(project.get("workspace") or "").name
    values = [mission["name"], project.get("name", ""), project_folder, _repository_name(mission.get("workspace"))]
    normal = {_normalized(value) for value in values if value}
    return {value for spelling in normal for value in (spelling, spelling.replace(" ", "")) if value}


def _control(text, names):
    for name in names:
        text = re.sub(rf"\b{re.escape(name)}\b", "", text)
    return re.sub(r"\b(?:(?:no|na|do|da|de|em|para|pro|pra|sobre)(?: (?:o|a|projeto|missao))?|o|a)$", "", " ".join(text.split())).strip()


def _matches_target(variants, aliases):
    candidates = []
    for text in variants:
        names = sorted((name for name in aliases if re.search(rf"\b{re.escape(name)}\b", text)), key=len, reverse=True)
        if not names:
            continue
        control = _control(text, names)
        if not control or any(rule.fullmatch(control) for rule in (_STATUS, _SWITCH, _RESUME, _STOP)) or any(
            re.search(rf"\b(?:no|na|do|da|em|projeto|missao) {re.escape(name)}\b", text) for name in names
        ):
            candidates.append((len(names[0]), control))
    return max(candidates, key=lambda candidate: candidate[0])[1] if candidates else None


def _create_project(client, projects, name):
    name = (name or "").rstrip(".!?").strip()
    if any(name.startswith(left) and name.endswith(right) for left, right in [('"', '"'), ("'", "'"), ("“", "”"), ("‘", "’")]):
        name = name[1:-1].strip()
    if not name:
        return 'Qual é o nome do projeto? Escreva “Crie um projeto chamado” seguido do nome.'
    for project in projects:
        if project["name"].casefold() == name.casefold():
            return f'O projeto “{project["name"]}” já está na lista Projetos do Orbita.'
    created = client.call("POST", "/projects", json={"name": name})
    return f'Criei o projeto “{created["name"]}” com a pasta padrão do Orbita. Ele aparece em Projetos no desktop.'


def _project_list(missions, projects):
    if not projects:
        return 'Você ainda não tem projetos. Pode escrever “Crie um projeto chamado” seguido do nome.'
    with_chats = {mission["projectId"] for mission in missions}
    listing = _mission_list(missions, projects, "Seus projetos e chats") if missions else "Seus projetos"
    return "\n".join([listing, *(f"• {project['name']} · Sem chats" for project in projects.values() if project["id"] not in with_chats)])


def _start_development(client, binding, mission, text, images, *, resume=False, files=(), owner=None, platform="signal"):
    mission_id = mission["id"]
    if mission["state"] in ACTIVE:
        return f'A missão “{mission["name"]}” já está em andamento. Pode perguntar como ela está enquanto trabalha.'
    route = f"/missions/{mission_id}"
    access = desktop_full_access()
    if resume:
        client.call("POST", route + "/resume", json={"fullAccess": access, "source": platform})
    else:
        payload = {**turn_preferences(mission), "text": text, "execute": True, "fullAccess": access, "source": platform}
        if files:
            files = validate_files(files)
        uploads = files or [{"path": path, "name": Path(path).name} for path in images]
        queued = None
        if owner:
            payload["requestId"], queued = result_subscription(owner, mission)
        try:
            if uploads:
                with ExitStack() as stack:
                    attached = [("files", (entry["name"], stack.enter_context(Path(entry["path"]).open("rb")))) for entry in uploads]
                    client.call("POST", route + "/turns/attachments", data={"turn": json.dumps(payload)}, files=attached)
            else:
                client.call("POST", route + "/turns", json=payload)
        except (RuntimeError, OSError):
            if queued:
                queued.unlink(missing_ok=True)
            raise
        if owner and files:
            clear_files(owner)
    save_binding(binding, mission_id)
    if files and owner:
        return f'Coloquei o pedido e {len(files)} anexo(s) em “{mission["name"]}”. Vou enviar o resultado por aqui quando terminar.'
    if owner and not resume:
        return f'Pedido iniciado em “{mission["name"]}”. Vou enviar o resultado por aqui quando terminar.'
    return f'Pedido iniciado em “{mission["name"]}”. Você pode continuar conversando por aqui e perguntar pelo andamento.'


def _development_request(text, decision, forwarded):
    if forwarded:
        request = forwarded["request"].strip()
        if request and request[0] in '"“‘[':
            closing = {'"': '"', '“': '”', '‘': '’', '[': ']'}[request[0]]
            end = request.rfind(closing, 1)
            suffix = _normalized(request[end + 1:]) if end > 0 else ""
            if end > 0 and (not suffix or re.match(r"^(?:e )?(?:depois|quando acabar|quando terminar|me devolva|me envie|me manda)", suffix)):
                request = request[1:end].strip()
            else:
                request = request[1:].strip()
        if request:
            return request
    request = decision.get("request")
    return request.strip() if isinstance(request, str) and request.strip() else text


def _signal_text(text):
    # A quoted transcript is text. Paths mentioned in it must not become attachments.
    text = re.sub(r"(?i)MEDIA\s*:", "MEDIA：", text)
    text = text.replace("[[audio_as_voice]]", "").replace("[[as_document]]", "").replace("![", "Imagem [")
    return re.sub(r"(?<![\w:/])(?:[A-Za-z]:[/\\]|~/|/)(?:[\w.\-]+[/\\])*[\w.\-]+\.[A-Za-z0-9]+\b",
                  lambda match: match[0] if text[max(0, match.start() - 1):match.start()] == "`" and
                  text[match.end():match.end() + 1] == "`" else f"`{match[0]}`", text)


def _requested_missions(query, missions, projects, selected, *, require_project=False):
    query = _normalized(query)
    if query in {"", "desse chat", "deste chat", "dessa conversa", "desta conversa", "atual"}:
        return [selected] if selected else missions

    def mentions(name):
        return bool(name and re.search(rf"\b{re.escape(_normalized(name))}\b", query))

    projects_mentioned = {project_id for project_id, project in projects.items()
                          if mentions(project["name"]) or mentions(PureWindowsPath(project.get("workspace") or "").name)}
    if require_project and not projects_mentioned:
        return []
    candidates = [item for item in missions if not projects_mentioned or item["projectId"] in projects_mentioned]
    names = [item for item in candidates if mentions(item["name"]) or mentions("chat " + item["name"])]
    if names or re.search(r"\bchat \d+\b", query):
        return names
    if projects_mentioned:
        return candidates
    return [item for item in candidates if any(mentions(alias) for alias in _aliases(item, projects.get(item["projectId"], {})))]


def _decision_missions(decision, missions, selected):
    if decision.get("mission_id"):
        return [item for item in missions if item["id"] == decision["mission_id"] and
                (not decision.get("project_id") or item["projectId"] == decision["project_id"])]
    if decision.get("project_id"):
        return [item for item in missions if item["projectId"] == decision["project_id"]]
    return [selected] if selected else missions


def _select_for_action(missions, projects):
    if len(missions) == 1:
        return missions[0], None
    if missions:
        return None, _mission_list(missions, projects, "Qual chat você quer usar? Informe o projeto e o nome do chat.")
    return None, 'Qual é o projeto e o nome do chat? Exemplo “Gabarito / Chat 1”.'


def _read_chat(client, binding, selected, projects, key=None, text=None, history=()):
    mission, message = _select_for_action(selected, projects)
    if not mission:
        return message
    save_binding(binding, mission["id"])
    rows = as_transcript(client.events(mission["id"], time_budget=2))[-12:]
    project = projects.get(mission["projectId"], {}).get("name", "Projeto")
    heading = f'{project} / {mission["name"]}'
    if not rows:
        return f'{heading} ainda não tem mensagens.'
    if key and text:
        try:
            result = respond(key, text, history, {"chat_lido": heading, "estado": mission["state"], "mensagens": [
                {"role": row["role"], "content": row["content"][-4000:]} for row in rows]})
            if result.get("action") == "answer" and isinstance(result.get("reply"), str) and result["reply"].strip():
                return f'{heading}\n\n{result["reply"]}'
        except RuntimeError:
            pass  # The original entries remain available if a summary can't be generated.
    latest = rows[-6:]
    return "\n\n".join([f'{heading} · Mensagens recentes', *(
        f'{"Você" if row["role"] == "user" else "Antigravity"}\n{row["content"][-3500:].strip()}' for row in latest)])


def _open_project_program(client, selected, projects, target=""):
    mission, message = _select_for_action(selected, projects)
    if not mission:
        return message
    workspace = Path(mission["workspace"])
    executables = []
    for root, dirs, files in os.walk(workspace, followlinks=False):
        dirs[:] = [item for item in dirs if item.casefold() not in _SKIP_PROGRAMS and not item.startswith('.') and
                   not Path(root, item).is_junction()]
        for file in files:
            if file.lower().endswith(".exe") and (not target or _normalized(Path(file).stem) == _normalized(Path(target).stem)):
                executables.append(str(Path(root, file).relative_to(workspace)))
    if not executables:
        return f'Não encontrei um aplicativo .exe pronto no chat “{mission["name"]}”.'
    if len(executables) > 1:
        return "Encontrei mais de um executável. Qual você quer abrir? " + ", ".join(executables[:8])
    result = client.call("POST", f"/missions/{mission['id']}/open-app", json={"path": executables[0]})
    return result["message"]


def _signal_turn(port, key, binding, text, images, emit, history, inbound=None, platform="signal"):
    def reply(message):
        if isinstance(message, SignalPhoto):
            return SignalPhoto(message.path, _signal_text(message.caption)), "pronta"
        message = _signal_text(message)
        emit(message)
        return message, "pronta"

    if inbound:
        received, incomplete, text = inbound
    command, _, argument = text.partition(" ")
    if command.casefold() == "!print":
        return reply(capture_screen(argument.strip()))
    if snapshot := _CAPTURE.fullmatch(text):
        window = (snapshot["window"] or "").strip()
        return reply(capture_screen("" if _normalized(window) in {"", "tela", "tela do pc", "pc", "computador", "desktop"} else window))
    if command.casefold() == "!abrir":
        return reply(open_program(argument))
    normalized = _normalized(text)
    previous = pending_files(key)
    if inbound:
        if incomplete:
            return reply("Não consegui baixar todos os arquivos. Reenvie os anexos para eu incluir no pedido.")
        files = validate_files(received)
        file_only = text in {_FILE_ONLY, "Arquivo recebido pelo Discord."}
        keep_files(key, files, "" if file_only or not files else text)
        if file_only:
            names = ", ".join(entry["name"] for entry in files)
            return reply(f'Recebi {names}. Diga o projeto, o chat e o que devo fazer com o arquivo.')
    else:
        refers_to_files = _FILE_REFERENCE.search(normalized) or (not previous.get("prompt") and
            re.match(r"^(?:resuma|resume|analise|analisa|traduza|traduz|leia|revise|revisa|extrai|extraia)\b", normalized))
        files = previous["files"] if refers_to_files else []
    variants = tuple(dict.fromkeys((normalized, _normalized(text, split_camel=False))))
    with OrbitaClient(port) as client:
        state = client.call("GET", "/state")
        projects = {project["id"]: project for project in state["projects"]}
        missions = state["missions"]
        if create := _CREATE_PROJECT.fullmatch(text):
            return reply(_create_project(client, projects.values(), create["name"]))
        if _LIST.fullmatch(normalized):
            return reply(_project_list(missions, projects) if "projetos" in normalized.split() else _mission_list(missions, projects))
        bound = binding.read_text(encoding="ascii") if binding.is_file() else ""
        if direct := _DIRECT_FORWARD.fullmatch(text):
            target_name = direct["target"]
            named_project = bool(re.match(r"chat\s+\d+\s+(?:do|da|de|no|na|em)\s", target_name, re.IGNORECASE))
            targets = _requested_missions(target_name, missions, projects, None, require_project=named_project)
            target, message = _select_for_action(targets, projects)
            if target:
                return reply(_start_development(client, binding, target, _development_request(text, {}, direct), images,
                                                files=files, owner=key, platform=platform))
            return reply(message)
        matches = []
        for mission in missions:
            aliases = _aliases(mission, projects.get(mission["projectId"], {}))
            control = _matches_target(variants, aliases)
            if control is not None:
                matches.append((mission, control))

        if len(matches) > 1 and any(not control or any(rule.fullmatch(control) for rule in (_STATUS, _SWITCH, _RESUME, _STOP))
                                    for _, control in matches):
            return reply(_mission_list([mission for mission, _ in matches], projects, "Qual dessas missões você quer usar?"))
        if len(matches) == 1:
            mission, control = matches[0]
        else:
            if _SWITCH_WITH_NAME.fullmatch(normalized) and missions:
                return reply(_mission_list(missions, projects, "Qual dessas missões você quis dizer?"))
            active = [item for item in missions if item["state"] in ACTIVE]
            mission = next((item for item in missions if item["id"] == bound), None)
            if mission is None:
                mission = active[0] if len(active) == 1 else missions[0] if len(missions) == 1 else None
            control = normalized
        if _RETURN_WHEN_READY.search(normalized) and mission:
            subscribed = mission["id"] in _subscription_missions(key)
            if subscribed:
                return reply(f'Estou acompanhando “{mission["name"]}”. Vou enviar o resultado por aqui quando terminar.')
            return reply(_mission_status(client, mission))
        if not inbound and previous.get("prompt") and re.match(r"^(?:no |na |projeto |chat |use )", normalized):
            destination = _requested_missions(text, missions, projects, mission)
            if len(destination) == 1:
                body = previous["prompt"]
                forwarded = _FORWARDED_REQUEST.search(body) or _FILE_PROMPT.search(body)
                return reply(_start_development(client, binding, destination[0], _development_request(body, {}, forwarded), [],
                                                files=previous["files"], owner=key, platform=platform))
        file_prompt = _FILE_PROMPT.search(text) if files else None
        if file_prompt and not _FORWARDED_REQUEST.search(text):
            destination = _requested_missions(text[:file_prompt.start()], missions, projects, None)
            if len(destination) == 1:
                return reply(_start_development(client, binding, destination[0], _development_request(text, {}, file_prompt), [],
                                                files=files, owner=key, platform=platform))
        if command.casefold() == "!vincular":
            selected, message = _select_for_action(_requested_missions(argument, missions, projects, None), projects)
            if not selected:
                return reply(message)
            save_binding(binding, selected["id"])
            return reply(f'Esta conversa está vinculada a “{projects.get(selected["projectId"], {}).get("name", "Orbita")} / {selected["name"]}”.')
        read = _READ_CHAT.fullmatch(text)
        if command.casefold() in {"!chat", "!historico", "!histórico"} or read:
            query = read["query"].strip() if read else argument
            if read and read["subject"].casefold().endswith("chat") and re.match(r"^\d+\b", query):
                query = f"Chat {query}"
            return reply(_read_chat(client, binding, _requested_missions(query, missions, projects, mission), projects))
        if mission is not None:
            if len(matches) == 1 or not control or any(rule.fullmatch(control) for rule in (_STATUS, _SWITCH, _RESUME, _STOP)):
                if bound != mission["id"]:
                    save_binding(binding, mission["id"])
            if not control or _SWITCH.fullmatch(control):
                return reply(f"Estou na missão {mission['name']}. O que você quer fazer?")
            if _STATUS.fullmatch(control):
                return reply(_mission_status(client, mission))
            if _STOP.fullmatch(control):
                if mission["state"] not in ACTIVE:
                    return reply(_mission_status(client, mission))
                client.call("POST", f"/missions/{mission['id']}/cancel", json={})
                return reply(f"Pedi a interrupção de {mission['name']}.")
            if not images and _RESUME.fullmatch(control):
                if mission.get("pending"):
                    return reply(_start_development(client, binding, mission, text, [], resume=True, platform=platform))
                return reply(_mission_status(client, mission))
        if opening := _OPEN_APP.fullmatch(text):
            app = opening["app"].strip().rstrip('.').strip()
            if project_app := _APP_FROM_PROJECT.fullmatch(app):
                return reply(_open_project_program(client, _requested_missions(project_app[1], missions, projects, mission), projects))
            if not re.match(r"^(?:chat|projeto|conversa|missao)\b", _normalized(app)):
                if _normalized(app) in {"app", "aplicativo", "programa", "um programa", "um aplicativo"}:
                    return reply("Qual programa você quer abrir no PC?")
                return reply(open_program(app))
        context = {"projetos": [{"id": item["id"], "nome": item["name"]} for item in projects.values()],
                   "missoes": [{"id": item["id"], "projeto": item["projectId"], "nome": item["name"],
                                "estado": _STATES.get(item["state"], item["state"])} for item in missions],
                   "missao_selecionada": mission["id"] if mission and len(matches) <= 1 else None,
                   "canal": platform,
                   "tem_anexos": bool(images or files),
                   "arquivos_anexados": [entry["name"] for entry in files]}
        decision = respond(key, text, history, context)
        if (decision["action"] == "capture_screen" and re.search(r"\b(?:print|screenshot|captura)\b", normalized)
                and not normalized.startswith(("como ", "me explica ", "me explique ")) and not re.search(r"\b(?:nao|nunca)\b", normalized)):
            window = decision.get("window") or ""
            if isinstance(window, str) and (not window or _normalized(window) in normalized):
                return reply(capture_screen(window))
            return reply("Qual janela você quer capturar?")
        if decision["action"] == "read_chat":
            return reply(_read_chat(client, binding, _decision_missions(decision, missions, mission), projects, key, text, history))
        if decision["action"] == "open_app":
            app = decision.get("app")
            if decision.get("mission_id") or decision.get("project_id"):
                return reply(_open_project_program(client, _decision_missions(decision, missions, mission), projects,
                                                   app if isinstance(app, str) else ""))
            if isinstance(app, str) and app.strip() and _normalized(app) in normalized:
                return reply(open_program(app))
            return reply("Qual programa você quer abrir no PC?")
        if decision["action"] == "create_project":
            name = decision.get("name", "")
            if isinstance(name, str) and name.casefold().strip() in text.casefold():
                return reply(_create_project(client, projects.values(), name))
            return reply("Qual é o nome do projeto que você quer criar?")
        if decision["action"] == "develop":
            navigation = _FORWARDED_REQUEST.search(text)
            forwarded = navigation or file_prompt
            new_chat = decision.get("new_chat") is True or bool(navigation and navigation["new"])
            target = decision.get("mission_id")
            project_id = decision.get("project_id")
            chosen = next((item for item in missions if item["id"] == target), None)
            if new_chat and not project_id and chosen:
                project_id = chosen["projectId"]
            if isinstance(project_id, str) and project_id in projects and (not target or new_chat):
                chats = [item for item in missions if item["projectId"] == project_id]
                if len(chats) > 1 and not new_chat:
                    return reply(_mission_list(chats, projects, "Em qual desses chats você quer executar o pedido?"))
                if not chats or new_chat:
                    names = {item["name"] for item in chats}
                    number = 1
                    while f"Chat {number}" in names:
                        number += 1
                    chosen = client.call("POST", f"/projects/{project_id}/missions", json={"name": f"Chat {number}"})
                else:
                    chosen = chats[0]
            elif not target and not project_id and len(matches) <= 1:
                chosen = mission
            if chosen is None:
                return reply(_mission_list(missions, projects, "Em qual missão você quer executar o pedido?") if missions
                             else "Em qual projeto você quer executar o pedido?")
            return reply(_start_development(client, binding, chosen, _development_request(text, decision, forwarded), images,
                                            files=files, owner=key, platform=platform))
        answer = decision.get("reply")
        if not isinstance(answer, str) or not answer.strip():
            return reply("Pode me explicar o que quer fazer?")
        return reply(answer)


def allowed_users(platform):
    setting, identity_format = _CHANNELS[platform]
    raw = get_env_value_prefer_dotenv(setting) or ""
    return {part for entry in raw.split(",") if identity_format.fullmatch(part := entry.strip().lower())}


def discord_channels():
    configured = get_env_value_prefer_dotenv("DISCORD_ALLOWED_CHANNELS") or ""
    return {channel for item in configured.split(",") if _DISCORD_ID.fullmatch(channel := item.strip())}


def discord_chat_allowed(user, channel, direct):
    return (user in allowed_users(Platform.DISCORD) and bool(_DISCORD_ID.fullmatch(channel))
            and (direct or channel in discord_channels()))


def _discord_private_chat(user, channel=None):
    path = get_hermes_home() / "orbita-discord-private" / f"{user}.txt"
    try:
        saved = path.read_text(encoding="ascii")
    except FileNotFoundError:
        saved = ""
    if channel is not None and channel != saved:
        save_binding(path, channel)
        return channel
    return saved if _DISCORD_ID.fullmatch(saved) else None


def execute_message_turn(port, source, agent, prompt, images, emit, history=(), *, signal_files=None):
    owner = str(source.user_id or "")
    allowed = allowed_users(source.platform)
    identities = (owner.lower(), str(getattr(source, "user_id_alt", None) or "").lower())
    authorized = next((identity for identity in identities if identity in allowed), None)
    if source.platform == Platform.DISCORD:
        channel = str(source.chat_id or "")
        direct = source.chat_type == "dm" and not getattr(source, "guild_id", None)
        if source.is_bot or not discord_chat_allowed(owner, channel, direct):
            raise RuntimeError("Use a mensagem privada do bot com seu ID autorizado em Mensagens → Discord. Para usar um canal, autorize também o ID dele.")
        if direct:
            _discord_private_chat(owner, channel)
        key = f"discord_{owner}_{channel}"
    else:
        if source.is_bot or source.chat_type != "dm" or str(source.chat_id) != owner or authorized is None:
            raise RuntimeError("O acesso às missões do Orbita está disponível no chat privado da conta pareada.")
        key = f"{source.platform.value}_{local_identity(source.platform, authorized)}"
    binding = binding_directory() / (key + ".txt")
    text = prompt.strip()
    command, _, argument = text.partition(" ")
    command = command.casefold()
    if source.platform in {Platform.SIGNAL, Platform.DISCORD}:
        model_reply = model_command(key, command, argument.strip(), source.platform.value)
        if model_reply is not None:
            emit(model_reply)
            return model_reply, "pronta"
        if signal_files or command in {"!retomar", "!print", "!abrir", "!chat", "!historico", "!histórico", "!vincular"} or not command.startswith("!"):
            return _signal_turn(port, key, binding, "Retome" if command == "!retomar" else text, images, emit, history,
                                signal_files, source.platform.value)
    if command in {"!orbita", "!ajuda"}:
        reply = _HELP
    elif command.startswith("!") and command != "!retomar":
        reply = mission_command(port, binding, command, argument.strip())
    elif not binding.is_file():
        reply = "Escolha uma missão com !missoes e !usar ID.\n\n" + _HELP
    else:
        return execute_turn(port, key, agent, text, images, emit, None, resume=command == "!retomar")

    emit(reply)
    return reply, "pronta"


def _mission_list(missions, projects, heading="Estas são suas missões. Pode falar o nome de uma delas."):
    if not missions:
        return "Crie uma missão no Orbita Desktop para começar."
    lines = []
    for mission in missions:
        project = projects.get(mission["projectId"], {}).get("name")
        name = f"{project} / {mission['name']}" if project and project != mission["name"] else mission["name"]
        lines.append(f"• {name} · {_STATES.get(mission['state'], mission['state'])}")
    return "\n".join([heading, *lines])


def _mission_status(client, mission):
    state = mission["state"]
    message = f"{mission['name']} · {_STATES.get(state, state)}"
    if state in _BLOCKERS:
        message += "\n" + _BLOCKERS[state]
    if state in {"precisa-de-voce", "pendente"}:
        detail = ""
        for entry in client.events(mission["id"]):
            if entry["kind"] == "estado":
                detail = entry["text"]
        if detail:
            message += "\n\n" + detail[:900]
    return message


def mission_command(port, binding, command, argument):
    if command not in {"!missoes", "!usar", "!status", "!parar"}:
        return _HELP
    with OrbitaClient(port) as client:
        state = client.call("GET", "/state")
        missions = state["missions"]
        if command == "!missoes":
            projects = {item["id"]: item["name"] for item in state["projects"]}
            lines = [f"{m['id'][:8]} · {projects.get(m['projectId'], 'Orbita')} / {m['name']} · {_STATES.get(m['state'], m['state'])}"
                     for m in missions]
            return "\n".join(["Escolha com !usar ID", *lines]) if lines else "Crie uma missão no Orbita Desktop para começar."
        if command == "!usar":
            matches = [m for m in missions if MISSION_ID.fullmatch(argument) and m["id"].startswith(argument)]
            if len(matches) != 1:
                return "Use !missoes e informe o ID de uma missão com !usar ID."
            mission = matches[0]
            save_binding(binding, mission["id"])
            return f"Missão selecionada: {mission['name']}. Envie o pedido aqui."

        selected = binding.read_text(encoding="ascii") if binding.is_file() else ""
        mission = next((item for item in missions if item["id"] == selected), None)
        if not mission:
            return "Escolha uma missão com !missoes e !usar ID."
        if command == "!parar":
            client.call("POST", f"/missions/{mission['id']}/cancel", json={})
            return f"Pedi a interrupção de {mission['name']}."
        return _mission_status(client, mission)


def _blocked_missions(port):
    with OrbitaClient(port) as client:
        return [mission for mission in client.call("GET", "/state")["missions"] if mission["state"] in _BLOCKERS]


def local_identity(platform, identity):
    return identity if platform == Platform.TELEGRAM else hashlib.sha256(identity.encode("ascii")).hexdigest()


def _subscription_missions(owner):
    missions = set()
    for path in queued_results(owner):
        try:
            missions.add(json.loads(path.read_text(encoding="utf-8"))["missionId"])
        except FileNotFoundError:
            continue
    return missions


def _ready_results(port, owner):
    pending = list(queued_results(owner))
    if not pending:
        return []
    ready = []
    with OrbitaClient(port) as client:
        for path in pending:
            entry = json.loads(path.read_text(encoding="utf-8"))
            mission_id = entry["missionId"]
            if not MISSION_ID.fullmatch(mission_id) or not re.fullmatch(r"[a-f0-9]{32}", path.stem):
                continue
            snapshot = client.call("GET", f"/missions/{mission_id}/signal-activity", params={"requestId": path.stem})
            turn = next((turn for turn in reversed(snapshot["turns"]) if turn["requestId"] == path.stem), None)
            if turn is None:
                if time() - entry["queuedAt"] > 15 * 60:
                    path.unlink(missing_ok=True)
                continue
            terminal = turn.get("terminalState")
            if terminal in {"concluida", "pronta"}:
                answer = turn["reply"].strip() or turn.get("detail") or "O pedido foi concluído."
                ready.append((path, _signal_text(f'Resultado final · {entry["name"]}\n\n{answer}')))
            elif terminal in _BLOCKERS and time() - entry["queuedAt"] > 7 * 86400:
                path.unlink(missing_ok=True)
    return ready


async def _send_results(port, adapter, recipient, owner=None):
    owner = owner or "signal_" + local_identity(Platform.SIGNAL, recipient)
    for path, text in await asyncio.to_thread(_ready_results, port, owner):
        if (await adapter.send(recipient, text)).success:
            path.unlink(missing_ok=True)


async def _send_alerts(platform, adapter, recipient, missions):
    identity = local_identity(platform, recipient)
    notice_file = get_hermes_home() / f"orbita-{platform.value}-alerts-{identity}.json"
    try:
        delivered = json.loads(notice_file.read_text(encoding="utf8"))
    except FileNotFoundError:
        delivered = {}
    for mission in missions:
        mission_id = mission["id"]
        fingerprint = mission["state"] + ":" + mission["updated"]
        if delivered.get(mission_id) == fingerprint:
            continue
        guidance = (f'Pode me perguntar "Como está a missão {mission["name"]}?" ou pedir para eu continuar quando estiver resolvido.'
                    if platform in {Platform.SIGNAL, Platform.DISCORD} else
                    f"Envie !usar {mission_id[:8]} e depois !status para consultar. Use !retomar após resolver o bloqueio.")
        text = f"Orbita · {mission['name']}\n{_BLOCKERS[mission['state']]}\n\n{guidance}"
        result = await adapter.send(recipient, text)
        if result.success:
            delivered[mission_id] = fingerprint
            temporary = notice_file.with_suffix(".tmp")
            temporary.write_text(json.dumps(delivered), encoding="utf8")
            temporary.replace(notice_file)


async def _watch(runner, port):
    while True:
        channels = [(platform, runner.adapters[platform], allowed_users(platform))
                    for platform in _CHANNELS if platform in runner.adapters]
        if any(recipients for _, _, recipients in channels):
            try:
                missions = await asyncio.to_thread(_blocked_missions, port)
            except (RuntimeError, OSError, ValueError, httpx.HTTPError) as error:
                _LOGGER.debug("Orbita could not load mission alerts (%s)", type(error).__name__)
            else:
                for platform, adapter, recipients in channels:
                    try:
                        if platform == Platform.DISCORD:
                            paired_channels = discord_channels()
                            for user in recipients:
                                direct = _discord_private_chat(user)
                                for channel in (paired_channels | {direct} if direct else paired_channels):
                                    key = f"discord_{user}_{channel}"
                                    watched = _subscription_missions(key)
                                    await _send_alerts(platform, adapter, channel, [mission for mission in missions if mission["id"] in watched])
                                    await _send_results(port, adapter, channel, key)
                        else:
                            for owner in recipients:
                                await _send_alerts(platform, adapter, owner, missions)
                                if platform == Platform.SIGNAL:
                                    await _send_results(port, adapter, owner)
                    except (RuntimeError, OSError, ValueError, httpx.HTTPError) as error:
                        _LOGGER.debug("Orbita delivery failed on %s (%s)", platform.value, type(error).__name__)
        await asyncio.sleep(5)


def start_alerts(runner):
    port = bridge_port()
    return asyncio.create_task(_watch(runner, port)) if port is not None and any(map(allowed_users, _CHANNELS)) else None
