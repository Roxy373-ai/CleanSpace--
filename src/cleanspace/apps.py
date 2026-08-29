from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path

from .models import InstalledApp

try:
    import winreg
except ImportError:  # pragma: no cover
    winreg = None


UNINSTALL_ROOTS = (
    (getattr(winreg, "HKEY_LOCAL_MACHINE", None), r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    (getattr(winreg, "HKEY_LOCAL_MACHINE", None), r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    (getattr(winreg, "HKEY_CURRENT_USER", None), r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
)


def _value(key, name: str, default=""):
    try:
        return winreg.QueryValueEx(key, name)[0]
    except OSError:
        return default


def installed_apps() -> list[InstalledApp]:
    if winreg is None:
        return []
    found: dict[tuple[str, str], InstalledApp] = {}
    for hive, root_path in UNINSTALL_ROOTS:
        if hive is None:
            continue
        try:
            root = winreg.OpenKey(hive, root_path)
        except OSError:
            continue
        with root:
            for index in range(winreg.QueryInfoKey(root)[0]):
                try:
                    key_name = winreg.EnumKey(root, index)
                    with winreg.OpenKey(root, key_name) as key:
                        name = str(_value(key, "DisplayName", "")).strip()
                        if not name or int(_value(key, "SystemComponent", 0) or 0) == 1:
                            continue
                        size_kb = int(_value(key, "EstimatedSize", 0) or 0)
                        app = InstalledApp(
                            name=name, publisher=str(_value(key, "Publisher", "")),
                            version=str(_value(key, "DisplayVersion", "")),
                            install_date=str(_value(key, "InstallDate", "")),
                            install_location=str(_value(key, "InstallLocation", "")),
                            estimated_size=size_kb * 1024,
                            uninstall_command=str(_value(key, "UninstallString", "")),
                            icon_path=str(_value(key, "DisplayIcon", "")),
                            registry_key=f"{root_path}\\{key_name}",
                        )
                        found[(app.name.casefold(), app.version.casefold())] = app
                except (OSError, ValueError):
                    continue
    return sorted(found.values(), key=lambda app: (app.name.casefold(), app.version.casefold()))


def extract_executable(command: str) -> str:
    command = os.path.expandvars(command.strip())
    if not command:
        return ""
    match = re.match(r'^"([^"]+)"', command)
    executable = match.group(1) if match else command.split(maxsplit=1)[0]
    return executable.strip('"')


def extract_icon_path(display_icon: str) -> str:
    value = os.path.expandvars(display_icon.strip())
    if not value:
        return ""
    quoted = re.match(r'^"([^"]+)"', value)
    if quoted:
        return quoted.group(1)
    path = value.rsplit(",", 1)[0] if re.search(r",\s*-?\d+\s*$", value) else value
    return path.strip().strip('"')


def uninstall_command_is_valid(command: str) -> bool:
    executable = extract_executable(command)
    if not executable:
        return False
    if Path(executable).name.casefold() in {"msiexec", "msiexec.exe"}:
        return True
    return Path(executable).is_file() and Path(executable).suffix.casefold() in {".exe", ".com"}


def launch_uninstaller(command: str) -> None:
    if not uninstall_command_is_valid(command):
        raise ValueError("invalid uninstall command")
    subprocess.Popen(
        os.path.expandvars(command), shell=False,
        creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
    )
