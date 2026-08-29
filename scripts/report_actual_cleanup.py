from __future__ import annotations

import argparse
import sqlite3
import threading
from pathlib import Path

from cleanspace.models import ScanOptions
from cleanspace.risk import safe_cache_roots
from cleanspace.scanner import ScannerEngine


def latest_scan_report(database_path: Path) -> tuple[int, int, int, int, int]:
    connection = sqlite3.connect(database_path.as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    row = connection.execute(
        "SELECT id, file_count, total_size, error_count FROM scan_sessions ORDER BY id DESC LIMIT 1"
    ).fetchone()
    if row is None:
        return 0, 0, 0, 0, 0
    duplicate = connection.execute(
        """SELECT COALESCE(SUM((copies - 1) * size), 0) AS possible_size
           FROM (
               SELECT size, COUNT(*) AS copies FROM files
               WHERE scan_id=? AND size>=1048576
               GROUP BY size HAVING COUNT(*) > 1
           )""",
        (row["id"],),
    ).fetchone()
    return int(row["id"]), int(row["file_count"]), int(row["total_size"]), int(row["error_count"]), int(duplicate[0])


def scan_safe_caches() -> tuple[int, int, int]:
    roots = []
    inaccessible_roots = 0
    for value in safe_cache_roots():
        candidate = Path(value)
        try:
            exists = candidate.exists()
        except OSError:
            inaccessible_roots += 1
            continue
        if exists and not any(candidate == root or root in candidate.parents for root in roots):
            roots.append(candidate)
    pause = threading.Event()
    pause.set()
    engine = ScannerEngine(ScanOptions(roots=tuple(roots)), pause, threading.Event())
    count = total = 0
    for record in engine.records():
        count += 1
        total += record.size
    return count, total, inaccessible_roots + len(engine.errors)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    args = parser.parse_args()
    scan_id, files, total, errors, possible_duplicates = latest_scan_report(args.database)
    safe_files, safe_size, safe_errors = scan_safe_caches()
    print(f"scan_id={scan_id}")
    print(f"indexed_files={files}")
    print(f"indexed_bytes={total}")
    print(f"indexed_errors={errors}")
    print(f"safe_cache_files_now={safe_files}")
    print(f"safe_cache_bytes_now={safe_size}")
    print(f"safe_cache_errors_now={safe_errors}")
    print(f"duplicate_size_upper_bound={possible_duplicates}")


if __name__ == "__main__":
    main()
