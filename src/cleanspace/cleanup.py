from __future__ import annotations

import os
from dataclasses import dataclass

from send2trash import send2trash

from .database import Database
from .models import CleanupPlanItem, RiskLevel, signed_int64
from .risk import classify_path


@dataclass(slots=True)
class CleanupResult:
    item: CleanupPlanItem
    success: bool
    error_key: str = ""
    detail: str = ""


class CleanupService:
    def __init__(self, database: Database, recycler=send2trash) -> None:
        self.database = database
        self.recycler = recycler

    def revalidate(self, item: CleanupPlanItem) -> tuple[bool, str]:
        decision = classify_path(item.path, is_directory=item.is_directory)
        if decision.level is RiskLevel.BLOCKED or not item.direct_delete_allowed:
            return False, "error.blocked"
        try:
            stat = item.path.stat(follow_symlinks=False)
        except OSError:
            return False, "error.changed"
        if item.is_directory:
            if not item.path.is_dir():
                return False, "error.changed"
        elif not item.path.is_file() or stat.st_size != item.expected_size:
            return False, "error.changed"
        if stat.st_mtime_ns != item.expected_modified_ns:
            return False, "error.changed"
        if item.expected_inode and signed_int64(stat.st_ino) != signed_int64(item.expected_inode):
            return False, "error.changed"
        if item.expected_device and signed_int64(stat.st_dev) != signed_int64(item.expected_device):
            return False, "error.changed"
        return True, ""

    def execute(self, items: list[CleanupPlanItem]) -> list[CleanupResult]:
        results: list[CleanupResult] = []
        for item in items:
            valid, error_key = self.revalidate(item)
            if not valid:
                result = CleanupResult(item, False, error_key)
            else:
                try:
                    self.recycler(os.fspath(item.path))
                    result = CleanupResult(item, True)
                except Exception as error:
                    result = CleanupResult(item, False, "error.recycle", str(error))
            self.database.add_history(
                item.path, item.expected_size, item.risk.value,
                "success" if result.success else "failed", result.detail or result.error_key,
            )
            results.append(result)
        return results
