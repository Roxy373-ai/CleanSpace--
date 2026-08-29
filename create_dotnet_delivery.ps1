param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [string]$DestinationRoot
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exe = Join-Path $publishRoot "CleanSpace.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "CleanSpace.exe not found in $publishRoot" }

$desktop = if ([string]::IsNullOrWhiteSpace($DestinationRoot)) { [Environment]::GetFolderPath("Desktop") } else { (Resolve-Path -LiteralPath $DestinationRoot).Path }
if ([string]::IsNullOrWhiteSpace($desktop)) { $desktop = "D:\Users\Roxy\Desktop" }
$baseName = "CleanSpace_v1.2.1_win-x64"
$delivery = Join-Path $desktop $baseName
$suffix = 2
while (Test-Path -LiteralPath $delivery) {
    $delivery = Join-Path $desktop ("{0}_{1}" -f $baseName, $suffix)
    $suffix++
}

New-Item -ItemType Directory -Path $delivery | Out-Null
Get-ChildItem -LiteralPath $publishRoot | Copy-Item -Destination $delivery -Recurse -Force

$ffprobe = Get-ChildItem -LiteralPath "C:\Users\Roxy\AppData\Local\Microsoft\WinGet\Packages" -Filter "ffprobe.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ffprobe) { Copy-Item -LiteralPath $ffprobe.FullName -Destination (Join-Path $delivery "ffprobe.exe") -Force }

Copy-Item -LiteralPath (Join-Path $projectRoot "CleanSpace_Bilingual_User_Guide.txt") -Destination $delivery
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") -Destination $delivery
Copy-Item -LiteralPath (Join-Path $projectRoot "FFMPEG_SOURCE_AND_LICENSE.txt") -Destination $delivery
Copy-Item -LiteralPath (Join-Path $projectRoot "RELEASE_NOTES_v1.2.1_中韩.txt") -Destination $delivery
Copy-Item -LiteralPath (Join-Path $projectRoot "GITHUB_RELEASE_UPLOAD_GUIDE_中韩.txt") -Destination $delivery

@"
CleanSpace 软件位置 / 프로그램 위치
====================================

作者 / 제작자：허준영

本机可发送正式版：
$delivery

启动文件：
$(Join-Path $delivery "CleanSpace.exe")

把整个文件夹或同名 ZIP 发给别人。对方解压后直接双击 CleanSpace.exe，
不需要安装 Python、.NET 或其他运行环境。请不要只单独复制 EXE。

전체 폴더 또는 같은 이름의 ZIP을 전달하세요. 압축을 푼 뒤 CleanSpace.exe를
더블 클릭하면 Python이나 .NET을 별도로 설치하지 않아도 됩니다.
"@ | Set-Content -LiteralPath (Join-Path $delivery "软件位置_请看这里.txt") -Encoding UTF8

$hashes = Get-ChildItem -LiteralPath $delivery -File | Sort-Object Name | Get-FileHash -Algorithm SHA256
$hashLines = $hashes | ForEach-Object { "{0}  {1}" -f $_.Hash, (Split-Path -Leaf $_.Path) }
$hashLines | Set-Content -LiteralPath (Join-Path $delivery "SHA256校验值.txt") -Encoding UTF8

$zip = "$delivery.zip"
Compress-Archive -LiteralPath $delivery -DestinationPath $zip -CompressionLevel Optimal

Write-Output "DELIVERY_DIR=$delivery"
Write-Output "DELIVERY_ZIP=$zip"
