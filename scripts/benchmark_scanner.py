from __future__ import annotations

import tempfile
import threading
import time
from pathlib import Path

from cleanspace.models import ScanOptions
from cleanspace.scanner import ScannerEngine


def main(file_count: int = 10_000) -> None:
    with tempfile.TemporaryDirectory(prefix="cleanspace-benchmark-") as directory:
        root = Path(directory)
        for index in range(file_count):
            folder = root / f"folder-{index % 100:03d}"
            folder.mkdir(exist_ok=True)
            (folder / f"file-{index:05d}.dat").write_bytes(b"x" * 128)
        pause = threading.Event()
        pause.set()
        started = time.perf_counter()
        engine = ScannerEngine(ScanOptions((root,)), pause, threading.Event())
        records = list(engine.records())
        elapsed = time.perf_counter() - started
        rate = len(records) / elapsed if elapsed else 0
        print(f"files={len(records)} elapsed={elapsed:.3f}s rate={rate:,.0f} files/s errors={len(engine.errors)}")
        if len(records) != file_count or engine.errors:
            raise SystemExit(1)


if __name__ == "__main__":
    main()

