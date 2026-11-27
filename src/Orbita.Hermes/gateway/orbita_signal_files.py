"""Carry Signal files and private result subscriptions across gateway turns."""

import json
from pathlib import Path, PureWindowsPath
from unicodedata import normalize
from time import time
from uuid import uuid4

from hermes_constants import get_hermes_home


_FILE_ONLY = "Arquivo recebido pelo Signal."
_EXTENSIONS = {
    "application/pdf": ".pdf", "text/plain": ".txt", "text/csv": ".csv", "application/json": ".json",
    "text/x-signal-plain": ".txt",
    "image/jpeg": ".jpg", "image/png": ".png", "image/gif": ".gif", "image/webp": ".webp",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document": ".docx",
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": ".xlsx",
}
_FILE_LIMIT = 5 * 1024 * 1024
_TOTAL_LIMIT = 10 * 1024 * 1024


def _cached_text(entry):
    try:
        checked = validate_files([entry])[0]
        text = Path(checked["path"]).read_text(encoding="utf-8-sig").strip()
        return text if "\0" not in text else ""
    except (OSError, ValueError, RuntimeError):
        return ""


def _same_message(caption, text):
    if not caption:
        return True
    caption = normalize("NFC", caption.replace("\r\n", "\n"))
    text = normalize("NFC", text)
    prefix = caption.rstrip(" .…")
    return caption == text or (len(prefix) >= 80 and text.startswith(prefix))


def prepare_signal_files(runner, session_key, event, platform="signal"):
    pending = getattr(runner, "_orbita_pending_signal_files", None)
    if pending is None:
        pending = runner._orbita_pending_signal_files = {}
    pending.pop(session_key, None)
    paths = event.media_urls
    kinds = event.media_types
    if paths and all(i < len(kinds) and kinds[i].startswith("audio/") for i in range(len(paths))):
        return None  # Keep the gateway's voice-note transcription flow.
    raw = event.raw_message if isinstance(event.raw_message, dict) else {}
    expected = getattr(event, "_orbita_attachment_count", raw.get("orbita_attachment_count", len(paths)))
    if not paths and not expected:
        return None
    received = []
    caption = (event.text or "").strip()
    for index, raw_path in enumerate(paths):
        label = event.media_filenames[index] if index < len(event.media_filenames) else ""
        mime = (kinds[index] if index < len(kinds) else "").split(";", 1)[0].strip().casefold()
        path = Path(raw_path)
        name = PureWindowsPath(label).name if isinstance(label, str) and label else ""
        if not name or not Path(name).suffix:
            extension = _EXTENSIONS.get(mime, path.suffix)
            name = (name or f"anexo-{platform}-{index + 1}") + extension
        entry = {"path": str(path), "name": name}
        signal_text = platform == "signal" and (mime == "text/x-signal-plain" or
                      (Path(name).suffix.casefold() == ".bin" and len(caption) >= 80))
        discord_paste = platform == "discord" and name.casefold() == "message.txt" and not caption
        if signal_text or discord_paste:
            pasted = _cached_text(entry)
            if pasted and _same_message(caption, pasted):
                caption = pasted
                continue
            if signal_text and mime == "text/x-signal-plain" and pasted:
                caption = "\n\n".join(part for part in (caption, pasted) if part)
                continue
        received.append(entry)
    caption = caption or (_FILE_ONLY if platform == "signal" else "Arquivo recebido pelo Discord.")
    pending[session_key] = (received, expected > len(paths), caption)
    return caption


def consume_signal_files(runner, session_key):
    return getattr(runner, "_orbita_pending_signal_files", {}).pop(session_key, None)


def validate_files(files):
    if len(files) > 6:
        raise RuntimeError("Envie até 6 arquivos em cada pedido.")
    total = 0
    home = get_hermes_home()
    allowed = [home / "cache" / name for name in ("images", "documents", "audio", "videos")]
    allowed += [home / name for name in ("image_cache", "document_cache", "audio_cache", "video_cache")]
    roots = [root.resolve() for root in allowed]
    result = []
    for entry in files:
        path = Path(entry["path"])
        try:
            path = path.resolve(strict=True)
            if not path.is_file() or not any(path.is_relative_to(root) for root in roots):
                raise ValueError("Anexo fora do cache de entrada.")
            size = path.stat().st_size
        except (OSError, ValueError) as error:
            raise RuntimeError("O anexo saiu do cache. Reenvie o arquivo.") from error
        if not 0 < size <= _FILE_LIMIT:
            raise RuntimeError("Cada anexo deve conter dados e ter até 5 MB.")
        total += size
        result.append({"path": str(path), "name": entry["name"]})
    if total > _TOTAL_LIMIT:
        raise RuntimeError("Os anexos juntos podem ter até 10 MB.")
    return result


def write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(f".{uuid4().hex}.tmp")
    try:
        temp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        temp.replace(path)
    finally:
        temp.unlink(missing_ok=True)


def _inbox(owner):
    return get_hermes_home() / "orbita-signal-inbox" / f"{owner}.json"


def pending_files(owner):
    try:
        return json.loads(_inbox(owner).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {"files": []}


def keep_files(owner, files, prompt=""):
    write_json(_inbox(owner), {"files": files, "prompt": prompt})


def clear_files(owner):
    _inbox(owner).unlink(missing_ok=True)


def result_subscription(owner, mission):
    receipt = uuid4().hex
    path = get_hermes_home() / "orbita-signal-results" / owner / f"{receipt}.json"
    write_json(path, {"missionId": mission["id"], "name": mission["name"], "queuedAt": time()})
    return receipt, path


def queued_results(owner):
    return (get_hermes_home() / "orbita-signal-results" / owner).glob("*.json")
