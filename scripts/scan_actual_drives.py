from __future__ import annotations

import argparse
import threading
import time
from pathlib import Path

from cleanspace.database import Database
from cleanspace.models import ScanOptions
from cleanspace.scanner import BATCH_SIZE, ScannerEngine


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    parser.add_argument("roots", nargs="+", type=Path)
    args = parser.parse_args()
    database = Database(args.database)
    roots = tuple(root for root in args.roots if root.exists())
    scan_id = database.start_scan(roots)
    pause = threading.Event()
    pause.set()
    engine = ScannerEngine(ScanOptions(roots), pause, threading.Event())
    batch = []
    count = total = 0
    started = time.perf_counter()
    try:
        for record in engine.records():
            batch.append(record)
            count += 1
            total += record.size
            if len(batch) >= BATCH_SIZE:
                database.add_files(scan_id, batch)
                batch.clear()
                if count % (BATCH_SIZE * 100) == 0:
                    print(f"progress_files={count}", flush=True)
        if batch:
            database.add_files(scan_id, batch)
        database.add_scan_errors(scan_id, engine.errors)
        database.finish_scan(scan_id, count=count, size=total, errors=len(engine.errors), cancelled=False)
    except Exception:
        database.finish_scan(scan_id, count=count, size=total, errors=len(engine.errors) + 1, cancelled=True)
        raise
    elapsed = time.perf_counter() - started
    print(f"scan_id={scan_id}")
    print(f"files={count}")
    print(f"bytes={total}")
    print(f"errors={len(engine.errors)}")
    print(f"elapsed_seconds={elapsed:.3f}")
    print(f"integrity={database.connection().execute('PRAGMA quick_check(1)').fetchone()[0]}")


if __name__ == "__main__":
    main()
