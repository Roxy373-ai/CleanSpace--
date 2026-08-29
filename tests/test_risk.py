import os
from pathlib import Path

from cleanspace.models import RiskLevel
from cleanspace.risk import classify_path, normalized_path


def test_normalization_is_case_insensitive_on_windows():
    first = normalized_path(r"C:\Windows\Temp\..\Logs")
    second = normalized_path(r"c:\windows\logs")
    if os.name == "nt":
        assert first == second


def test_system_root_is_blocked():
    decision = classify_path(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "kernel32.dll")
    assert decision.level is RiskLevel.BLOCKED
    assert not decision.direct_delete_allowed


def test_temp_is_safe():
    path = Path(os.environ.get("TEMP", r"C:\Users\Test\AppData\Local\Temp")) / "cleanspace.tmp"
    decision = classify_path(path)
    assert decision.level is RiskLevel.SAFE
    assert decision.direct_delete_allowed


def test_personal_drive_file_is_caution():
    decision = classify_path(r"D:\Personal\movie.mp4")
    assert decision.level is RiskLevel.CAUTION
    assert decision.direct_delete_allowed


def test_browser_cache_is_safe_but_cookies_are_not(monkeypatch, tmp_path):
    local = Path(r"D:\CleanSpaceSyntheticUser\Local")
    roaming = Path(r"D:\CleanSpaceSyntheticUser\Roaming")
    monkeypatch.setenv("LOCALAPPDATA", str(local))
    monkeypatch.setenv("APPDATA", str(roaming))
    cache = local / "Google" / "Chrome" / "User Data" / "Default" / "Cache" / "data.bin"
    cookies = local / "Google" / "Chrome" / "User Data" / "Default" / "Network" / "Cookies"
    assert classify_path(cache).level is RiskLevel.SAFE
    assert classify_path(cookies).level is not RiskLevel.SAFE
