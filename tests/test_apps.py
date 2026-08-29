from cleanspace.apps import extract_icon_path


def test_extract_icon_path_handles_windows_icon_index():
    assert extract_icon_path(r'"C:\Program Files\Example\app.exe",0') == r"C:\Program Files\Example\app.exe"
    assert extract_icon_path(r"C:\Windows\System32\shell32.dll,-42") == r"C:\Windows\System32\shell32.dll"
