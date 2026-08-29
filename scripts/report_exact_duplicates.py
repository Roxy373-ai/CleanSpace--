from __future__ import annotations

import argparse
import time
from pathlib import Path

from cleanspace.database import Database
from cleanspace.duplicates import find_exact_duplicates


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    args = parser.parse_args()
    database = Database(args.database)
    scan_id = database.latest_scan_id()
    if scan_id is None:
        raise SystemExit("no scan")
    candidates = database.duplicate_candidates(scan_id, minimum_size=1024 * 1024)
    known = database.known_full_hashes(scan_id)
    pending: list[tuple[Path, str]] = []
    last_percent = -1

    def progress(current: int, total: int) -> None:
        nonlocal last_percent
        percent = int(current * 100 / total) if total else 100
        if percent != last_percent and (percent % 5 == 0 or current == total):
            print(f"progress={current}/{total} ({percent}%)", flush=True)
            last_percent = percent

    started = time.perf_counter()
    groups = find_exact_duplicates(
        candidates, minimum_size=1024 * 1024, known_hashes=known,
        on_full_hash=lambda path, digest: pending.append((path, digest)), on_progress=progress,
    )
    if pending:
        database.update_full_hashes(scan_id, pending)
    elapsed = time.perf_counter() - started
    print(f"candidate_files={len(candidates)}")
    print(f"exact_groups={len(groups)}")
    print(f"exact_files={sum(len(group.files) for group in groups)}")
    print(f"reclaimable_bytes={sum(group.reclaimable_size for group in groups)}")
    print(f"elapsed_seconds={elapsed:.3f}")


if __name__ == "__main__":
    main()
