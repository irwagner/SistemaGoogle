"""Execute Hermes Desktop turns through the Orbita service on this device."""

from contextlib import ExitStack, suppress
from datetime import datetime
import json
import os
from pathlib import Path
import re
from time import monotonic

import httpx
from hermes_constants import get_hermes_home


MODEL = "antigravity"
ACTIVE = {"conversando", "executando"}
MISSION_ID = re.compile(r"[a-z0-9]{6,40}")
SESSION_ID = re.compile(r"[A-Za-z0-9_-]{1,120}")


class OrbitaClient:
    def __init__(self, port):
        if not 1024 <= port <= 65535:
            raise ValueError("Porta do Orbita inválida.")
        self.http = httpx.Client(base_url=f"http://127.0.0.1:{port}", trust_env=False, timeout=15)
        self.token = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.http.close()

    def headers(self):
        if self.token is None:
            page = self.http.get("/")
            match = re.search(r'name="orbita-token" content="([a-f0-9]{64})"', page.text)
            if not page.is_success or not match:
                raise RuntimeError("O servidor local do Orbita não respondeu na porta configurada.")
            self.token = match[1]
        return {"X-Orbita-Token": self.token}

    def call(self, method, path, **kwargs):
        for attempt in range(2):
            response = self.http.request(method, "/api" + path, headers=self.headers(), **kwargs)
            if response.status_code == 401 and attempt == 0 and not kwargs.get("files"):
                self.token = None
                continue
            break
        if not response.is_success:
            try:
                reason = response.json().get("error")
            except ValueError:
                reason = None
            raise RuntimeError(reason or f"O servidor do Orbita respondeu com HTTP {response.status_code}.")
        return response.json() if response.content else None

    def events(self, mission_id, after=0, *, time_budget=None):
        try:
            with self.http.stream("GET", f"/api/missions/{mission_id}/events", params={"after": after},
                                  headers=self.headers(), timeout=httpx.Timeout(15, read=1.25)) as response:
                response.raise_for_status()
                deadline = monotonic() + time_budget if time_budget is not None else None
                for line in response.iter_lines():
                    if deadline is not None and monotonic() >= deadline:
                        return
                    if line.startswith("data: "):
                        yield json.loads(line[6:])
        except httpx.ReadTimeout:
            return


def bridge_port():
    try:
        return int((get_hermes_home() / "orbita-local-port").read_text(encoding="ascii"))
    except FileNotFoundError:
        return None


def turn_preferences(mission):
    try:
        saved = json.loads((get_hermes_home() / "orbita-preferences.json").read_text(encoding="utf8"))
    except FileNotFoundError:
        saved = {}
    accounts = mission.get("accountIds") or []
    if saved.get("mode") == "cli":
        accounts = []
    elif saved.get("mode") == "pool":
        accounts = saved["accounts"]

    model = saved.get("model", "mission")
    if model == "mission":
        model = mission.get("requestedModel")
    elif model == "auto":
        model = None
    effort = saved.get("effort", "auto")
    return {"accounts": accounts, "model": model, "effort": None if effort == "auto" else effort}


def desktop_full_access():
    try:
        return json.loads((get_hermes_home() / "orbita-permission.json").read_text(encoding="utf8")) is True
    except FileNotFoundError:
        return False


def as_transcript(events):
    messages = []
    for event in events:
        kind = event["kind"]
        if kind == "usuario":
            text = event["text"].rsplit("\n\nContexto do pedido no Signal:\n", 1)[-1].strip()
            messages.append({"role": "user", "content": text, "at": event["at"], "sequence": event["sequence"]})
        elif kind == "mensagem":
            if messages and messages[-1]["role"] == "assistant":
                messages[-1]["content"] += event["text"]
            else:
                messages.append({"role": "assistant", "content": event["text"], "at": event["at"], "sequence": event["sequence"]})
    return messages


def binding_directory():
    return get_hermes_home() / "orbita-missions"


def register_projects(state):
    from hermes_cli import projects_db

    with projects_db.connect_closing() as db:
        for project in state["projects"]:
            slug = "orbita-" + project["id"]
            existing = projects_db.get_project(db, slug)
            project_id = existing.id if existing else projects_db.create_project(
                db, name=project["name"], slug=slug, primary_path=project["workspace"], allow_duplicate_path=True)
            for mission in state["missions"]:
                if mission["projectId"] == project["id"]:
                    projects_db.add_folder(db, project_id, mission["workspace"], label=mission["name"])


def import_missions():
    from hermes_state import SessionDB

    with OrbitaClient(bridge_port()) as client:
        state = client.call("GET", "/state")
        register_projects(state)
        db = SessionDB()
        try:
            bound = desktop_bindings(db)
            for mission in state["missions"]:
                mission_id = mission["id"]
                if MISSION_ID.fullmatch(mission_id) and mission_id not in bound:
                    ensure_desktop_session(db, client, mission)
        finally:
            db.close()
    return ({"projects": state["projects"], "missions": state["missions"]})


def desktop_bindings(db):
    return {entry.read_text(encoding="ascii"): entry.stem for entry in binding_directory().glob("*.txt")
            if SESSION_ID.fullmatch(entry.stem) and db.get_session(entry.stem) is not None}


def ensure_desktop_session(db, client, mission):
    mission_id = mission["id"]
    session_id = "orbita_" + mission_id
    if db.get_session(session_id) is not None:
        return session_id
    messages = as_transcript(client.events(mission_id))
    db.create_session(session_id, "desktop", model=MODEL, cwd=mission["workspace"])
    db.set_session_title(session_id, mission["name"])
    for row in messages:
        db.append_message(session_id, row["role"], row["content"],
                          timestamp=datetime.fromisoformat(row["at"]).timestamp(),
                          message_uid=f"{session_id}_{row['sequence']}")
    return session_id


def open_desktop_mission(mission_id):
    from hermes_state import SessionDB

    if not MISSION_ID.fullmatch(mission_id):
        raise ValueError("Identificador de missão inválido.")
    with OrbitaClient(bridge_port()) as client:
        state = client.call("GET", "/state")
        mission = next((item for item in state["missions"] if item["id"] == mission_id), None)
        if mission is None:
            raise RuntimeError("O chat não está disponível no Orbita.")
        register_projects({"projects": [project for project in state["projects"] if project["id"] == mission["projectId"]],
                           "missions": [mission]})
        with SessionDB() as db:
            return desktop_bindings(db).get(mission_id) or ensure_desktop_session(db, client, mission)


def normalize_path(path):
    return os.path.normcase(os.path.normpath(path))


def resolve_mission(client, key, cwd, text):
    snapshot = client.call("GET", "/state")
    if not SESSION_ID.fullmatch(key or ""):
        raise RuntimeError("A conversa ainda não tem um identificador local.")
    binding = binding_directory() / (key + ".txt")
    bound = binding.read_text(encoding="ascii") if binding.is_file() else None
    for mission in snapshot["missions"]:
        if key == "orbita_" + mission["id"] or bound == mission["id"]:
            return mission
    if bound or key.startswith("orbita_"):
        raise RuntimeError("A missão ligada a esta conversa não está disponível no Orbita.")
    if cwd:
        rooted = normalize_path(cwd)
        for item in snapshot["missions"]:
            if normalize_path(item["workspace"]) == rooted:
                save_binding(binding, item["id"])
                return item
        project_id = next((item["id"] for item in snapshot["projects"]
                           if normalize_path(item["workspace"]) == rooted), None)
        if project_id:
            name = re.sub(r"\s+", " ", text).strip()[:72] or "Nova missão"
            mission = client.call("POST", f"/projects/{project_id}/missions", json={"name": name})
            save_binding(binding, mission["id"])
            return mission
    raise RuntimeError("Escolha a pasta de um projeto do Orbita nesta conversa e envie novamente.")


def save_binding(binding, mission_id):
    binding.parent.mkdir(parents=True, exist_ok=True)
    pending = binding.with_suffix(".tmp")
    pending.write_text(mission_id, encoding="ascii")
    pending.replace(binding)


def execute_turn(port, session_id, agent, prompt, images, emit, cwd, *, resume=False, conversation=False):
    from agent.interrupt_control import USER_INTERRUPT_REASONS

    if not isinstance(prompt, str) or not prompt.strip():
        raise ValueError("Escreva o pedido para o Antigravity.")
    text = prompt.strip()
    explicit_execute = text.casefold().startswith("executar:")
    if explicit_execute:
        text = text.partition(":")[2].strip()
    if not text:
        raise ValueError("Escreva o pedido que o Antigravity deve executar.")
    full_access = desktop_full_access()
    result = ""
    terminal = None
    last_status = ""
    with OrbitaClient(port) as client:
        mission = resolve_mission(client, session_id, cwd, text)
        mission_id = mission["id"]
        if not MISSION_ID.fullmatch(mission_id):
            raise RuntimeError("Identificador de missão inválido.")
        route = f"/missions/{mission_id}"
        if mission["state"] in ACTIVE:
            raise RuntimeError("Esta missão já está em execução no Orbita.")
        cursor = max((entry["sequence"] for entry in client.events(mission_id)), default=0)
        if resume:
            client.call("POST", route + "/resume", json={"fullAccess": full_access})
        else:
            payload = {"text": text, "execute": explicit_execute or (full_access and not conversation), "fullAccess": full_access,
                       **turn_preferences(mission)}
            if images:
                with ExitStack() as stack:
                    attached = [("files", (Path(path).name, stack.enter_context(Path(path).open("rb")))) for path in images]
                    client.call("POST", route + "/turns/attachments", data={"turn": json.dumps(payload)}, files=attached)
            else:
                client.call("POST", route + "/turns", json=payload)

        cancelled = False
        while terminal is None:
            for entry in client.events(mission_id, cursor):
                cursor = max(cursor, entry["sequence"])
                if entry["kind"] == "mensagem":
                    result += entry["text"]
                    emit(entry["text"])
                elif entry["kind"] == "estado":
                    last_status = entry["text"]
            if (agent._interrupt_requested and agent._tool_interrupt_reason in USER_INTERRUPT_REASONS
                    and not cancelled):
                if client.call("GET", route)["state"] in ACTIVE:
                    client.call("POST", route + "/cancel", json={})
                agent.clear_interrupt(hard_cancel=True)
                cancelled = True
            settled = client.call("GET", route)
            if settled["state"] not in ACTIVE:
                terminal = settled["state"]
                # A state write can precede its last SSE batch.
                for entry in client.events(mission_id, cursor):
                    if entry["kind"] == "mensagem":
                        result += entry["text"]
                        emit(entry["text"])
                    elif entry["kind"] == "estado":
                        last_status = entry["text"]

    if not result and last_status:
        result = last_status
        emit(result)
    return result, terminal


def run_gateway_turn(port, session_id, agent, prompt, images, history, source=None, *, signal_files=None):
    from agent.runtime_cwd import scoped_session_cwd
    from gateway.config import Platform
    from gateway.run_turn_runner import _NoStreamConsumer
    from .orbita_signal_tools import SignalPhoto

    def emit(part):
        if agent.stream_delta_callback:
            with suppress(_NoStreamConsumer):
                agent.stream_delta_callback(part)

    try:
        if source is not None and source.platform in {Platform.TELEGRAM, Platform.SIGNAL, Platform.DISCORD}:
            from .orbita_messaging import execute_message_turn
            result, terminal = execute_message_turn(port, source, agent, prompt, images, emit, history, signal_files=signal_files)
        else:
            result, terminal = execute_turn(port, session_id, agent, prompt, images, emit, scoped_session_cwd())
    except (RuntimeError, ValueError, OSError, httpx.HTTPError) as error:
        result = f"Orbita: {error}"
        emit(result)
        terminal = "erro"
    final = result
    if isinstance(result, SignalPhoto):
        final, result = result.response(), result.caption
    texts = [*history, {"role": "user", "content": prompt}]
    agent._persist_user_message_idx = len(history)
    if result:
        texts.append({"role": "assistant", "content": result})
    agent._flush_messages_to_session_db(texts, history)
    return {"final_response": final, "messages": texts, "interrupted": terminal == "interrompida",
            "current_turn_user_idx": len(history)}


if __name__ == "__main__":
    import sys

    if len(sys.argv) == 3 and sys.argv[1] == "--open-mission":
        print(json.dumps({"sessionId": open_desktop_mission(sys.argv[2])}))
    else:
        result = import_missions()
        print(json.dumps({name: len(entries) for name, entries in result.items()}))
