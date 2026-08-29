from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import threading
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QFileInfo, QObject, QRect, QSize, Qt, QTimer, Signal, QUrl
from PySide6.QtGui import QColor, QDesktopServices, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QFrame, QGridLayout,
    QFileIconProvider, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget, QListWidgetItem,
    QMainWindow, QMessageBox, QProgressBar, QPushButton, QScrollArea, QSizePolicy,
    QSplitter, QStackedWidget, QTableWidget, QTableWidgetItem, QToolTip, QTreeWidget,
    QTreeWidgetItem, QVBoxLayout, QWidget,
)

from .apps import extract_executable, extract_icon_path, installed_apps, launch_uninstaller, uninstall_command_is_valid
from .cache_rules import cache_candidates, current_cache_records
from .cleanup import CleanupService
from .database import Database, MEDIA_EXTENSIONS
from .duplicates import find_exact_duplicates
from .i18n import TranslationManager, tr
from .media import IMAGE_EXTENSIONS, check_media, find_similar_images
from .models import CleanupPlanItem, FileRecord, LocaleCode, MediaCheckResult, RiskLevel, ScanOptions
from .risk import classify_path
from .scanner import ScanController


def human_size(value: int) -> str:
    number = float(max(0, value))
    for unit in ("B", "KB", "MB", "GB", "TB", "PB"):
        if number < 1024 or unit == "PB":
            return f"{number:.0f} {unit}" if unit == "B" else f"{number:.1f} {unit}"
        number /= 1024
    return f"{number:.1f} PB"


def format_time(ns: int) -> str:
    try:
        return datetime.fromtimestamp(ns / 1_000_000_000).strftime("%Y-%m-%d %H:%M")
    except (OSError, ValueError, OverflowError):
        return ""


def format_message(key: str, **values: object) -> str:
    return tr(key).format(**values)


class TaskSignals(QObject):
    finished = Signal(object)
    failed = Signal(str)
    progress = Signal(int, int)


def run_background(function, callback, error_callback) -> TaskSignals:
    signals = TaskSignals()
    signals.finished.connect(callback)
    signals.failed.connect(error_callback)

    def runner() -> None:
        try:
            signals.finished.emit(function())
        except Exception as error:
            signals.failed.emit(str(error))

    thread = threading.Thread(target=runner, daemon=True)
    thread.start()
    signals._thread = thread  # type: ignore[attr-defined]
    return signals


def run_background_with_progress(function, callback, error_callback, progress_callback) -> TaskSignals:
    signals = TaskSignals()
    signals.finished.connect(callback)
    signals.failed.connect(error_callback)
    signals.progress.connect(progress_callback)

    def runner() -> None:
        try:
            signals.finished.emit(function(signals.progress.emit))
        except Exception as error:
            signals.failed.emit(str(error))

    thread = threading.Thread(target=runner, daemon=True)
    thread.start()
    signals._thread = thread  # type: ignore[attr-defined]
    return signals


class DriveCard(QFrame):
    def __init__(self, root: Path) -> None:
        super().__init__()
        self.root = root
        self.setObjectName("driveCard")
        layout = QVBoxLayout(self)
        self.title = QLabel()
        self.title.setObjectName("driveTitle")
        self.detail = QLabel()
        self.bar = QProgressBar()
        self.bar.setTextVisible(False)
        self.free_label = QLabel()
        layout.addWidget(self.title)
        layout.addWidget(self.detail)
        layout.addWidget(self.bar)
        layout.addWidget(self.free_label)
        self.refresh()

    def refresh(self) -> None:
        try:
            usage = shutil.disk_usage(self.root)
            percent = round(usage.used * 100 / usage.total) if usage.total else 0
            self.title.setText(f"{self.root.drive or self.root} · NTFS")
            self.detail.setText(f"{tr('dashboard.used')} {human_size(usage.used)} / {human_size(usage.total)}")
            self.free_label.setText(f"{tr('dashboard.free')} {human_size(usage.free)}")
            self.bar.setValue(percent)
        except OSError:
            self.title.setText(str(self.root))
            self.detail.setText(tr("error.unavailable"))
            self.bar.setValue(0)


class TreemapWidget(QWidget):
    recordActivated = Signal(object)
    recordDoubleClicked = Signal(object)

    def __init__(self) -> None:
        super().__init__()
        self.records: list[FileRecord] = []
        self.blocks: list[tuple[QRect, FileRecord]] = []
        self.setMinimumHeight(210)
        self.setMouseTracking(True)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)

    def set_records(self, records: list[FileRecord]) -> None:
        self.records = records[:18]
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect().adjusted(4, 4, -4, -4)
        self.blocks.clear()
        painter.fillRect(rect, QColor("#ffffff"))
        if not self.records:
            painter.setPen(QColor("#657286"))
            painter.drawText(rect, Qt.AlignmentFlag.AlignCenter, tr("status.no_results"))
            return
        total = sum(item.size for item in self.records) or 1
        x = rect.x()
        colors = ("#2878df", "#6757d9", "#2e9b70", "#ca7b27", "#bd4b60")
        for index, record in enumerate(self.records):
            width = max(3, round(rect.width() * record.size / total))
            if index == len(self.records) - 1:
                width = rect.right() - x + 1
            block = QRect(x, rect.y(), width, rect.height())
            decision = classify_path(record.path)
            color = {
                RiskLevel.SAFE: "#27966f", RiskLevel.CAUTION: colors[index % len(colors)],
                RiskLevel.BLOCKED: "#9aa6b2",
            }[decision.level]
            painter.fillRect(block, QColor(color))
            self.blocks.append((block, record))
            if width > 82:
                painter.setPen(QColor("white"))
                text = f"{record.path.name}\n{human_size(record.size)}"
                painter.drawText(block.adjusted(7, 7, -7, -7), Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignTop, text)
            x += width
            if x > rect.right():
                break

    def _record_at(self, position) -> FileRecord | None:
        return next((record for block, record in self.blocks if block.contains(position)), None)

    def mousePressEvent(self, event) -> None:
        record = self._record_at(event.position().toPoint())
        if record is not None:
            self.recordActivated.emit(record)
        super().mousePressEvent(event)

    def mouseDoubleClickEvent(self, event) -> None:
        record = self._record_at(event.position().toPoint())
        if record is not None:
            self.recordDoubleClicked.emit(record)
        super().mouseDoubleClickEvent(event)

    def mouseMoveEvent(self, event) -> None:
        record = self._record_at(event.position().toPoint())
        if record is not None:
            decision = classify_path(record.path)
            QToolTip.showText(event.globalPosition().toPoint(), f"{record.path}\n{human_size(record.size)}\n{tr(decision.level.value)}", self)
        else:
            QToolTip.hideText()
        super().mouseMoveEvent(event)


def setup_table(columns: int) -> QTableWidget:
    table = QTableWidget(0, columns)
    table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
    table.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
    table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
    table.setAlternatingRowColors(True)
    table.verticalHeader().setVisible(False)
    table.horizontalHeader().setStretchLastSection(True)
    return table


class CleanSpaceWindow(QMainWindow):
    NAV_KEYS = (
        "nav.dashboard", "nav.space", "nav.media", "nav.duplicates", "nav.apps",
        "nav.cleanup", "nav.history", "nav.settings", "nav.about",
    )

    def __init__(self, database: Database, translations: TranslationManager) -> None:
        super().__init__()
        self.database = database
        self.translations = translations
        self.scanner = ScanController(database)
        self.cleanup_service = CleanupService(database)
        self.records: list[FileRecord] = []
        self.scan_id: int | None = database.latest_scan_id()
        self.scan_count = 0
        self.scan_size = 0
        self.cleanup_items: list[CleanupPlanItem] = []
        self._exact_groups = []
        self._similar_groups: list[list[Path]] = []
        self._tasks: list[TaskSignals] = []
        self._file_icon_provider = QFileIconProvider()
        self._space_drive_filter = "ALL"
        self._treemap_record: FileRecord | None = None
        self._live_refresh_timer = QTimer(self)
        self._live_refresh_timer.setSingleShot(True)
        self._live_refresh_timer.setInterval(450)
        self._live_refresh_timer.timeout.connect(lambda: self._refresh_results(display_limit=500))
        self.setMinimumSize(1080, 700)
        self.resize(1320, 820)
        self._build_ui()
        self._connect_signals()
        self.retranslate_ui()
        if self.scan_id is not None:
            self.scan_count, self.scan_size = database.scan_summary(self.scan_id)
            self.records = database.list_files(self.scan_id, limit=100_000)
            self.cleanup_items = cache_candidates(current_cache_records())
            self._refresh_results()
            self._refresh_cleanup_table()

    def _build_ui(self) -> None:
        central = QWidget()
        root = QVBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        top = QFrame()
        top.setObjectName("topBar")
        top_layout = QHBoxLayout(top)
        brand = QLabel("CleanSpace")
        brand.setObjectName("brand")
        self.author_label = QLabel()
        top_layout.addWidget(brand)
        top_layout.addWidget(self.author_label)
        top_layout.addStretch()
        self.scan_button = QPushButton()
        self.scan_button.setObjectName("primaryButton")
        self.scan_c_button = QPushButton("C:")
        self.scan_d_button = QPushButton("D:")
        self.pause_button = QPushButton()
        self.cancel_button = QPushButton()
        self.pause_button.setEnabled(False)
        self.cancel_button.setEnabled(False)
        if Path("C:/").exists():
            top_layout.addWidget(self.scan_c_button)
        if Path("D:/").exists():
            top_layout.addWidget(self.scan_d_button)
        top_layout.addWidget(self.scan_button)
        top_layout.addWidget(self.pause_button)
        top_layout.addWidget(self.cancel_button)
        root.addWidget(top)

        splitter = QSplitter()
        self.nav = QListWidget()
        self.nav.setObjectName("navigation")
        self.nav.setFixedWidth(205)
        self.stack = QStackedWidget()
        splitter.addWidget(self.nav)
        splitter.addWidget(self.stack)
        splitter.setStretchFactor(1, 1)
        root.addWidget(splitter, 1)

        status = QFrame()
        status_layout = QHBoxLayout(status)
        status_layout.setContentsMargins(14, 5, 14, 5)
        self.status_label = QLabel()
        self.progress = QProgressBar()
        self.progress.setFixedWidth(220)
        self.progress.setRange(0, 1)
        self.progress.setValue(0)
        self.error_label = QLabel()
        status_layout.addWidget(self.status_label, 1)
        status_layout.addWidget(self.error_label)
        status_layout.addWidget(self.progress)
        root.addWidget(status)
        self.setCentralWidget(central)

        for _ in self.NAV_KEYS:
            self.nav.addItem(QListWidgetItem())
        self.dashboard_page = self._dashboard_page()
        self.space_page = self._space_page()
        self.media_page = self._media_page()
        self.duplicates_page = self._duplicates_page()
        self.apps_page = self._apps_page()
        self.cleanup_page = self._cleanup_page()
        self.history_page = self._history_page()
        self.settings_page = self._settings_page()
        self.about_page = self._about_page()
        for page in (
            self.dashboard_page, self.space_page, self.media_page, self.duplicates_page,
            self.apps_page, self.cleanup_page, self.history_page, self.settings_page, self.about_page,
        ):
            self.stack.addWidget(page)
        self.nav.setCurrentRow(0)

    def _page_shell(self) -> tuple[QWidget, QVBoxLayout, QLabel, QLabel]:
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(24, 22, 24, 22)
        title = QLabel()
        title.setObjectName("pageTitle")
        subtitle = QLabel()
        subtitle.setWordWrap(True)
        subtitle.setObjectName("subtitle")
        layout.addWidget(title)
        layout.addWidget(subtitle)
        return page, layout, title, subtitle

    def _dashboard_page(self) -> QWidget:
        page, layout, self.dashboard_title, self.dashboard_subtitle = self._page_shell()
        drive_layout = QGridLayout()
        self.drive_cards = [DriveCard(Path(root)) for root in ("C:/", "D:/") if Path(root).exists()]
        for index, card in enumerate(self.drive_cards):
            drive_layout.addWidget(card, 0, index)
        layout.addLayout(drive_layout)
        self.summary_label = QLabel()
        self.summary_label.setObjectName("summary")
        layout.addWidget(self.summary_label)
        self.recovery_summary_label = QLabel()
        self.recovery_summary_label.setObjectName("recoverySummary")
        self.recovery_summary_label.setWordWrap(True)
        layout.addWidget(self.recovery_summary_label)
        self.treemap = TreemapWidget()
        layout.addWidget(self.treemap, 1)
        treemap_actions = QHBoxLayout()
        self.treemap_detail = QLabel()
        self.treemap_detail.setObjectName("detailPanel")
        self.treemap_detail.setWordWrap(True)
        self.treemap_locate_button = QPushButton()
        self.treemap_add_button = QPushButton()
        self.treemap_locate_button.setEnabled(False)
        self.treemap_add_button.setEnabled(False)
        treemap_actions.addWidget(self.treemap_detail, 1)
        treemap_actions.addWidget(self.treemap_locate_button)
        treemap_actions.addWidget(self.treemap_add_button)
        layout.addLayout(treemap_actions)
        return page

    def _space_page(self) -> QWidget:
        page, layout, self.space_title, _ = self._page_shell()
        drives = QHBoxLayout()
        self.space_drive_buttons: dict[str, QPushButton] = {}
        for drive in ("ALL", "C", "D"):
            if drive != "ALL" and not Path(f"{drive}:/").exists():
                continue
            button = QPushButton("" if drive == "ALL" else f"{drive}:")
            button.setCheckable(True)
            button.setObjectName("filterButton")
            button.clicked.connect(lambda _checked=False, selected=drive: self._set_space_drive_filter(selected))
            drives.addWidget(button)
            self.space_drive_buttons[drive] = button
        drives.addStretch()
        layout.addLayout(drives)
        controls = QHBoxLayout()
        self.space_filter = QLineEdit()
        self.add_selected_button = QPushButton()
        self.space_locate_button = QPushButton()
        controls.addWidget(self.space_filter, 1)
        controls.addWidget(self.space_locate_button)
        controls.addWidget(self.add_selected_button)
        layout.addLayout(controls)
        self.space_table = setup_table(5)
        layout.addWidget(self.space_table, 1)
        return page

    def _media_page(self) -> QWidget:
        page, layout, self.media_title, _ = self._page_shell()
        controls = QHBoxLayout()
        self.check_media_button = QPushButton()
        self.media_open_button = QPushButton()
        self.media_add_button = QPushButton()
        self.media_locate_button = QPushButton()
        controls.addWidget(self.check_media_button)
        controls.addWidget(self.media_open_button)
        controls.addWidget(self.media_locate_button)
        controls.addWidget(self.media_add_button)
        controls.addStretch()
        layout.addLayout(controls)
        media_splitter = QSplitter()
        self.media_table = setup_table(4)
        self.media_table.setIconSize(QSize(64, 52))
        self.media_table.verticalHeader().setDefaultSectionSize(62)
        self.media_preview = QLabel()
        self.media_preview.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.media_preview.setMinimumWidth(260)
        self.media_preview.setObjectName("mediaPreview")
        media_splitter.addWidget(self.media_table)
        media_splitter.addWidget(self.media_preview)
        media_splitter.setStretchFactor(0, 1)
        layout.addWidget(media_splitter, 1)
        return page

    def _duplicates_page(self) -> QWidget:
        page, layout, self.duplicates_title, _ = self._page_shell()
        controls = QHBoxLayout()
        self.find_duplicates_button = QPushButton()
        self.duplicates_locate_button = QPushButton()
        self.duplicates_add_button = QPushButton()
        controls.addWidget(self.find_duplicates_button)
        controls.addWidget(self.duplicates_locate_button)
        controls.addWidget(self.duplicates_add_button)
        controls.addStretch()
        layout.addLayout(controls)
        self.duplicates_summary_label = QLabel()
        self.duplicates_summary_label.setObjectName("detailPanel")
        self.duplicates_summary_label.setWordWrap(True)
        layout.addWidget(self.duplicates_summary_label)
        self.duplicates_tree = QTreeWidget()
        self.duplicates_tree.setColumnCount(3)
        self.duplicates_tree.setAlternatingRowColors(True)
        self.duplicates_tree.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        layout.addWidget(self.duplicates_tree, 1)
        return page

    def _apps_page(self) -> QWidget:
        page, layout, self.apps_title, _ = self._page_shell()
        controls = QHBoxLayout()
        self.refresh_apps_button = QPushButton()
        self.uninstall_button = QPushButton()
        controls.addWidget(self.refresh_apps_button)
        controls.addWidget(self.uninstall_button)
        controls.addStretch()
        layout.addLayout(controls)
        self.apps_table = setup_table(5)
        self.apps_table.setIconSize(QSize(32, 32))
        self.apps_table.verticalHeader().setDefaultSectionSize(42)
        layout.addWidget(self.apps_table, 1)
        return page

    def _cleanup_page(self) -> QWidget:
        page, layout, self.cleanup_title, _ = self._page_shell()
        controls = QHBoxLayout()
        self.selected_size_label = QLabel()
        self.select_safe_button = QPushButton()
        self.select_all_allowed_button = QPushButton()
        self.clear_selection_button = QPushButton()
        self.cleanup_locate_button = QPushButton()
        self.recycle_button = QPushButton()
        self.recycle_button.setObjectName("primaryButton")
        controls.addWidget(self.selected_size_label)
        controls.addWidget(self.select_safe_button)
        controls.addWidget(self.select_all_allowed_button)
        controls.addWidget(self.clear_selection_button)
        controls.addStretch()
        controls.addWidget(self.cleanup_locate_button)
        controls.addWidget(self.recycle_button)
        layout.addLayout(controls)
        self.cleanup_detail_label = QLabel()
        self.cleanup_detail_label.setObjectName("detailPanel")
        self.cleanup_detail_label.setWordWrap(True)
        layout.addWidget(self.cleanup_detail_label)
        self.cleanup_table = setup_table(4)
        layout.addWidget(self.cleanup_table, 1)
        return page

    def _history_page(self) -> QWidget:
        page, layout, self.history_title, _ = self._page_shell()
        self.history_table = setup_table(5)
        layout.addWidget(self.history_table, 1)
        return page

    def _settings_page(self) -> QWidget:
        page, layout, self.settings_title, _ = self._page_shell()
        self.language_label = QLabel()
        language_row = QHBoxLayout()
        self.language_buttons: dict[LocaleCode, QPushButton] = {}
        for code, label in ((LocaleCode.ZH_CN, "简体中文"), (LocaleCode.KO_KR, "한국어")):
            button = QPushButton(label)
            button.setCheckable(True)
            button.setObjectName("languageButton")
            button.clicked.connect(lambda _checked=False, selected=code: self._language_changed(selected))
            language_row.addWidget(button)
            self.language_buttons[code] = button
        language_row.addStretch()
        self.protection_label = QLabel()
        self.protection_label.setWordWrap(True)
        self.elevate_button = QPushButton()
        layout.addWidget(self.language_label)
        layout.addLayout(language_row)
        layout.addSpacing(20)
        layout.addWidget(self.protection_label)
        layout.addWidget(self.elevate_button, 0, Qt.AlignmentFlag.AlignLeft)
        layout.addStretch()
        return page

    def _about_page(self) -> QWidget:
        page, layout, self.about_title, _ = self._page_shell()
        self.about_body = QLabel()
        self.about_body.setWordWrap(True)
        self.about_body.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.about_body.setObjectName("aboutBody")
        layout.addWidget(self.about_body, 1)
        return page

    def _connect_signals(self) -> None:
        self.nav.currentRowChanged.connect(self.stack.setCurrentIndex)
        self.scan_button.clicked.connect(self.start_scan)
        self.scan_c_button.clicked.connect(lambda: self.start_scan((Path("C:/"),)))
        self.scan_d_button.clicked.connect(lambda: self.start_scan((Path("D:/"),)))
        self.pause_button.clicked.connect(self.toggle_pause)
        self.cancel_button.clicked.connect(self.scanner.cancel)
        self.scanner.batch_ready.connect(self._scan_batch)
        self.scanner.progress.connect(self._scan_progress)
        self.scanner.finished.connect(self._scan_finished)
        self.scanner.failed.connect(self._show_error)
        self.space_filter.textChanged.connect(self._filter_space)
        self.treemap.recordActivated.connect(self._show_treemap_record)
        self.treemap.recordDoubleClicked.connect(lambda record: self._locate_path(record.path))
        self.treemap_locate_button.clicked.connect(self._locate_treemap_record)
        self.treemap_add_button.clicked.connect(self._add_treemap_record)
        self.add_selected_button.clicked.connect(lambda: self._add_selected(self.space_table, self._space_rows))
        self.space_locate_button.clicked.connect(lambda: self._locate_selected(self.space_table, self._space_rows))
        self.space_table.cellDoubleClicked.connect(lambda _row, _column: self._locate_selected(self.space_table, self._space_rows))
        self.media_add_button.clicked.connect(lambda: self._add_selected(self.media_table, self._media_rows))
        self.media_locate_button.clicked.connect(lambda: self._locate_selected(self.media_table, self._media_rows))
        self.media_open_button.clicked.connect(lambda: self._open_selected(self.media_table, self._media_rows))
        self.media_table.cellDoubleClicked.connect(lambda _row, _column: self._open_selected(self.media_table, self._media_rows))
        self.media_table.itemSelectionChanged.connect(self._show_media_preview)
        self.media_table.verticalScrollBar().valueChanged.connect(
            lambda _value: QTimer.singleShot(0, self._load_visible_media_thumbnails)
        )
        self.media_table.horizontalHeader().sortIndicatorChanged.connect(
            lambda _column, _order: QTimer.singleShot(0, self._load_visible_media_thumbnails)
        )
        self.check_media_button.clicked.connect(self._check_media)
        self.find_duplicates_button.clicked.connect(self._find_duplicates)
        self.duplicates_tree.itemDoubleClicked.connect(self._locate_duplicate_item)
        self.duplicates_locate_button.clicked.connect(self._locate_selected_duplicate)
        self.duplicates_add_button.clicked.connect(self._add_selected_duplicates)
        self.refresh_apps_button.clicked.connect(self._load_apps)
        self.uninstall_button.clicked.connect(self._uninstall_selected)
        self.cleanup_table.itemChanged.connect(self._cleanup_selection_changed)
        self.cleanup_table.itemSelectionChanged.connect(self._show_cleanup_details)
        self.cleanup_table.cellDoubleClicked.connect(lambda row, _column: self._locate_path(self.cleanup_items[row].path))
        self.select_safe_button.clicked.connect(lambda: self._select_cleanup(False))
        self.select_all_allowed_button.clicked.connect(lambda: self._select_cleanup(True))
        self.clear_selection_button.clicked.connect(self._clear_cleanup_selection)
        self.cleanup_locate_button.clicked.connect(self._locate_cleanup_selected)
        self.recycle_button.clicked.connect(self._execute_cleanup)
        self.elevate_button.clicked.connect(self._restart_elevated)

    def retranslate_ui(self) -> None:
        self.setWindowTitle(f"{tr('app.title')} — {tr('author.credit')}")
        self.author_label.setText(tr("author.credit"))
        for index, key in enumerate(self.NAV_KEYS):
            self.nav.item(index).setText(tr(key))
        self.scan_button.setText(tr("action.scan"))
        self.scan_c_button.setText(tr("action.scan_c"))
        self.scan_d_button.setText(tr("action.scan_d"))
        self.pause_button.setText(tr("action.pause"))
        self.cancel_button.setText(tr("action.cancel"))
        self.status_label.setText(tr("status.ready"))
        self.dashboard_title.setText(tr("dashboard.title"))
        self.dashboard_subtitle.setText(tr("dashboard.subtitle"))
        self.treemap_detail.setText(tr("dashboard.treemap_hint"))
        self.treemap_locate_button.setText(tr("action.locate"))
        self.treemap_add_button.setText(tr("action.add_cleanup"))
        self.space_title.setText(tr("space.title"))
        self.space_filter.setPlaceholderText(tr("space.filter"))
        self.add_selected_button.setText(tr("action.add_cleanup"))
        self.space_locate_button.setText(tr("action.locate"))
        self.space_drive_buttons["ALL"].setText(tr("filter.all_drives"))
        self.media_title.setText(tr("media.title"))
        self.check_media_button.setText(tr("action.check_media"))
        self.media_open_button.setText(tr("action.open"))
        self.media_add_button.setText(tr("action.add_cleanup"))
        self.media_locate_button.setText(tr("action.locate"))
        self.duplicates_title.setText(tr("duplicates.title"))
        self.find_duplicates_button.setText(tr("action.find_duplicates"))
        self.duplicates_locate_button.setText(tr("action.locate"))
        self.duplicates_add_button.setText(tr("action.add_cleanup"))
        self.apps_title.setText(tr("apps.title"))
        self.refresh_apps_button.setText(tr("action.refresh"))
        self.uninstall_button.setText(tr("action.uninstall"))
        self.cleanup_title.setText(tr("cleanup.title"))
        self.select_safe_button.setText(tr("action.select_safe"))
        self.select_all_allowed_button.setText(tr("action.select_all_allowed"))
        self.clear_selection_button.setText(tr("action.clear_selection"))
        self.cleanup_locate_button.setText(tr("action.locate"))
        if not self.cleanup_table.selectionModel().selectedRows():
            self.cleanup_detail_label.setText(tr("cleanup.detail_hint"))
        self.recycle_button.setText(tr("action.recycle"))
        self.history_title.setText(tr("history.title"))
        self.settings_title.setText(tr("settings.title"))
        self.language_label.setText(tr("settings.language"))
        for code, button in self.language_buttons.items():
            button.setChecked(code == self.translations.current)
        self.protection_label.setText(tr("settings.protection"))
        self.elevate_button.setText(tr("action.elevate"))
        self.about_title.setText(tr("about.title"))
        self.about_body.setText(f"CleanSpace 0.1.0\n\n{tr('about.body')}")
        self.space_table.setHorizontalHeaderLabels([tr("column.name"), tr("column.path"), tr("column.size"), tr("column.modified"), tr("column.risk")])
        self.media_table.setHorizontalHeaderLabels([tr("column.name"), tr("column.path"), tr("column.size"), tr("column.status")])
        self.duplicates_tree.setHeaderLabels([tr("column.name"), tr("column.path"), tr("column.size")])
        self.apps_table.setHorizontalHeaderLabels([tr("column.name"), tr("column.publisher"), tr("column.version"), tr("column.size"), tr("column.date")])
        self.cleanup_table.setHorizontalHeaderLabels([tr("column.path"), tr("column.size"), tr("column.risk"), tr("column.status")])
        self.history_table.setHorizontalHeaderLabels([tr("column.date"), tr("column.path"), tr("column.size"), tr("column.risk"), tr("column.result")])
        for card in self.drive_cards:
            card.refresh()
        self._refresh_results()
        self._refresh_cleanup_table()
        self._refresh_history()

    def start_scan(self, requested_roots: tuple[Path, ...] | None = None) -> None:
        if self.scanner.running:
            return
        roots = requested_roots or tuple(Path(root) for root in ("C:/", "D:/") if Path(root).exists())
        self.records.clear()
        self.scan_count = 0
        self.scan_size = 0
        self.cleanup_items.clear()
        self._refresh_results()
        self._refresh_cleanup_table()
        self.progress.setRange(0, 0)
        self.scan_button.setEnabled(False)
        self.scan_c_button.setEnabled(False)
        self.scan_d_button.setEnabled(False)
        self.pause_button.setEnabled(True)
        self.cancel_button.setEnabled(True)
        self.scanner.start(ScanOptions(roots=roots))

    def toggle_pause(self) -> None:
        if self.pause_button.text() == tr("action.pause"):
            self.scanner.pause()
            self.pause_button.setText(tr("action.resume"))
            self.status_label.setText(tr("status.paused"))
        else:
            self.scanner.resume()
            self.pause_button.setText(tr("action.pause"))

    def _scan_progress(self, count: int, size: int, path: str, errors: int) -> None:
        self.scan_count = count
        self.scan_size = size
        self.status_label.setText(format_message("status.scanning", path=path))
        self.summary_label.setText(format_message("dashboard.scanned", count=count, size=human_size(size)))
        self.error_label.setText(format_message("label.scan_errors", count=errors))

    def _scan_batch(self, batch: list[FileRecord]) -> None:
        self.records.extend(batch)
        if len(self.records) > 105_000:
            self.records = sorted(self.records, key=lambda item: item.size, reverse=True)[:100_000]
        if not self._live_refresh_timer.isActive():
            self._live_refresh_timer.start()

    def _scan_finished(self, count: int, size: int, errors: int, cancelled: bool, scan_id: int) -> None:
        self.scan_id = scan_id
        self.scan_count = count
        self.scan_size = size
        self.records = self.database.list_files(scan_id, limit=100_000)
        self.progress.setRange(0, 1)
        self.progress.setValue(1)
        self.scan_button.setEnabled(True)
        self.scan_c_button.setEnabled(True)
        self.scan_d_button.setEnabled(True)
        self.pause_button.setEnabled(False)
        self.cancel_button.setEnabled(False)
        self.pause_button.setText(tr("action.pause"))
        self.status_label.setText(tr("status.cancelled") if cancelled else tr("status.complete"))
        self.summary_label.setText(format_message("dashboard.scanned", count=count, size=human_size(size)))
        self.cleanup_items = cache_candidates(current_cache_records())
        self._refresh_results()
        self._refresh_cleanup_table()

    def _refresh_results(self, display_limit: int = 5000) -> None:
        top = sorted(self.records, key=lambda item: item.size, reverse=True)
        self._space_rows = top[:display_limit]
        self._media_rows = [item for item in top if item.extension in MEDIA_EXTENSIONS][:display_limit]
        self._populate_file_table(self.space_table, self._space_rows, media=False)
        self._populate_file_table(self.media_table, self._media_rows, media=True)
        self.treemap.set_records(top[:18])
        self._apply_space_filters()
        safe_size = sum(item.expected_size for item in self.cleanup_items if item.risk == RiskLevel.SAFE)
        large_files = [item for item in top if item.size >= 1024 ** 3]
        self.recovery_summary_label.setText(format_message(
            "dashboard.recovery_summary", safe=human_size(safe_size),
            count=len(large_files), large=human_size(sum(item.size for item in large_files)),
        ))
        if self.scan_count:
            self.summary_label.setText(format_message("dashboard.scanned", count=self.scan_count, size=human_size(self.scan_size)))

    def _populate_file_table(self, table: QTableWidget, records: list[FileRecord], media: bool) -> None:
        table.setSortingEnabled(False)
        table.setRowCount(len(records))
        for row, record in enumerate(records):
            name_item = QTableWidgetItem(record.path.name)
            name_item.setData(Qt.ItemDataRole.UserRole + 1, str(record.path))
            name_item.setToolTip(str(record.path))
            table.setItem(row, 0, name_item)
            path_item = QTableWidgetItem(str(record.path))
            path_item.setToolTip(str(record.path))
            table.setItem(row, 1, path_item)
            size_item = QTableWidgetItem(human_size(record.size))
            size_item.setData(Qt.ItemDataRole.UserRole, record.size)
            table.setItem(row, 2, size_item)
            table.setItem(row, 3, QTableWidgetItem(tr(record.media_state.value) if media else format_time(record.modified_ns)))
            if not media:
                decision = classify_path(record.path)
                risk_item = QTableWidgetItem(tr(decision.level.value))
                risk_item.setToolTip(tr(decision.reason_key))
                risk_item.setBackground(QColor({
                    RiskLevel.SAFE: "#dcfce7", RiskLevel.CAUTION: "#fef3c7", RiskLevel.BLOCKED: "#fee2e2",
                }[decision.level]))
                table.setItem(row, 4, risk_item)
        table.setSortingEnabled(True)
        table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Interactive)
        table.horizontalHeader().resizeSection(0, 250)
        table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        for column in range(2, table.columnCount()):
            table.horizontalHeader().setSectionResizeMode(column, QHeaderView.ResizeMode.ResizeToContents)
        if media:
            QTimer.singleShot(0, self._load_visible_media_thumbnails)

    def _load_visible_media_thumbnails(self) -> None:
        if not self._media_rows or self.media_table.rowCount() == 0:
            return
        first = self.media_table.rowAt(0)
        if first < 0:
            first = 0
        last = self.media_table.rowAt(self.media_table.viewport().height() - 1)
        if last < 0:
            last = min(self.media_table.rowCount() - 1, first + 24)
        for row in range(max(0, first - 3), min(self.media_table.rowCount(), last + 4)):
            item = self.media_table.item(row, 0)
            if item is None or item.data(Qt.ItemDataRole.UserRole + 10):
                continue
            record = self._record_for_row(self.media_table, self._media_rows, row)
            if record is None:
                continue
            icon = QIcon()
            if record.extension in IMAGE_EXTENSIONS:
                pixmap = QPixmap(str(record.path))
                if not pixmap.isNull():
                    icon = QIcon(pixmap.scaled(
                        QSize(64, 52), Qt.AspectRatioMode.KeepAspectRatio,
                        Qt.TransformationMode.SmoothTransformation,
                    ))
            if icon.isNull():
                icon = self._file_icon_provider.icon(QFileInfo(str(record.path)))
            item.setIcon(icon)
            item.setData(Qt.ItemDataRole.UserRole + 10, True)

    def _show_media_preview(self) -> None:
        rows = self.media_table.selectionModel().selectedRows()
        if not rows:
            self.media_preview.clear()
            return
        record = self._record_for_row(self.media_table, self._media_rows, rows[0].row())
        if record is None:
            return
        if record.extension in IMAGE_EXTENSIONS:
            pixmap = QPixmap(str(record.path))
            if not pixmap.isNull():
                self.media_preview.setPixmap(pixmap.scaled(
                    QSize(360, 360), Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation,
                ))
                return
        self.media_preview.setPixmap(QPixmap())
        self.media_preview.setText(f"{record.path.name}\n\n{human_size(record.size)}\n{tr(record.media_state.value)}")

    def _filter_space(self, query: str) -> None:
        self._apply_space_filters()

    def _set_space_drive_filter(self, drive: str) -> None:
        self._space_drive_filter = drive
        for key, button in self.space_drive_buttons.items():
            button.setChecked(key == drive)
        self._apply_space_filters()

    def _apply_space_filters(self) -> None:
        query = self.space_filter.text() if hasattr(self, "space_filter") else ""
        needle = query.casefold().strip()
        for row, record in enumerate(self._space_rows):
            drive = record.path.drive.rstrip(":").upper()
            drive_hidden = self._space_drive_filter != "ALL" and drive != self._space_drive_filter
            text_hidden = bool(needle and needle not in str(record.path).casefold())
            self.space_table.setRowHidden(row, drive_hidden or text_hidden)

    def _record_for_row(self, table: QTableWidget, source: list[FileRecord], row: int) -> FileRecord | None:
        path_text = table.item(row, 0).data(Qt.ItemDataRole.UserRole + 1) if table.item(row, 0) else ""
        path = Path(path_text) if path_text else Path()
        return next((item for item in source if item.path == path), None)

    def _locate_selected(self, table: QTableWidget, source: list[FileRecord]) -> None:
        rows = table.selectionModel().selectedRows()
        if rows:
            record = self._record_for_row(table, source, rows[0].row())
            if record is not None:
                self._locate_path(record.path)

    def _open_selected(self, table: QTableWidget, source: list[FileRecord]) -> None:
        rows = table.selectionModel().selectedRows()
        if rows:
            record = self._record_for_row(table, source, rows[0].row())
            if record is not None:
                self._open_path(record.path)

    def _open_path(self, path: Path) -> None:
        if not path.exists():
            self._show_error(tr("error.path_missing"))
            return
        if not QDesktopServices.openUrl(QUrl.fromLocalFile(str(path))):
            self._show_error(tr("error.open_failed"))

    def _locate_path(self, path: Path) -> None:
        if not path.exists():
            self._show_error(tr("error.path_missing"))
            return
        if os.name == "nt":
            result = ctypes.windll.shell32.ShellExecuteW(
                None, "open", "explorer.exe", f'/select,"{path}"', str(path.parent), 1
            )
            if result <= 32:
                self._show_error(tr("error.locate_failed"))
        else:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(path.parent)))

    def _show_treemap_record(self, record: FileRecord) -> None:
        self._treemap_record = record
        decision = classify_path(record.path)
        self.treemap_detail.setText(format_message(
            "dashboard.treemap_detail", path=record.path, size=human_size(record.size),
            risk=tr(decision.level.value), reason=tr(decision.reason_key),
        ))
        self.treemap_locate_button.setEnabled(True)
        self.treemap_add_button.setEnabled(decision.direct_delete_allowed)

    def _locate_treemap_record(self) -> None:
        if self._treemap_record is not None:
            self._locate_path(self._treemap_record.path)

    def _add_treemap_record(self) -> None:
        if self._treemap_record is not None:
            self._add_records([self._treemap_record])

    def _locate_duplicate_item(self, item: QTreeWidgetItem) -> None:
        path = item.data(0, Qt.ItemDataRole.UserRole)
        if path:
            self._locate_path(Path(path))

    def _add_selected(self, table: QTableWidget, source: list[FileRecord]) -> None:
        selected_records = []
        for index in table.selectionModel().selectedRows():
            record = self._record_for_row(table, source, index.row())
            if record is not None:
                selected_records.append(record)
        self._add_records(selected_records)

    def _add_records(self, records: list[FileRecord]) -> None:
        existing = {item.path for item in self.cleanup_items}
        for record in records:
            if record.path in existing:
                continue
            decision = classify_path(record.path)
            self.cleanup_items.append(CleanupPlanItem(
                path=record.path, expected_size=record.size, expected_modified_ns=record.modified_ns,
                expected_device=record.device, expected_inode=record.inode, risk=decision.level,
                reason_key=decision.reason_key, source_rule="user-selection",
                direct_delete_allowed=decision.direct_delete_allowed,
            ))
            existing.add(record.path)
        self._refresh_cleanup_table()
        self.nav.setCurrentRow(5)

    def _refresh_cleanup_table(self) -> None:
        checked_paths = {
            self.cleanup_items[row].path for row in range(min(self.cleanup_table.rowCount(), len(self.cleanup_items)))
            if self.cleanup_table.item(row, 0) and self.cleanup_table.item(row, 0).checkState() == Qt.CheckState.Checked
        }
        self.cleanup_table.blockSignals(True)
        self.cleanup_table.setRowCount(len(self.cleanup_items))
        for row, item in enumerate(self.cleanup_items):
            path_item = QTableWidgetItem(str(item.path))
            path_item.setToolTip(str(item.path))
            path_item.setFlags(path_item.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            if item.direct_delete_allowed:
                path_item.setCheckState(Qt.CheckState.Checked if item.path in checked_paths else Qt.CheckState.Unchecked)
            else:
                path_item.setCheckState(Qt.CheckState.Unchecked)
                path_item.setFlags(path_item.flags() & ~Qt.ItemFlag.ItemIsEnabled)
            self.cleanup_table.setItem(row, 0, path_item)
            self.cleanup_table.setItem(row, 1, QTableWidgetItem(human_size(item.expected_size)))
            risk_item = QTableWidgetItem(tr(item.risk.value))
            risk_item.setBackground(QColor({
                RiskLevel.SAFE: "#dcfce7", RiskLevel.CAUTION: "#fef3c7", RiskLevel.BLOCKED: "#fee2e2",
            }[item.risk]))
            self.cleanup_table.setItem(row, 2, risk_item)
            self.cleanup_table.setItem(row, 3, QTableWidgetItem(tr(item.reason_key)))
        header = self.cleanup_table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.Interactive)
        header.resizeSection(3, 340)
        self.cleanup_table.blockSignals(False)
        self._cleanup_selection_changed()

    def _cleanup_selection_changed(self, *_args) -> None:
        total = 0
        selected = 0
        for row, item in enumerate(self.cleanup_items):
            cell = self.cleanup_table.item(row, 0)
            if cell and cell.checkState() == Qt.CheckState.Checked:
                total += item.expected_size
                selected += 1
        self.selected_size_label.setText(format_message("label.selected_size", size=human_size(total)))
        self.recycle_button.setEnabled(selected > 0)

    def _select_cleanup(self, include_caution: bool) -> None:
        if include_caution and QMessageBox.question(
            self, tr("dialog.select_caution_title"), tr("dialog.select_caution_body")
        ) != QMessageBox.StandardButton.Yes:
            return
        self.cleanup_table.blockSignals(True)
        for row, item in enumerate(self.cleanup_items):
            allowed = item.direct_delete_allowed and (include_caution or item.risk == RiskLevel.SAFE)
            self.cleanup_table.item(row, 0).setCheckState(Qt.CheckState.Checked if allowed else Qt.CheckState.Unchecked)
        self.cleanup_table.blockSignals(False)
        self._cleanup_selection_changed()

    def _clear_cleanup_selection(self) -> None:
        self.cleanup_table.blockSignals(True)
        for row in range(self.cleanup_table.rowCount()):
            self.cleanup_table.item(row, 0).setCheckState(Qt.CheckState.Unchecked)
        self.cleanup_table.blockSignals(False)
        self._cleanup_selection_changed()

    def _locate_cleanup_selected(self) -> None:
        rows = self.cleanup_table.selectionModel().selectedRows()
        if rows:
            self._locate_path(self.cleanup_items[rows[0].row()].path)

    def _show_cleanup_details(self) -> None:
        rows = self.cleanup_table.selectionModel().selectedRows()
        if not rows:
            self.cleanup_detail_label.setText(tr("cleanup.detail_hint"))
            return
        item = self.cleanup_items[rows[0].row()]
        self.cleanup_detail_label.setText(format_message(
            "cleanup.detail", path=item.path, size=human_size(item.expected_size),
            risk=tr(item.risk.value), reason=tr(item.reason_key), source=tr(f"source.{item.source_rule}"),
        ))

    def _execute_cleanup(self) -> None:
        selected = [item for row, item in enumerate(self.cleanup_items) if self.cleanup_table.item(row, 0).checkState() == Qt.CheckState.Checked]
        if not selected:
            return
        total = sum(item.expected_size for item in selected)
        answer = QMessageBox.question(
            self, tr("dialog.cleanup_title"),
            format_message("dialog.cleanup_body", count=len(selected), size=human_size(total)),
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        results = self.cleanup_service.execute(selected)
        succeeded = {result.item.path for result in results if result.success}
        self.cleanup_items = [item for item in self.cleanup_items if item.path not in succeeded]
        self._refresh_cleanup_table()
        self._refresh_history()
        failures = [result for result in results if not result.success]
        if failures:
            QMessageBox.warning(self, tr("cleanup.title"), "\n".join(f"{item.item.path}: {tr(item.error_key)}" for item in failures[:12]))

    def _check_media(self) -> None:
        records = list(self._media_rows)
        self.check_media_button.setEnabled(False)

        def work():
            return [check_media(record) for record in records]

        def done(results: list[MediaCheckResult]) -> None:
            states = {item.path: item.state for item in results}
            for record in self._media_rows:
                record.media_state = states.get(record.path, record.media_state)
            if self.scan_id is not None:
                for item in results:
                    self.database.update_hashes(self.scan_id, item.path, perceptual_hash=item.perceptual_hash, media_state=item.state)
            self._populate_file_table(self.media_table, self._media_rows, media=True)
            self.check_media_button.setEnabled(True)
            self._similar_groups = find_similar_images(results)
            self._render_duplicate_tree()

        self._tasks.append(run_background(work, done, self._show_error))

    def _find_duplicates(self) -> None:
        self.find_duplicates_button.setEnabled(False)
        self.duplicates_tree.clear()
        self.duplicates_tree.addTopLevelItem(QTreeWidgetItem([tr("status.duplicates_working"), "", ""]))
        self.status_label.setText(tr("status.duplicates_working"))

        def done(result) -> None:
            groups, candidate_count = result
            self._exact_groups = groups
            self._render_duplicate_tree()
            if not groups:
                self.duplicates_tree.addTopLevelItem(QTreeWidgetItem([tr("duplicates.none"), "", ""]))
            self.status_label.setText(format_message("status.duplicates_complete", count=candidate_count))
            self.find_duplicates_button.setEnabled(True)

        def work(report_progress):
            records = self.database.duplicate_candidates(self.scan_id, minimum_size=1024 * 1024) if self.scan_id is not None else list(self.records)
            known = self.database.known_full_hashes(self.scan_id) if self.scan_id is not None else {}
            pending_hashes: list[tuple[Path, str]] = []
            callback = None
            if self.scan_id is not None:
                callback = lambda path, digest: pending_hashes.append((path, digest))
            groups = find_exact_duplicates(
                records, minimum_size=1024 * 1024, known_hashes=known,
                on_full_hash=callback, on_progress=report_progress,
            )
            if self.scan_id is not None and pending_hashes:
                self.database.update_full_hashes(self.scan_id, pending_hashes)
            return groups, len(records)

        def progress(current: int, total: int) -> None:
            self.status_label.setText(format_message("status.duplicates_progress", current=current, total=total))

        self._tasks.append(run_background_with_progress(work, done, self._show_error, progress))

    def _selected_duplicate_paths(self) -> list[Path]:
        paths = []
        for item in self.duplicates_tree.selectedItems():
            value = item.data(0, Qt.ItemDataRole.UserRole)
            if value:
                paths.append(Path(value))
        return paths

    def _locate_selected_duplicate(self) -> None:
        paths = self._selected_duplicate_paths()
        if paths:
            self._locate_path(paths[0])

    def _add_selected_duplicates(self) -> None:
        lookup = {
            record.path: record
            for group in self._exact_groups
            for record in group.files
        }
        records = [lookup[path] for path in self._selected_duplicate_paths() if path in lookup]
        if records:
            self._add_records(records)

    def _render_duplicate_tree(self) -> None:
        self.duplicates_tree.clear()
        reclaimable = sum(group.reclaimable_size for group in self._exact_groups)
        self.duplicates_summary_label.setText(format_message(
            "duplicates.summary", groups=len(self._exact_groups), size=human_size(reclaimable)
        ))
        if self._exact_groups:
            exact_root = QTreeWidgetItem([
                tr("duplicates.exact"),
                format_message("label.files", count=sum(len(group.files) for group in self._exact_groups)),
                "",
            ])
            self.duplicates_tree.addTopLevelItem(exact_root)
            for index, group in enumerate(self._exact_groups, 1):
                parent = QTreeWidgetItem([
                    f"#{index}", format_message("label.files", count=len(group.files)),
                    human_size(group.reclaimable_size),
                ])
                exact_root.addChild(parent)
                for record in group.files:
                    child = QTreeWidgetItem([
                        record.path.name, str(record.path.parent), human_size(record.size),
                    ])
                    child.setData(0, Qt.ItemDataRole.UserRole, str(record.path))
                    child.setToolTip(1, str(record.path))
                    parent.addChild(child)
        if self._similar_groups:
            similar_root = QTreeWidgetItem([
                tr("duplicates.similar"),
                format_message("label.files", count=sum(len(group) for group in self._similar_groups)),
                "",
            ])
            self.duplicates_tree.addTopLevelItem(similar_root)
            for index, paths in enumerate(self._similar_groups, 1):
                parent = QTreeWidgetItem([
                    format_message("label.candidate", index=index),
                    format_message("label.files", count=len(paths)), "",
                ])
                similar_root.addChild(parent)
                for path in paths:
                    try:
                        size = path.stat().st_size
                    except OSError:
                        size = 0
                    child = QTreeWidgetItem([path.name, str(path.parent), human_size(size)])
                    child.setData(0, Qt.ItemDataRole.UserRole, str(path))
                    child.setToolTip(1, str(path))
                    parent.addChild(child)
        self.duplicates_tree.expandToDepth(1)

    def _load_apps(self) -> None:
        self.refresh_apps_button.setEnabled(False)

        def done(apps) -> None:
            self._installed_apps = apps
            self.apps_table.setRowCount(len(apps))
            for row, app in enumerate(apps):
                name_item = QTableWidgetItem(app.name)
                icon_path = extract_icon_path(app.icon_path) or extract_executable(app.uninstall_command)
                if icon_path and Path(icon_path).exists():
                    name_item.setIcon(self._file_icon_provider.icon(QFileInfo(icon_path)))
                self.apps_table.setItem(row, 0, name_item)
                self.apps_table.setItem(row, 1, QTableWidgetItem(app.publisher))
                self.apps_table.setItem(row, 2, QTableWidgetItem(app.version))
                self.apps_table.setItem(row, 3, QTableWidgetItem(human_size(app.estimated_size)))
                self.apps_table.setItem(row, 4, QTableWidgetItem(app.install_date))
            self.refresh_apps_button.setEnabled(True)

        self._tasks.append(run_background(installed_apps, done, self._show_error))

    def _uninstall_selected(self) -> None:
        rows = self.apps_table.selectionModel().selectedRows()
        if not rows or not hasattr(self, "_installed_apps"):
            return
        app = self._installed_apps[rows[0].row()]
        if not uninstall_command_is_valid(app.uninstall_command):
            QDesktopServices.openUrl(QUrl("ms-settings:appsfeatures"))
            return
        if QMessageBox.question(self, tr("dialog.uninstall_title"), format_message("dialog.uninstall_body", name=app.name)) == QMessageBox.StandardButton.Yes:
            try:
                launch_uninstaller(app.uninstall_command)
            except Exception as error:
                self._show_error(str(error))

    def _refresh_history(self) -> None:
        rows = self.database.history()
        self.history_table.setRowCount(len(rows))
        for row_index, row in enumerate(rows):
            self.history_table.setItem(row_index, 0, QTableWidgetItem(row["happened_at"][:19].replace("T", " ")))
            self.history_table.setItem(row_index, 1, QTableWidgetItem(row["original_path"]))
            self.history_table.setItem(row_index, 2, QTableWidgetItem(human_size(row["estimated_size"])))
            self.history_table.setItem(row_index, 3, QTableWidgetItem(tr(row["risk"])))
            self.history_table.setItem(row_index, 4, QTableWidgetItem(row["result"]))

    def _language_changed(self, code: LocaleCode) -> None:
        if code == self.translations.current:
            return
        self.translations.install(code)
        self.retranslate_ui()

    def _restart_elevated(self) -> None:
        if os.name != "nt" or ctypes.windll.shell32.IsUserAnAdmin():
            return
        executable = sys.executable
        parameters = subprocess.list2cmdline(sys.argv)
        ctypes.windll.shell32.ShellExecuteW(None, "runas", executable, parameters, os.getcwd(), 1)

    def _show_error(self, message: str) -> None:
        self.scan_button.setEnabled(True)
        self.check_media_button.setEnabled(True)
        self.find_duplicates_button.setEnabled(True)
        self.refresh_apps_button.setEnabled(True)
        QMessageBox.warning(self, tr("app.title"), message)


STYLE_SHEET = """
QWidget { font-family: "Segoe UI", "Malgun Gothic", "Microsoft YaHei UI"; font-size: 13px; color: #1f2937; }
QMainWindow { background: #f3f6fa; }
#topBar { background: white; border-bottom: 1px solid #d9e0e8; }
#topBar QLabel { color: #1f2937; }
#brand { font-size: 22px; font-weight: 600; color: #1d2735; }
#navigation { background: #17212d; color: #cfd8e4; border: 0; padding: 8px; }
#navigation::item { min-height: 42px; padding-left: 12px; border-radius: 7px; }
#navigation::item:selected { background: #275f9f; color: white; }
#pageTitle { font-size: 25px; font-weight: 600; color: #1e2937; }
#subtitle { color: #657286; }
#summary { font-size: 17px; font-weight: 600; padding: 8px 0; }
#recoverySummary { color: #334155; background: #e8f1fb; border: 1px solid #bfd3e8; border-radius: 8px; padding: 10px; }
#detailPanel { color: #334155; background: white; border: 1px solid #d7dfe8; border-radius: 8px; padding: 9px 12px; }
#driveCard { background: white; border: 1px solid #d7dfe8; border-radius: 10px; }
#driveCard QLabel { color: #344154; }
#driveTitle { font-size: 16px; font-weight: 600; }
QProgressBar { min-height: 8px; max-height: 8px; border: 0; border-radius: 4px; background: #e2e8f0; color: #657286; }
QProgressBar::chunk { border-radius: 4px; background: #347fe6; }
QPushButton { min-height: 32px; padding: 3px 12px; border: 1px solid #bfc9d5; border-radius: 7px; background: white; }
QPushButton:hover { background: #edf3fa; }
QPushButton:disabled { color: #98a2b0; background: #edf0f3; }
#primaryButton { color: white; background: #2674dc; border-color: #2674dc; }
#primaryButton:hover { background: #195fbd; }
QLineEdit { min-height: 32px; padding: 2px 8px; border: 1px solid #c8d1dc; border-radius: 7px; background: white; }
#languageButton { min-width: 140px; min-height: 42px; font-size: 15px; }
#languageButton:checked { color: white; background: #2674dc; border-color: #2674dc; }
#languageDialog { background: #f6f8fc; }
#languageDialogBrand { font-size: 24px; font-weight: 700; color: #17365f; }
#languageDialogTitle { font-size: 20px; font-weight: 600; color: #1f2937; }
#languageDialogHint { color: #657286; margin-bottom: 8px; }
#startupLanguageButton { min-height: 66px; font-size: 16px; text-align: left; padding-left: 24px; background: white; border: 1px solid #cbd6e4; }
#startupLanguageButton:hover { background: #eaf3ff; border-color: #2674dc; }
#startupLanguageButton:default { border: 2px solid #2674dc; background: #eef5ff; }
QTableWidget, QTreeWidget { background: white; alternate-background-color: #f7f9fc; border: 1px solid #d7dfe8; border-radius: 8px; }
QHeaderView::section { background: #edf2f7; border: 0; border-bottom: 1px solid #d5dde7; padding: 7px; }
QScrollBar:vertical { width: 15px; background: #edf2f7; margin: 0; border-left: 1px solid #d3dce7; }
QScrollBar::handle:vertical { min-height: 38px; background: #6f8eae; border-radius: 6px; margin: 2px; }
QScrollBar::handle:vertical:hover { background: #47739f; }
QScrollBar:horizontal { height: 15px; background: #edf2f7; margin: 0; border-top: 1px solid #d3dce7; }
QScrollBar::handle:horizontal { min-width: 38px; background: #6f8eae; border-radius: 6px; margin: 2px; }
QScrollBar::add-line, QScrollBar::sub-line { width: 0; height: 0; }
QMessageBox { background: #f6f8fc; }
QMessageBox QLabel { color: #1f2937; min-width: 300px; }
#aboutBody { font-size: 17px; }
#mediaPreview { background: white; border: 1px solid #d7dfe8; border-radius: 8px; color: #657286; padding: 12px; }
"""
