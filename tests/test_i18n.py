import json
from pathlib import Path


def test_locale_catalogs_have_identical_keys_and_credit():
    root = Path("src/cleanspace/locales")
    zh = json.loads((root / "zh-CN.json").read_text(encoding="utf-8"))
    ko = json.loads((root / "ko-KR.json").read_text(encoding="utf-8"))
    assert set(zh) == set(ko)
    assert "허준영" in zh["author.credit"]
    assert "허준영" in ko["author.credit"]
    assert all(value.strip() for value in zh.values())
    assert all(value.strip() for value in ko.values())

