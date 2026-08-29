from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from pathlib import Path
from typing import Iterable


def signed_int64(value: int) -> int:
    normalized = int(value) & ((1 << 64) - 1)
    return normalized - (1 << 64) if normalized >= (1 << 63) else normalized


class LocaleCode(StrEnum):
    ZH_CN = "zh-CN"
    KO_KR = "ko-KR"


class RiskLevel(StrEnum):
    SAFE = "risk.safe"
    CAUTION = "risk.caution"
    BLOCKED = "risk.blocked"


class MediaState(StrEnum):
    NOT_CHECKED = "media.not_checked"
    VALID = "media.valid"
    SUSPECT = "media.suspect"
    BROKEN = "media.broken"


@dataclass(slots=True)
class ScanOptions:
    roots: tuple[Path, ...]
    min_size: int = 0
    include_hidden: bool = True
    media_checks: bool = False

    @classmethod
    def from_roots(cls, roots: Iterable[str | Path], **kwargs: object) -> "ScanOptions":
        return cls(tuple(Path(root) for root in roots), **kwargs)


@dataclass(slots=True)
class FileRecord:
    path: Path
    size: int
    modified_ns: int
    device: int = 0
    inode: int = 0
    extension: str = ""
    media_state: MediaState = MediaState.NOT_CHECKED
    error: str | None = None

    def __post_init__(self) -> None:
        self.device = signed_int64(self.device)
        self.inode = signed_int64(self.inode)

    @property
    def identity(self) -> tuple[int, int]:
        return self.device, self.inode


@dataclass(slots=True)
class MediaCheckResult:
    path: Path
    state: MediaState
    reason: str = ""
    perceptual_hash: str | None = None


@dataclass(slots=True)
class DuplicateGroup:
    digest: str
    files: list[FileRecord] = field(default_factory=list)

    @property
    def reclaimable_size(self) -> int:
        unique_physical = {
            ("inode", item.device, item.inode, item.size)
            if item.identity != (0, 0)
            else ("path", str(item.path), item.size)
            for item in self.files
        }
        return max(0, (len(unique_physical) - 1) * self.files[0].size) if self.files else 0


@dataclass(slots=True)
class InstalledApp:
    name: str
    publisher: str = ""
    version: str = ""
    install_date: str = ""
    install_location: str = ""
    estimated_size: int = 0
    uninstall_command: str = ""
    icon_path: str = ""
    registry_key: str = ""


@dataclass(slots=True)
class RiskDecision:
    level: RiskLevel
    reason_key: str
    direct_delete_allowed: bool


@dataclass(slots=True)
class CleanupPlanItem:
    path: Path
    expected_size: int
    expected_modified_ns: int
    expected_device: int
    expected_inode: int
    risk: RiskLevel
    reason_key: str
    source_rule: str
    direct_delete_allowed: bool
    is_directory: bool = False


@dataclass(frozen=True, slots=True)
class CleanupRule:
    rule_id: str
    title_key: str
    roots: tuple[Path, ...]
    risk: RiskLevel
    reason_key: str
