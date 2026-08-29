from __future__ import annotations

import hashlib
from collections import defaultdict
from pathlib import Path

from .models import DuplicateGroup, FileRecord


CHUNK_SIZE = 1024 * 1024


def quick_hash(path: Path, size: int) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        digest.update(handle.read(CHUNK_SIZE))
        if size > CHUNK_SIZE * 2:
            handle.seek(max(0, size - CHUNK_SIZE))
            digest.update(handle.read(CHUNK_SIZE))
    digest.update(str(size).encode("ascii"))
    return digest.hexdigest()


def full_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def find_exact_duplicates(
    records: list[FileRecord], minimum_size: int = 1,
    known_hashes: dict[Path, str] | None = None, on_full_hash=None, on_progress=None,
) -> list[DuplicateGroup]:
    known_hashes = known_hashes or {}
    by_size: dict[int, list[FileRecord]] = defaultdict(list)
    for record in records:
        if record.size < minimum_size:
            continue
        try:
            is_file = record.path.is_file()
        except OSError:
            continue
        if is_file:
            by_size[record.size].append(record)

    groups: list[DuplicateGroup] = []
    candidate_groups = [group for group in by_size.values() if len(group) > 1]
    for group_index, size_group in enumerate(candidate_groups, 1):
        if on_progress is not None:
            on_progress(group_index, len(candidate_groups))
        if len(size_group) < 2:
            continue
        physical_seen: set[tuple[int, int]] = set()
        unique_records: list[FileRecord] = []
        for record in size_group:
            identity = record.identity
            if identity != (0, 0) and identity in physical_seen:
                continue
            physical_seen.add(identity)
            unique_records.append(record)
        if len(unique_records) < 2:
            continue

        cached = [record for record in unique_records if record.path in known_hashes]
        uncached = [record for record in unique_records if record.path not in known_hashes]
        by_full: dict[str, list[FileRecord]] = defaultdict(list)
        for record in cached:
            by_full[known_hashes[record.path]].append(record)
        if cached:
            candidates = uncached
        else:
            by_quick: dict[str, list[FileRecord]] = defaultdict(list)
            for record in uncached:
                try:
                    by_quick[quick_hash(record.path, record.size)].append(record)
                except OSError:
                    continue
            candidates = [record for group in by_quick.values() if len(group) > 1 for record in group]
        for record in candidates:
            try:
                digest = full_hash(record.path)
            except OSError:
                continue
            by_full[digest].append(record)
            if on_full_hash is not None:
                on_full_hash(record.path, digest)
        for digest, exact_group in by_full.items():
            if len(exact_group) > 1:
                groups.append(DuplicateGroup(digest=digest, files=exact_group))
    return sorted(groups, key=lambda group: group.reclaimable_size, reverse=True)
