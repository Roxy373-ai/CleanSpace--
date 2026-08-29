$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
$project = Join-Path $projectRoot "src-dotnet\CleanSpace\CleanSpace.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$output = Join-Path $projectRoot "dist-dotnet\CleanSpace-$stamp"

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "Project-local .NET SDK not found: $dotnet"
}

$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".tools\dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".tools\nuget-packages"
$env:APPDATA = Join-Path $projectRoot ".tools\appdata"

& $dotnet restore $project --runtime win-x64 -p:PublishReadyToRun=true --configfile (Join-Path $projectRoot "NuGet.Publish.Config") --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

& $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false `
    -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Output "PUBLISH_DIR=$output"
Write-Output "EXE=$(Join-Path $output 'CleanSpace.exe')"
