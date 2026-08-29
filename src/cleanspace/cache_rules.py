from __future__ import annotations

import threading
from pathlib import Path

from .models import CleanupPlanItem, FileRecord, ScanOptions
from .risk import classify_path, normalized_path, safe_cache_roots
from .scanner import ScannerEngine


def current_cache_records() -> list[FileRecord]:
    roots = []
    for value in safe_cache_roots():
        candidate = Path(value)
        try:
            exists = candidate.exists()
        except OSError:
            continue
        if exists and not any(candidate == root or root in candidate.parents for root in roots):
            roots.append(candidate)
    pause = threading.Event()
    pause.set()
    engine = ScannerEngine(ScanOptions(roots=tuple(roots)), pause, threading.Event())
    return list(engine.records())


def cache_candidates(records: list[FileRecord]) -> list[CleanupPlanItem]:
    roots = safe_cache_roots()
    candidates: list[CleanupPlanItem] = []
    for record in records:
        normalized = normalized_path(record.path)
        if not any(normalized == root or normalized.startswith(root + "\\") for root in roots):
            continue
        decision = classify_path(record.path)
        candidates.append(
            CleanupPlanItem(
                path=record.path,
                expected_size=record.size,
                expected_modified_ns=record.modified_ns,
                expected_device=record.device,
                expected_inode=record.inode,
                risk=decision.level,
                reason_key=decision.reason_key,
                source_rule="known-cache",
                direct_delete_allowed=decision.direct_delete_allowed,
            )
        )
    return candidates
