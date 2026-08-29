from __future__ import annotations

import json
import locale
from pathlib import Path

from PySide6.QtCore import QCoreApplication, QSettings, QTranslator

from .models import LocaleCode


class JsonTranslator(QTranslator):
    def __init__(self, messages: dict[str, str]) -> None:
        super().__init__()
        self.messages = messages

    def translate(self, context: str, source_text: str, disambiguation=None, n: int = -1) -> str:
        return self.messages.get(source_text, source_text)


class TranslationManager:
    def __init__(self, app: QCoreApplication) -> None:
        self.app = app
        self.settings = QSettings("허준영", "CleanSpace")
        self._translator: JsonTranslator | None = None
        self.current = self._initial_locale()

    def _initial_locale(self) -> LocaleCode:
        saved = self.settings.value("locale", "", type=str)
        if saved in {item.value for item in LocaleCode}:
            return LocaleCode(saved)
        language = (locale.getlocale()[0] or "").lower()
        return LocaleCode.KO_KR if language.startswith("ko") else LocaleCode.ZH_CN

    def install(self, locale_code: LocaleCode | str) -> None:
        code = LocaleCode(locale_code)
        if self._translator is not None:
            self.app.removeTranslator(self._translator)
        locale_path = Path(__file__).parent / "locales" / f"{code.value}.json"
        messages = json.loads(locale_path.read_text(encoding="utf-8"))
        self._translator = JsonTranslator(messages)
        self.app.installTranslator(self._translator)
        self.current = code
        self.settings.setValue("locale", code.value)


def tr(key: str) -> str:
    return QCoreApplication.translate("CleanSpace", key)

