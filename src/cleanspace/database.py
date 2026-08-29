from __future__ import annotations

import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

from .models import FileRecord, MediaState, signed_int64


SCHEMA = """
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS scan_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    roots TEXT NOT NULL,
    file_count INTEGER NOT NULL DEFAULT 0,
    total_size INTEGER NOT NULL DEFAULT 0,
    error_count INTEGER NOT NULL DEFAULT 0,
    cancelled INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS files (
    scan_id INTEGER NOT NULL,
    path TEXT NOT NULL,
    size INTEGER NOT NULL,
    modified_ns INTEGER NOT NULL,
    device INTEGER NOT NULL,
    inode INTEGER NOT NULL,
    extension TEXT NOT NULL,
    media_state TEXT NOT NULL,
    error TEXT,
    quick_hash TEXT,
    full_hash TEXT,
    perceptual_hash TEXT,
    PRIMARY KEY (scan_id, path)
);
CREATE INDEX IF NOT EXISTS idx_files_scan_size ON files(scan_id, size DESC);
CREATE INDEX IF NOT EXISTS idx_files_scan_ext ON files(scan_id, extension);
CREATE TABLE IF NOT EXISTS hash_cache (
    path TEXT NOT NULL,
    size INTEGER NOT NULL,
    modified_ns INTEGER NOT NULL,
    quick_hash TEXT,
    full_hash TEXT,
    perceptual_hash TEXT,
    media_state TEXT,
    PRIMARY KEY(path, size, modified_ns)
);
CREATE TABLE IF NOT EXISTS scan_errors (
    scan_id INTEGER NOT NULL,
    path TEXT NOT NULL,
    message TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS cleanup_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    happened_at TEXT NOT NULL,
    original_path TEXT NOT NULL,
    estimated_size INTEGER NOT NULL,
    risk TEXT NOT NULL,
    result TEXT NOT NULL,
    error TEXT
);
"""


class CorruptDatabaseError(RuntimeError):
    pass


class Database:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._local = threading.local()
        existed = self.path.exists() and self.path.stat().st_size > 0
        connection = self.connection()
        if existed:
            try:
                connection.execute("SELECT name FROM sqlite_master ORDER BY name LIMIT 1").fetchone()
                connection.execute("SELECT id FROM scan_sessions ORDER BY id DESC LIMIT 1").fetchone()
                connection.execute("SELECT path FROM files ORDER BY scan_id DESC, size DESC LIMIT 1").fetchone()
                connection.execute("SELECT path FROM hash_cache LIMIT 1").fetchone()
            except sqlite3.DatabaseError as error:
                self.close()
                raise CorruptDatabaseError(str(error)) from error
        connection.executescript(SCHEMA)

    def connection(self) -> sqlite3.Connection:
        connection = getattr(self._local, "connection", None)
        if connection is None:
            connection = sqlite3.connect(self.path, timeout=30)
            connection.row_factory = sqlite3.Row
            self._local.connection = connection
        return connection

    def close(self) -> None:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            connection.close()
            self._local.connection = None

    def full_integrity_check(self) -> str:
        row = self.connection().execute("PRAGMA quick_check(1)").fetchone()
        return str(row[0] if row else "unknown")

    def start_scan(self, roots: Iterable[Path]) -> int:
        now = datetime.now(timezone.utc).isoformat()
        cursor = self.connection().execute(
            "INSERT INTO scan_sessions(started_at, roots) VALUES (?, ?)",
            (now, "\n".join(str(root) for root in roots)),
        )
        self.connection().commit()
        return int(cursor.lastrowid)

    def add_files(self, scan_id: int, records: Iterable[FileRecord]) -> None:
        rows = [
            (
                scan_id, str(item.path), item.size, item.modified_ns, signed_int64(item.device),
                signed_int64(item.inode), item.extension, item.media_state.value, item.error,
            )
            for item in records
        ]
        if not rows:
            return
        self.connection().executemany(
            """INSERT OR REPLACE INTO files(
                scan_id, path, size, modified_ns, device, inode, extension, media_state, error
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            rows,
        )
        paths = [row[1] for row in rows]
        placeholders = ",".join("?" for _ in paths)
        self.connection().execute(
            f"""UPDATE files SET
               quick_hash=(SELECT quick_hash FROM hash_cache h WHERE h.path=files.path AND h.size=files.size AND h.modified_ns=files.modified_ns),
               full_hash=(SELECT full_hash FROM hash_cache h WHERE h.path=files.path AND h.size=files.size AND h.modified_ns=files.modified_ns),
               perceptual_hash=(SELECT perceptual_hash FROM hash_cache h WHERE h.path=files.path AND h.size=files.size AND h.modified_ns=files.modified_ns),
               media_state=COALESCE((SELECT media_state FROM hash_cache h WHERE h.path=files.path AND h.size=files.size AND h.modified_ns=files.modified_ns), media_state)
               WHERE scan_id=? AND path IN ({placeholders})""",
            (scan_id, *paths),
        )
        self.connection().commit()

    def finish_scan(self, scan_id: int, *, count: int, size: int, errors: int, cancelled: bool) -> None:
        self.connection().execute(
            """UPDATE scan_sessions SET finished_at=?, file_count=?, total_size=?,
               error_count=?, cancelled=? WHERE id=?""",
            (datetime.now(timezone.utc).isoformat(), count, size, errors, int(cancelled), scan_id),
        )
        self.connection().commit()

    def add_scan_errors(self, scan_id: int, errors: Iterable[tuple[str, str]]) -> None:
        rows = [(scan_id, path, message) for path, message in errors]
        if rows:
            self.connection().executemany(
                "INSERT INTO scan_errors(scan_id, path, message) VALUES(?,?,?)", rows
            )
            self.connection().commit()

    def latest_scan_id(self) -> int | None:
        row = self.connection().execute("SELECT id FROM scan_sessions ORDER BY id DESC LIMIT 1").fetchone()
        return int(row[0]) if row else None

    def scan_summary(self, scan_id: int) -> tuple[int, int]:
        row = self.connection().execute(
            "SELECT file_count, total_size FROM scan_sessions WHERE id=?", (scan_id,)
        ).fetchone()
        return (int(row["file_count"]), int(row["total_size"])) if row else (0, 0)

    def list_files(self, scan_id: int, *, limit: int | None = None, media_only: bool = False) -> list[FileRecord]:
        params: list[object] = [scan_id]
        where = "scan_id=?"
        if media_only:
            placeholders = ",".join("?" for _ in MEDIA_EXTENSIONS)
            where += f" AND extension IN ({placeholders})"
            params.extend(sorted(MEDIA_EXTENSIONS))
        query = f"SELECT * FROM files WHERE {where} ORDER BY size DESC"
        if limit is not None:
            query += " LIMIT ?"
            params.append(limit)
        rows = self.connection().execute(query, params).fetchall()
        return [
            FileRecord(
                path=Path(row["path"]), size=row["size"], modified_ns=row["modified_ns"],
                device=row["device"], inode=row["inode"], extension=row["extension"],
                media_state=MediaState(row["media_state"]), error=row["error"],
            )
            for row in rows
        ]

    def duplicate_candidates(self, scan_id: int, minimum_size: int = 1024) -> list[FileRecord]:
        rows = self.connection().execute(
            """SELECT f.* FROM files f
               JOIN (
                   SELECT size FROM files
                   WHERE scan_id=? AND size>=?
                   GROUP BY size HAVING COUNT(*) > 1
               ) candidates ON candidates.size=f.size
               WHERE f.scan_id=? ORDER BY f.size DESC""",
            (scan_id, minimum_size, scan_id),
        ).fetchall()
        return [
            FileRecord(
                path=Path(row["path"]), size=row["size"], modified_ns=row["modified_ns"],
                device=row["device"], inode=row["inode"], extension=row["extension"],
                media_state=MediaState(row["media_state"]), error=row["error"],
            )
            for row in rows
        ]

    def files_under_roots(self, scan_id: int, roots: Iterable[str | Path]) -> list[FileRecord]:
        normalized = [str(Path(root)) .rstrip("\\/") for root in roots]
        if not normalized:
            return []
        clauses = " OR ".join("(LOWER(path)=LOWER(?) OR LOWER(path) LIKE LOWER(?))" for _ in normalized)
        params: list[object] = [scan_id]
        for root in normalized:
            params.extend((root, root + "\\%"))
        rows = self.connection().execute(
            f"SELECT * FROM files WHERE scan_id=? AND ({clauses}) ORDER BY size DESC", params
        ).fetchall()
        return [
            FileRecord(
                path=Path(row["path"]), size=row["size"], modified_ns=row["modified_ns"],
                device=row["device"], inode=row["inode"], extension=row["extension"],
                media_state=MediaState(row["media_state"]), error=row["error"],
            )
            for row in rows
        ]

    def update_hashes(self, scan_id: int, path: Path, *, quick_hash: str | None = None,
                      full_hash: str | None = None, perceptual_hash: str | None = None,
                      media_state: MediaState | None = None) -> None:
        values = {"quick_hash": quick_hash, "full_hash": full_hash, "perceptual_hash": perceptual_hash}
        if media_state is not None:
            values["media_state"] = media_state.value
        active = {key: value for key, value in values.items() if value is not None}
        if not active:
            return
        assignments = ", ".join(f"{key}=?" for key in active)
        self.connection().execute(
            f"UPDATE files SET {assignments} WHERE scan_id=? AND path=?",
            (*active.values(), scan_id, str(path)),
        )
        row = self.connection().execute(
            "SELECT size, modified_ns, quick_hash, full_hash, perceptual_hash, media_state FROM files WHERE scan_id=? AND path=?",
            (scan_id, str(path)),
        ).fetchone()
        if row:
            self.connection().execute(
                """INSERT INTO hash_cache(path,size,modified_ns,quick_hash,full_hash,perceptual_hash,media_state)
                   VALUES(?,?,?,?,?,?,?) ON CONFLICT(path,size,modified_ns) DO UPDATE SET
                   quick_hash=excluded.quick_hash, full_hash=excluded.full_hash,
                   perceptual_hash=excluded.perceptual_hash, media_state=excluded.media_state""",
                (str(path), row["size"], row["modified_ns"], row["quick_hash"], row["full_hash"], row["perceptual_hash"], row["media_state"]),
            )
        self.connection().commit()

    def known_full_hashes(self, scan_id: int) -> dict[Path, str]:
        rows = self.connection().execute(
            "SELECT path, full_hash FROM files WHERE scan_id=? AND full_hash IS NOT NULL", (scan_id,)
        ).fetchall()
        return {Path(row["path"]): row["full_hash"] for row in rows}

    def update_full_hashes(self, scan_id: int, hashes: list[tuple[Path, str]]) -> None:
        connection = self.connection()
        for path, digest in hashes:
            connection.execute(
                "UPDATE files SET full_hash=? WHERE scan_id=? AND path=?",
                (digest, scan_id, str(path)),
            )
            row = connection.execute(
                "SELECT size, modified_ns, quick_hash, perceptual_hash, media_state FROM files WHERE scan_id=? AND path=?",
                (scan_id, str(path)),
            ).fetchone()
            if row:
                connection.execute(
                    """INSERT INTO hash_cache(path,size,modified_ns,quick_hash,full_hash,perceptual_hash,media_state)
                       VALUES(?,?,?,?,?,?,?) ON CONFLICT(path,size,modified_ns) DO UPDATE SET
                       full_hash=excluded.full_hash""",
                    (str(path), row["size"], row["modified_ns"], row["quick_hash"], digest,
                     row["perceptual_hash"], row["media_state"]),
                )
        connection.commit()

    def add_history(self, path: Path, size: int, risk: str, result: str, error: str = "") -> None:
        self.connection().execute(
            "INSERT INTO cleanup_history(happened_at, original_path, estimated_size, risk, result, error) VALUES(?,?,?,?,?,?)",
            (datetime.now(timezone.utc).isoformat(), str(path), size, risk, result, error),
        )
        self.connection().commit()

    def history(self, limit: int = 200) -> list[sqlite3.Row]:
        return self.connection().execute(
            "SELECT * FROM cleanup_history ORDER BY id DESC LIMIT ?", (limit,)
        ).fetchall()


MEDIA_EXTENSIONS = {
    ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic",
    ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv",
}
