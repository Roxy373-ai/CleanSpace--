from __future__ import annotations

import os
import sys
import ctypes
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QStandardPaths
from PySide6.QtCore import Qt
from PySide6.QtGui import QIcon, QPixmap
from PySide6.QtWidgets import QApplication, QDialog, QHBoxLayout, QLabel, QPushButton, QVBoxLayout

from .database import CorruptDatabaseError, Database
from .i18n import TranslationManager, tr
from .models import LocaleCode
from .ui import CleanSpaceWindow, STYLE_SHEET, format_message


def data_directory() -> Path:
    override = os.environ.get("CLEANSPACE_DATA_DIR")
    if override:
        return Path(override)
    location = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppLocalDataLocation)
    return Path(location)


def open_database_with_recovery(path: Path) -> tuple[Database, Path | None]:
    try:
        return Database(path), None
    except CorruptDatabaseError:
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        backup_directory = path.parent / f"corrupt-backup-{timestamp}"
        backup_directory.mkdir(parents=True, exist_ok=False)
        for candidate in (path, Path(str(path) + "-wal"), Path(str(path) + "-shm")):
            if candidate.exists():
                candidate.replace(backup_directory / candidate.name)
        return Database(path), backup_directory


class LanguageSelectionDialog(QDialog):
    def __init__(self, current: LocaleCode) -> None:
        super().__init__()
        self.setObjectName("languageDialog")
        self.setAttribute(Qt.WidgetAttribute.WA_StyledBackground, True)
        self.selected: LocaleCode | None = None
        self.setWindowTitle("CleanSpace — 语言 / 언어")
        self.setFixedWidth(520)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(42, 34, 42, 38)
        layout.setSpacing(12)

        brand_row = QHBoxLayout()
        logo = QLabel()
        logo.setPixmap(QPixmap(str(Path(__file__).parent / "assets" / "cleanspace.png")).scaled(
            58, 58, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation
        ))
        brand_text = QVBoxLayout()
        name = QLabel("CleanSpace")
        name.setObjectName("languageDialogBrand")
        author = QLabel("허준영 制作 / 제작")
        author.setObjectName("languageDialogHint")
        brand_text.addWidget(name)
        brand_text.addWidget(author)
        brand_row.addWidget(logo)
        brand_row.addSpacing(10)
        brand_row.addLayout(brand_text)
        brand_row.addStretch()
        layout.addLayout(brand_row)
        layout.addSpacing(12)

        title = QLabel("请选择语言  /  언어를 선택하세요")
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        title.setObjectName("languageDialogTitle")
        hint = QLabel("选择后进入 CleanSpace  ·  선택 후 CleanSpace를 시작합니다")
        hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        hint.setObjectName("languageDialogHint")
        layout.addWidget(title)
        layout.addWidget(hint)
        for code, label in (
            (LocaleCode.ZH_CN, "简体中文\n进入中文版"),
            (LocaleCode.KO_KR, "한국어\n한국어로 시작"),
        ):
            button = QPushButton(label)
            button.setObjectName("startupLanguageButton")
            button.setDefault(code == current)
            button.clicked.connect(lambda _checked=False, chosen=code: self.choose(chosen))
            layout.addWidget(button)

    def choose(self, code: LocaleCode) -> None:
        self.selected = code
        self.accept()


def main() -> int:
    QApplication.setOrganizationName("허준영")
    QApplication.setOrganizationDomain("local.cleanspace")
    QApplication.setApplicationName("CleanSpace")
    QApplication.setApplicationVersion("0.1.0")
    if os.name == "nt":
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("HeoJunYoung.CleanSpace")
    app = QApplication(sys.argv)
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE_SHEET)
    app.setWindowIcon(QIcon(str(Path(__file__).parent / "assets" / "cleanspace.ico")))
    translations = TranslationManager(app)
    chooser = LanguageSelectionDialog(translations.current)
    if chooser.exec() != QDialog.DialogCode.Accepted or chooser.selected is None:
        return 0
    translations.install(chooser.selected)
    database, recovered_backup = open_database_with_recovery(data_directory() / "cleanspace.db")
    window = CleanSpaceWindow(database, translations)
    window.show()
    if recovered_backup is not None:
        from PySide6.QtWidgets import QMessageBox
        QMessageBox.warning(window, tr("app.title"), format_message("warning.index_rebuilt", path=recovered_backup))
    return app.exec()
