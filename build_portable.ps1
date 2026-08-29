$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvPython = Join-Path $ProjectRoot '.venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $VenvPython)) {
    python -m venv (Join-Path $ProjectRoot '.venv')
}

& $VenvPython -m pip install --upgrade pip
& $VenvPython -m pip install -r (Join-Path $ProjectRoot 'requirements.txt') pyinstaller
& $VenvPython -m pytest
& $VenvPython -m PyInstaller --noconfirm --clean (Join-Path $ProjectRoot 'CleanSpace.spec')

Write-Host "Portable build: $ProjectRoot\dist\CleanSpace.exe"
