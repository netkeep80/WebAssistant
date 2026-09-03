[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$productRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$projectPath = Join-Path $productRoot "src/WebAssistant/WebAssistant.csproj"
$installRoot = Join-Path $productRoot "install/windows"

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
    --output $appDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось собрать Windows package WebAssistant."
}

Copy-Item (Join-Path $installRoot "install.ps1") $packageRoot
Copy-Item (Join-Path $installRoot "install.bat") $packageRoot
Copy-Item (Join-Path $installRoot "uninstall.ps1") $packageRoot
Copy-Item (Join-Path $installRoot "uninstall.bat") $packageRoot

$executable = Join-Path $appDirectory "WebAssistant.exe"
if (-not (Test-Path $executable)) {
    throw "В package отсутствует WebAssistant.exe."
}

Write-Host "Windows package создан: $packageRoot"
