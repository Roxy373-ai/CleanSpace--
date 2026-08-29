from __future__ import annotations

import shutil
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

from PIL import Image, UnidentifiedImageError

from .models import FileRecord, MediaCheckResult, MediaState


IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff"}
VIDEO_EXTENSIONS = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv"}


def find_ffprobe() -> str | None:
    if getattr(sys, "frozen", False):
        bundled = Path(sys.executable).resolve().parent / "ffprobe.exe"
        if bundled.is_file():
            return str(bundled)
    return shutil.which("ffprobe")


def difference_hash(path: Path, hash_size: int = 8) -> str:
    with Image.open(path) as image:
        grayscale = image.convert("L").resize((hash_size + 1, hash_size), Image.Resampling.LANCZOS)
        pixels = list(grayscale.getdata())
    bits = []
    for row in range(hash_size):
        offset = row * (hash_size + 1)
        bits.extend(pixels[offset + column] > pixels[offset + column + 1] for column in range(hash_size))
    value = sum(int(bit) << index for index, bit in enumerate(bits))
    return f"{value:0{hash_size * hash_size // 4}x}"


def check_image(path: Path) -> MediaCheckResult:
    try:
        with Image.open(path) as image:
            image.verify()
        return MediaCheckResult(path, MediaState.VALID, perceptual_hash=difference_hash(path))
    except (UnidentifiedImageError, OSError, ValueError) as error:
        return MediaCheckResult(path, MediaState.BROKEN, str(error))


def check_video(path: Path, timeout: int = 15) -> MediaCheckResult:
    ffprobe = find_ffprobe()
    if not ffprobe:
        return MediaCheckResult(path, MediaState.SUSPECT, "ffprobe unavailable")
    try:
        result = subprocess.run(
            [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1", str(path)],
            capture_output=True, text=True, timeout=timeout, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if result.returncode == 0:
            return MediaCheckResult(path, MediaState.VALID)
        return MediaCheckResult(path, MediaState.BROKEN, result.stderr.strip()[:500])
    except (OSError, subprocess.TimeoutExpired) as error:
        return MediaCheckResult(path, MediaState.SUSPECT, str(error))


def check_media(record: FileRecord) -> MediaCheckResult:
    extension = record.extension.lower()
    if extension in IMAGE_EXTENSIONS:
        return check_image(record.path)
    if extension in VIDEO_EXTENSIONS:
        return check_video(record.path)
    return MediaCheckResult(record.path, MediaState.NOT_CHECKED, "unsupported")


def hamming_distance(left: str, right: str) -> int:
    return (int(left, 16) ^ int(right, 16)).bit_count()


def find_similar_images(results: list[MediaCheckResult], threshold: int = 6) -> list[list[Path]]:
    valid = [item for item in results if item.perceptual_hash]
    buckets: dict[tuple[int, int], list[int]] = defaultdict(list)
    values = [int(item.perceptual_hash or "0", 16) for item in valid]
    for index, value in enumerate(values):
        for band in range(4):
            buckets[(band, (value >> (band * 16)) & 0xFFFF)].append(index)
    parent = list(range(len(valid)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    def union(left: int, right: int) -> None:
        left_root, right_root = find(left), find(right)
        if left_root != right_root:
            parent[right_root] = left_root

    checked: set[tuple[int, int]] = set()
    for candidates in buckets.values():
        for offset, left in enumerate(candidates):
            for right in candidates[offset + 1:]:
                pair = (min(left, right), max(left, right))
                if pair in checked:
                    continue
                checked.add(pair)
                if hamming_distance(valid[left].perceptual_hash or "0", valid[right].perceptual_hash or "0") <= threshold:
                    union(left, right)
    groups: dict[int, list[Path]] = defaultdict(list)
    for index, item in enumerate(valid):
        groups[find(index)].append(item.path)
    return [paths for paths in groups.values() if len(paths) > 1]
