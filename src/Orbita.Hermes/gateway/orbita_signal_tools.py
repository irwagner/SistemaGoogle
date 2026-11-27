"""Windows actions explicitly requested by the paired Signal user."""

import ctypes
import os
import re
import shutil
import sys
import unicodedata
from contextlib import suppress
from dataclasses import dataclass
from pathlib import Path
from time import time
from uuid import uuid4

from hermes_constants import get_hermes_home


_ALIASES = {
    "bloco de notas": "notepad.exe", "notepad": "notepad.exe", "calculadora": "calc.exe", "calculator": "calc.exe",
    "paint": "mspaint.exe", "explorador de arquivos": "explorer.exe", "explorador": "explorer.exe",
    "microsoft edge": "msedge.exe", "edge": "msedge.exe", "google chrome": "chrome.exe", "chrome": "chrome.exe",
    "visual studio code": "code.exe", "vs code": "code.exe", "vscode": "code.exe", "firefox": "firefox.exe",
}


@dataclass(frozen=True)
class SignalPhoto:
    path: Path
    caption: str

    def response(self):
        return f'{self.caption}\n\nMEDIA:"{self.path.as_posix()}"'


def _name(value):
    return "".join(char for char in unicodedata.normalize("NFKD", value.casefold()) if not unicodedata.combining(char)).strip()


def _require_windows():
    if sys.platform != "win32":
        raise RuntimeError("Este comando está disponível no PC Windows onde o Orbita está rodando.")


def _require_visible_desktop():
    from ctypes import wintypes

    user = ctypes.WinDLL("user32", use_last_error=True)
    user.OpenInputDesktop.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    user.OpenInputDesktop.restype = wintypes.HANDLE
    user.GetUserObjectInformationW.argtypes = (wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID, wintypes.DWORD,
                                                ctypes.POINTER(wintypes.DWORD))
    user.GetUserObjectInformationW.restype = wintypes.BOOL
    user.CloseDesktop.argtypes = (wintypes.HANDLE,)
    user.CloseDesktop.restype = wintypes.BOOL
    desktop = user.OpenInputDesktop(0, False, 1)
    if desktop:
        try:
            name = ctypes.create_unicode_buffer(256)
            if user.GetUserObjectInformationW(desktop, 2, name, ctypes.sizeof(name), None) and name.value.casefold() == "default":
                return
        finally:
            user.CloseDesktop(desktop)
    raise RuntimeError("A área de trabalho do Windows está bloqueada ou indisponível. Desbloqueie o PC para tirar o print.")


def _find_window(target):
    import win32gui

    matches = []
    query = _name(target)

    def visit(handle, _):
        if win32gui.IsWindowVisible(handle) and not win32gui.IsIconic(handle):
            title = win32gui.GetWindowText(handle).strip()
            if title and query in _name(title):
                matches.append((handle, title))
        return True

    win32gui.EnumWindows(visit, None)
    exact = [item for item in matches if _name(item[1]) == query]
    options = exact or matches
    if len(options) == 1:
        return options[0]
    if options:
        raise RuntimeError("Encontrei várias janelas. Informe um título mais específico. " + ", ".join(title[:100] for _, title in options[:5]))
    raise RuntimeError(f'Não encontrei uma janela visível com o título “{target}”.')


def capture_screen(window=""):
    _require_windows()
    _require_visible_desktop()
    try:
        from PIL import ImageGrab
    except ImportError as error:
        raise RuntimeError("O componente de captura do Orbita precisa ser instalado no PC.") from error
    if window:
        handle, title = _find_window(window)
        caption = f"Captura da janela {title}."
    else:
        handle, caption = None, "Captura da tela principal do PC."
    try:
        with ImageGrab.grab(window=handle) as image:
            image.thumbnail((2400, 2400))
            folder = get_hermes_home() / "cache" / "screenshots"
            folder.mkdir(parents=True, exist_ok=True)
            for old in folder.glob("orbita-signal-*.jpg"):
                with suppress(OSError):
                    if time() - old.stat().st_mtime > 3600:
                        old.unlink()
            target = folder / f"orbita-signal-{uuid4().hex}.jpg"
            with image.convert("RGB") as rgb:
                rgb.save(target, format="JPEG", quality=89)
    except OSError as error:
        raise RuntimeError("O Windows não conseguiu capturar essa tela. Verifique se ela está aberta no PC.") from error
    return SignalPhoto(target, caption)


def _registered_app(executable):
    import winreg

    key = r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths" + "\\" + executable
    for root in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        for view in (winreg.KEY_READ, winreg.KEY_READ | winreg.KEY_WOW64_32KEY):
            with suppress(OSError):
                with winreg.OpenKey(root, key, 0, view) as entry:
                    value, _ = winreg.QueryValueEx(entry, "")
                    if not isinstance(value, str):
                        continue
                    candidate = Path(os.path.expandvars(value.strip('"')))
                    if candidate.is_absolute() and candidate.suffix.lower() == ".exe" and candidate.is_file():
                        return candidate
    return None


def _start_menu_shortcuts(name):
    for key in ("APPDATA", "PROGRAMDATA"):
        if root := os.environ.get(key):
            folder = Path(root) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
            if folder.is_dir():
                yield from (item for item in folder.rglob("*.lnk") if _name(item.stem) == name)


def open_program(target):
    _require_windows()
    target = target.strip().strip('"“”')
    if not target or len(target) > 1024 or any(char in target for char in '\r\n\0'):
        raise RuntimeError("Informe o nome do programa ou o caminho de um arquivo .exe.")
    label = target
    normalized = _name(target)
    if re.match(r"^[A-Za-z]:[/\\]", target):
        path = Path(target)
        if path.suffix.lower() != ".exe" or not path.is_file():
            raise RuntimeError(f'Não encontrei um executável em “{target}”.')
        label = path.name
    else:
        if any(mark in target for mark in "/\\:"):
            raise RuntimeError("Informe o nome de um programa instalado ou um caminho local .exe.")
        exe = _ALIASES.get(normalized, target if normalized.endswith(".exe") else target + ".exe")
        located = shutil.which(exe)
        path = Path(located) if located else _registered_app(exe)
        if path is None:
            path = next(_start_menu_shortcuts(normalized), None)
        if path is None:
            raise RuntimeError(f'Não encontrei “{target}” nos programas do PC. Você pode informar o caminho do .exe.')
    try:
        path = path.resolve()
        os.startfile(str(path), cwd=str(path.parent))
    except OSError as error:
        raise RuntimeError(f'O Windows não conseguiu abrir “{label}”.') from error
    return f'Pedi ao Windows para abrir “{label}”.'
