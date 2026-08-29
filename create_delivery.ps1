$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Desktop = [Environment]::GetFolderPath('Desktop')
$BaseName = 'CleanSpace_허준영_可发送版_20260828'
$Delivery = Join-Path $Desktop $BaseName
$Suffix = 1
while (Test-Path -LiteralPath $Delivery) {
    $Delivery = Join-Path $Desktop ("{0}_{1}" -f $BaseName, $Suffix)
    $Suffix++
}
New-Item -ItemType Directory -Path $Delivery | Out-Null

$CleanSpaceExe = Join-Path $ProjectRoot 'dist\CleanSpace.exe'
$FFprobeExe = (Get-Command ffprobe -ErrorAction Stop).Source
Copy-Item -LiteralPath $CleanSpaceExe -Destination (Join-Path $Delivery 'CleanSpace.exe')
Copy-Item -LiteralPath $FFprobeExe -Destination (Join-Path $Delivery 'ffprobe.exe')
Copy-Item -LiteralPath (Join-Path $ProjectRoot '使用说明_先看这里.txt') -Destination $Delivery
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'THIRD_PARTY_NOTICES.md') -Destination $Delivery
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'FFMPEG_SOURCE_AND_LICENSE.txt') -Destination $Delivery

$HashLines = foreach ($Name in 'CleanSpace.exe', 'ffprobe.exe') {
    $Hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Delivery $Name)
    "{0}  {1}" -f $Hash.Hash, $Name
}
$HashLines | Set-Content -LiteralPath (Join-Path $Delivery 'SHA256校验值.txt') -Encoding UTF8

$ZipPath = "$Delivery.zip"
Compress-Archive -LiteralPath $Delivery -DestinationPath $ZipPath -CompressionLevel Optimal
Write-Output "DELIVERY_FOLDER=$Delivery"
Write-Output "DELIVERY_ZIP=$ZipPath"
