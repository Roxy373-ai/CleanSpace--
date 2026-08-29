from __future__ import annotations

import os
import threading
from pathlib import Path

from PySide6.QtCore import QObject, Signal

from .database import Database
from .models import FileRecord, ScanOptions


FILE_ATTRIBUTE_REPARSE_POINT = 0x0400
BATCH_SIZE = 800


class ScannerEngine:
    def __init__(self, options: ScanOptions, pause_event: threading.Event, cancel_event: threading.Event) -> None:
        self.options = options
        self.pause_event = pause_event
        self.cancel_event = cancel_event
        self.errors: list[tuple[str, str]] = []

    def records(self):
        stack = [root for root in reversed(self.options.roots) if root.exists()]
        while stack and not self.cancel_event.is_set():
            self.pause_event.wait()
            current = stack.pop()
            try:
                with os.scandir(current) as iterator:
                    for entry in iterator:
                        if self.cancel_event.is_set():
                            return
                        self.pause_event.wait()
                        try:
                            stat = entry.stat(follow_symlinks=False)
                            attributes = getattr(stat, "st_file_attributes", 0)
                            if attributes & FILE_ATTRIBUTE_REPARSE_POINT or entry.is_symlink():
                                continue
                            if entry.is_dir(follow_symlinks=False):
                                stack.append(Path(entry.path))
                                continue
                            if not entry.is_file(follow_symlinks=False) or stat.st_size < self.options.min_size:
                                continue
                            yield FileRecord(
                                path=Path(entry.path), size=stat.st_size,
                                modified_ns=stat.st_mtime_ns, device=stat.st_dev,
                                inode=stat.st_ino, extension=Path(entry.name).suffix.lower(),
                            )
                        except (OSError, PermissionError) as error:
                            self.errors.append((entry.path, str(error)))
            except (OSError, PermissionError) as error:
                self.errors.append((str(current), str(error)))


class ScanController(QObject):
    batch_ready = Signal(list)
    progress = Signal(int, int, str, int)
    finished = Signal(int, int, int, bool, int)
    failed = Signal(str)

    def __init__(self, database: Database) -> None:
        super().__init__()
        self.database = database
        self._pause = threading.Event()
        self._pause.set()
        self._cancel = threading.Event()
        self._thread: threading.Thread | None = None

    @property
    def running(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

    def start(self, options: ScanOptions) -> None:
        if self.running:
            return
        self._pause.set()
        self._cancel.clear()
        self._thread = threading.Thread(target=self._run, args=(options,), daemon=True)
        self._thread.start()

    def pause(self) -> None:
        self._pause.clear()

    def resume(self) -> None:
        self._pause.set()

    def cancel(self) -> None:
        self._cancel.set()
        self._pause.set()

    def _run(self, options: ScanOptions) -> None:
        scan_id = self.database.start_scan(options.roots)
        engine = ScannerEngine(options, self._pause, self._cancel)
        batch: list[FileRecord] = []
        count = total = 0
        last_path = ""
        try:
            for record in engine.records():
                batch.append(record)
                count += 1
                total += record.size
                last_path = str(record.path)
                if len(batch) >= BATCH_SIZE:
                    self.database.add_files(scan_id, batch)
                    self.batch_ready.emit(batch.copy())
                    batch.clear()
                    self.progress.emit(count, total, last_path, len(engine.errors))
            if batch:
                self.database.add_files(scan_id, batch)
                self.batch_ready.emit(batch.copy())
            cancelled = self._cancel.is_set()
            self.database.add_scan_errors(scan_id, engine.errors)
            self.database.finish_scan(scan_id, count=count, size=total, errors=len(engine.errors), cancelled=cancelled)
            self.progress.emit(count, total, last_path, len(engine.errors))
            self.finished.emit(count, total, len(engine.errors), cancelled, scan_id)
        except Exception as error:
            self.database.finish_scan(scan_id, count=count, size=total, errors=len(engine.errors) + 1, cancelled=True)
            self.failed.emit(str(error))
