from pathlib import Path

from cleanspace.cleanup import CleanupService
from cleanspace.database import Database
from cleanspace.models import CleanupPlanItem, FileRecord
from cleanspace.risk import classify_path


def plan_item(path: Path) -> CleanupPlanItem:
    stat = path.stat()
    decision = classify_path(path)
    return CleanupPlanItem(
        path=path, expected_size=stat.st_size, expected_modified_ns=stat.st_mtime_ns,
        expected_device=stat.st_dev, expected_inode=stat.st_ino, risk=decision.level,
        reason_key=decision.reason_key, source_rule="test",
        direct_delete_allowed=decision.direct_delete_allowed,
    )


def test_database_roundtrip(tmp_path):
    database = Database(tmp_path / "db.sqlite")
    scan_id = database.start_scan([tmp_path])
    path = tmp_path / "文件.txt"
    path.write_text("hello", encoding="utf-8")
    stat = path.stat()
    database.add_files(scan_id, [FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, ".txt")])
    database.finish_scan(scan_id, count=1, size=stat.st_size, errors=0, cancelled=False)
    restored = database.list_files(scan_id)
    assert len(restored) == 1
    assert restored[0].path == path
    database.update_hashes(scan_id, path, full_hash="abc123")
    assert database.known_full_hashes(scan_id) == {path: "abc123"}
    assert database.scan_summary(scan_id) == (1, stat.st_size)


def test_database_returns_only_duplicate_size_candidates(tmp_path):
    database = Database(tmp_path / "duplicates.sqlite")
    scan_id = database.start_scan([tmp_path])
    records = []
    for name, content in (("a.bin", b"same"), ("b.bin", b"diff"), ("unique.bin", b"unique")):
        path = tmp_path / name
        path.write_bytes(content)
        stat = path.stat()
        records.append(FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, ".bin"))
    database.add_files(scan_id, records)
    candidates = database.duplicate_candidates(scan_id, minimum_size=1)
    assert {record.path.name for record in candidates} == {"a.bin", "b.bin"}


def test_database_lists_every_file_under_selected_roots(tmp_path):
    database = Database(tmp_path / "roots.sqlite")
    scan_id = database.start_scan([tmp_path])
    cache = tmp_path / "cache"
    cache.mkdir()
    inside = cache / "small.tmp"
    outside = tmp_path / "outside.tmp"
    inside.write_bytes(b"a")
    outside.write_bytes(b"b")
    records = []
    for path in (inside, outside):
        stat = path.stat()
        records.append(FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, path.suffix))
    database.add_files(scan_id, records)
    assert [record.path for record in database.files_under_roots(scan_id, [cache])] == [inside]


def test_cleanup_revalidates_and_uses_recycler(tmp_path, monkeypatch):
    monkeypatch.setenv("TEMP", str(tmp_path))
    from cleanspace import risk
    risk.safe_cache_roots.cache_clear() if hasattr(risk.safe_cache_roots, "cache_clear") else None
    path = tmp_path / "safe.tmp"
    path.write_bytes(b"disposable")
    item = plan_item(path)
    recycled = []
    service = CleanupService(Database(tmp_path / "history.sqlite"), recycler=recycled.append)
    result = service.execute([item])[0]
    assert result.success
    assert recycled == [str(path)]


def test_changed_file_is_skipped(tmp_path, monkeypatch):
    monkeypatch.setenv("TEMP", str(tmp_path))
    path = tmp_path / "changed.tmp"
    path.write_bytes(b"before")
    item = plan_item(path)
    path.write_bytes(b"after and different")
    recycled = []
    result = CleanupService(Database(tmp_path / "history.sqlite"), recycler=recycled.append).execute([item])[0]
    assert not result.success
    assert result.error_key == "error.changed"
    assert recycled == []
