# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path


project_root = Path(SPECPATH)

a = Analysis(
    [str(project_root / "main.py")],
    pathex=[str(project_root / "src")],
    binaries=[],
    datas=[
        (str(project_root / "src" / "cleanspace" / "locales"), "cleanspace/locales"),
        (str(project_root / "src" / "cleanspace" / "assets"), "cleanspace/assets"),
        (str(project_root / "README.md"), "."),
        (str(project_root / "THIRD_PARTY_NOTICES.md"), "."),
    ],
    hiddenimports=["send2trash.plat_win_modern"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=1,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="CleanSpace",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    version=str(project_root / "build" / "windows_version_info.txt"),
    icon=str(project_root / "src" / "cleanspace" / "assets" / "cleanspace.ico"),
    uac_admin=False,
)
