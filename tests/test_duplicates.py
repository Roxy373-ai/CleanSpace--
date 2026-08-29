from pathlib import Path

from cleanspace.duplicates import find_exact_duplicates
from cleanspace.models import FileRecord


def record(path: Path) -> FileRecord:
    stat = path.stat()
    return FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, path.suffix)


def test_exact_duplicates_are_grouped(tmp_path):
    first = tmp_path / "中文.bin"
    second = tmp_path / "한국어.bin"
    third = tmp_path / "different.bin"
    first.write_bytes(b"same content" * 1000)
    second.write_bytes(first.read_bytes())
    third.write_bytes(b"other content" * 1000)
    groups = find_exact_duplicates([record(first), record(second), record(third)])
    assert len(groups) == 1
    assert {item.path for item in groups[0].files} == {first, second}
    assert groups[0].reclaimable_size == first.stat().st_size


def test_hardlinks_are_not_reported_as_reclaimable_duplicates(tmp_path):
    first = tmp_path / "first.bin"
    linked = tmp_path / "linked.bin"
    first.write_bytes(b"physical file")
    try:
        linked.hardlink_to(first)
    except OSError:
        return
    assert find_exact_duplicates([record(first), record(linked)]) == []


def test_cached_hash_avoids_rehashing_known_file(tmp_path):
    first = tmp_path / "first.bin"
    second = tmp_path / "second.bin"
    first.write_bytes(b"cached duplicate" * 100)
    second.write_bytes(first.read_bytes())
    first_record, second_record = record(first), record(second)
    from cleanspace.duplicates import full_hash
    digest = full_hash(first)
    calculated = []
    groups = find_exact_duplicates(
        [first_record, second_record], known_hashes={first: digest},
        on_full_hash=lambda path, value: calculated.append((path, value)),
    )
    assert len(groups) == 1
    assert calculated == [(second, digest)]


def test_zero_file_identifiers_are_not_treated_as_hardlinks(tmp_path):
    first = tmp_path / "zero-first.bin"
    second = tmp_path / "zero-second.bin"
    first.write_bytes(b"zero identity duplicate")
    second.write_bytes(first.read_bytes())
    first_record = record(first)
    second_record = record(second)
    first_record.device = first_record.inode = 0
    second_record.device = second_record.inode = 0
    groups = find_exact_duplicates([first_record, second_record])
    assert len(groups) == 1
    assert groups[0].reclaimable_size == first.stat().st_size
