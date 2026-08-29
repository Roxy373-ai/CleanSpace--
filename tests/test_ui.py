import os
import time

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings
from PySide6.QtCore import Qt
from PySide6.QtWidgets import QApplication, QPushButton

from cleanspace.app import LanguageSelectionDialog
from cleanspace.database import Database
from cleanspace.i18n import TranslationManager
from cleanspace.models import LocaleCode
from cleanspace.models import FileRecord
from cleanspace.ui import CleanSpaceWindow


def test_live_language_switch_preserves_author_and_navigation(tmp_path):
    app = QApplication.instance() or QApplication([])
    manager = TranslationManager(app)
    manager.settings = QSettings(str(tmp_path / "settings.ini"), QSettings.Format.IniFormat)
    manager.install(LocaleCode.ZH_CN)
    window = CleanSpaceWindow(Database(tmp_path / "ui.sqlite"), manager)
    window.language_buttons[LocaleCode.KO_KR].click()
    app.processEvents()
    assert "허준영 제작" in window.windowTitle()
    assert window.nav.item(0).text() == "디스크 개요"
    assert window.nav.item(8).text() == "정보"
    window.close()


def test_startup_language_dialog_has_two_visible_choices():
    app = QApplication.instance() or QApplication([])
    dialog = LanguageSelectionDialog(LocaleCode.ZH_CN)
    labels = {button.text() for button in dialog.findChildren(QPushButton)}
    assert any("简体中文" in label for label in labels)
    assert any("한국어" in label for label in labels)
    dialog.close()


def test_media_thumbnail_survives_size_sort_and_path_is_complete(tmp_path):
    from PIL import Image

    app = QApplication.instance() or QApplication([])
    manager = TranslationManager(app)
    manager.settings = QSettings(str(tmp_path / "thumb-settings.ini"), QSettings.Format.IniFormat)
    manager.install(LocaleCode.ZH_CN)
    image_path = tmp_path / "缩略图.png"
    Image.new("RGB", (80, 60), "cornflowerblue").save(image_path)
    stat = image_path.stat()
    record = FileRecord(image_path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, ".png")
    window = CleanSpaceWindow(Database(tmp_path / "thumb-ui.sqlite"), manager)
    window.records = [record]
    window.scan_count = 1
    window.scan_size = record.size
    window._refresh_results()
    window.show()
    app.processEvents()
    window.media_table.sortItems(2, Qt.SortOrder.DescendingOrder)
    window._load_visible_media_thumbnails()
    assert window.media_table.item(0, 1).text() == str(image_path)
    assert not window.media_table.item(0, 0).icon().isNull()
    assert window.space_table.item(0, 4).text() in {"谨慎可删", "禁止直删", "安全可删"}
    window.close()


def test_scan_batch_is_visible_before_scan_finishes(tmp_path):
    app = QApplication.instance() or QApplication([])
    manager = TranslationManager(app)
    manager.settings = QSettings(str(tmp_path / "live-settings.ini"), QSettings.Format.IniFormat)
    manager.install(LocaleCode.ZH_CN)
    path = tmp_path / "live.bin"
    path.write_bytes(b"live")
    stat = path.stat()
    record = FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, ".bin")
    window = CleanSpaceWindow(Database(tmp_path / "live-ui.sqlite"), manager)
    window._scan_batch([record])
    window._refresh_results(display_limit=500)
    assert window.space_table.rowCount() == 1
    assert window.space_table.item(0, 1).text() == str(path)
    window.close()


def test_duplicate_button_runs_with_progress_callback(tmp_path):
    app = QApplication.instance() or QApplication([])
    manager = TranslationManager(app)
    manager.settings = QSettings(str(tmp_path / "duplicate-settings.ini"), QSettings.Format.IniFormat)
    manager.install(LocaleCode.ZH_CN)
    database = Database(tmp_path / "duplicate-ui.sqlite")
    scan_id = database.start_scan([tmp_path])
    paths = [tmp_path / "first.bin", tmp_path / "second.bin"]
    paths[0].write_bytes(b"d" * (1024 * 1024 + 64))
    paths[1].write_bytes(paths[0].read_bytes())
    records = []
    for path in paths:
        stat = path.stat()
        records.append(FileRecord(path, stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino, ".bin"))
    database.add_files(scan_id, records)
    database.finish_scan(scan_id, count=2, size=sum(item.size for item in records), errors=0, cancelled=False)
    window = CleanSpaceWindow(database, manager)
    errors = []
    window._show_error = errors.append
    window._find_duplicates()
    deadline = time.monotonic() + 5
    while window._tasks[-1]._thread.is_alive() and time.monotonic() < deadline:
        app.processEvents()
        time.sleep(0.01)
    app.processEvents()
    assert errors == []
    assert len(window._exact_groups) == 1
    assert window.find_duplicates_button.isEnabled()
    window.close()
