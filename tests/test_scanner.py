import threading
from pathlib import Path

from cleanspace.models import ScanOptions
from cleanspace.scanner import ScannerEngine


def test_scanner_handles_unicode_and_nested_files(tmp_path):
    nested = tmp_path / "中文" / "한국어"
    nested.mkdir(parents=True)
    expected = nested / "内容.txt"
    expected.write_text("hello", encoding="utf-8")
    pause = threading.Event()
    pause.set()
    engine = ScannerEngine(ScanOptions((tmp_path,)), pause, threading.Event())
    records = list(engine.records())
    assert [record.path for record in records] == [expected]
    assert engine.errors == []


def test_scanner_cancel_stops_before_work(tmp_path):
    (tmp_path / "file.txt").write_text("x", encoding="utf-8")
    pause = threading.Event()
    pause.set()
    cancel = threading.Event()
    cancel.set()
    engine = ScannerEngine(ScanOptions((tmp_path,)), pause, cancel)
    assert list(engine.records()) == []

