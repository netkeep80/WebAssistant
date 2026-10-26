[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant",
    [switch]$PurgeData,
    [switch]$PurgeConfig
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$dataRoot = "$env:ProgramData\WebAssistant"
$configDirectory = Join-Path $dataRoot "config"
$logDirectory = Join-Path $dataRoot "logs"
$runtimeDataDirectory = Join-Path $dataRoot "data"

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }
    & sc.exe delete $serviceName | Out-Null
}

if (Test-Path $InstallDirectory) {
    Remove-Item $InstallDirectory -Recurse -Force
}

if ($PurgeData) {
    foreach ($path in @($logDirectory, $runtimeDataDirectory)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

if ($PurgeConfig -and (Test-Path -LiteralPath $configDirectory)) {
    Remove-Item -LiteralPath $configDirectory -Recurse -Force
}

if ($PurgeConfig) {
    Write-Host "WebAssistant удалён; persistent configuration удалена явно."
} else {
    Write-Host "WebAssistant удалён; persistent configuration в $configDirectory сохранена."
}
