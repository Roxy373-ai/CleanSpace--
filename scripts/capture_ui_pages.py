from __future__ import annotations

import argparse
import os
import time
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings
from PySide6.QtWidgets import QApplication

from cleanspace.database import Database
from cleanspace.i18n import TranslationManager
from cleanspace.models import LocaleCode
from cleanspace.ui import CleanSpaceWindow, STYLE_SHEET


def wait_tasks(app: QApplication, window: CleanSpaceWindow, timeout: float = 20) -> None:
    deadline = time.monotonic() + timeout
    while any(getattr(task, "_thread", None) and task._thread.is_alive() for task in window._tasks):
        app.processEvents()
        if time.monotonic() >= deadline:
            break
        time.sleep(0.02)
    app.processEvents()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    app = QApplication([])
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE_SHEET)
    manager = TranslationManager(app)
    manager.settings = QSettings(str(args.output / "qa-settings.ini"), QSettings.Format.IniFormat)
    manager.install(LocaleCode.ZH_CN)
    started = time.perf_counter()
    window = CleanSpaceWindow(Database(args.database), manager)
    print(f"window_load_seconds={time.perf_counter() - started:.3f}")
    window.resize(1600, 950)
    window.show()
    app.processEvents()
    for index, name in ((0, "dashboard"), (1, "space"), (2, "media"), (5, "cleanup")):
        window.nav.setCurrentRow(index)
        app.processEvents()
        if index == 2:
            window._load_visible_media_thumbnails()
        window.grab().save(str(args.output / f"{name}.png"))
    window.nav.setCurrentRow(3)
    window._find_duplicates()
    wait_tasks(app, window, 30)
    window.grab().save(str(args.output / "duplicates.png"))
    window.nav.setCurrentRow(4)
    window._load_apps()
    wait_tasks(app, window, 10)
    window.grab().save(str(args.output / "apps.png"))
    window.close()


if __name__ == "__main__":
    main()
