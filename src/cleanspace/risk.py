from __future__ import annotations

import os
from pathlib import Path

from .models import RiskDecision, RiskLevel


def normalized_path(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(os.fspath(path))).rstrip("\\/")


def _is_within(path: str, root: str) -> bool:
    try:
        return os.path.commonpath((path, root)) == root
    except ValueError:
        return False


def protected_roots() -> tuple[str, ...]:
    candidates = {
        os.environ.get("SystemRoot", r"C:\Windows"),
        os.environ.get("ProgramFiles", r"C:\Program Files"),
        os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
        os.environ.get("ProgramData", r"C:\ProgramData"),
        r"C:\System Volume Information",
        r"C:\$Recycle.Bin",
    }
    return tuple(sorted({normalized_path(item) for item in candidates if item}))


def safe_cache_roots() -> tuple[str, ...]:
    local = os.environ.get("LOCALAPPDATA", "")
    roaming = os.environ.get("APPDATA", "")
    temp = os.environ.get("TEMP", "")
    candidates = {
        temp,
        os.path.join(local, "Temp") if local else "",
        os.path.join(local, "CrashDumps") if local else "",
        os.path.join(local, "D3DSCache") if local else "",
        os.path.join(local, "NVIDIA", "DXCache") if local else "",
        os.path.join(local, "NVIDIA", "GLCache") if local else "",
        os.path.join(local, "AMD", "DxCache") if local else "",
        os.path.join(local, "Microsoft", "Windows", "Explorer") if local else "",
        os.path.join(local, "Microsoft", "Windows", "INetCache") if local else "",
        os.path.join(roaming, "Adobe", "Common", "Media Cache Files") if roaming else "",
        os.path.join(local, "Adobe", "Common", "Media Cache Files") if local else "",
        os.path.join(roaming, "discord", "Cache") if roaming else "",
        os.path.join(roaming, "discord", "Code Cache") if roaming else "",
        os.path.join(roaming, "discord", "GPUCache") if roaming else "",
        os.path.join(roaming, "Code", "Cache") if roaming else "",
        os.path.join(roaming, "Code", "CachedData") if roaming else "",
        os.path.join(roaming, "Code", "Code Cache") if roaming else "",
        os.path.join(roaming, "Code", "GPUCache") if roaming else "",
    }
    for browser in (
        os.path.join(local, "Google", "Chrome", "User Data") if local else "",
        os.path.join(local, "Microsoft", "Edge", "User Data") if local else "",
    ):
        if not browser:
            continue
        for profile in ("Default", *(f"Profile {index}" for index in range(1, 11))):
            for cache_name in ("Cache", "Code Cache", "GPUCache"):
                candidates.add(os.path.join(browser, profile, cache_name))
    return tuple(sorted({normalized_path(item) for item in candidates if item}))


def classify_path(path: str | Path, *, is_directory: bool = False) -> RiskDecision:
    candidate = normalized_path(path)
    for root in safe_cache_roots():
        if _is_within(candidate, root):
            return RiskDecision(RiskLevel.SAFE, "risk.reason.temp", True)
    for root in protected_roots():
        if _is_within(candidate, root):
            return RiskDecision(RiskLevel.BLOCKED, "risk.reason.system", False)
    if os.path.splitdrive(candidate)[0]:
        return RiskDecision(RiskLevel.CAUTION, "risk.reason.personal", True)
    return RiskDecision(RiskLevel.BLOCKED, "risk.reason.unknown", False)


def is_direct_delete_allowed(path: str | Path) -> bool:
    return classify_path(path).direct_delete_allowed
