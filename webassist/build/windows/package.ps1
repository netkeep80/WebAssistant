[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$productRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$projectPath = Join-Path $productRoot "src/WebAssistant/WebAssistant.csproj"
$versionPath = Join-Path $productRoot "VERSION"
$installRoot = Join-Path $productRoot "install/windows"

if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "Отсутствует canonical VERSION: $versionPath"
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Некорректный VERSION: $version"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $productRoot "artifacts/windows-x64"
}

$packageRoot = [IO.Path]::GetFullPath($OutputDirectory)
$appDirectory = Join-Path $packageRoot "app"

if (Test-Path $packageRoot) {
    Remove-Item $packageRoot -Recurse -Force
}
New-Item $appDirectory -ItemType Directory -Force | Out-Null

& dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    "-p:ProductVersion=$version" `
    --output $appDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось собрать Windows package WebAssistant."
}

Copy-Item $versionPath (Join-Path $packageRoot "VERSION")
Copy-Item (Join-Path $installRoot "install.ps1") $packageRoot
Copy-Item (Join-Path $installRoot "install.bat") $packageRoot
Copy-Item (Join-Path $installRoot "uninstall.ps1") $packageRoot
Copy-Item (Join-Path $installRoot "uninstall.bat") $packageRoot

$executable = Join-Path $appDirectory "WebAssistant.exe"
if (-not (Test-Path $executable)) {
    throw "В package отсутствует WebAssistant.exe."
}

Write-Host "Windows package создан: $packageRoot (version $version)"
