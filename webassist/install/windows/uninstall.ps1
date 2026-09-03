[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant",
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$dataRoot = "$env:ProgramData\WebAssistant"

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

if ($PurgeData -and (Test-Path $dataRoot)) {
    Remove-Item $dataRoot -Recurse -Force
    Write-Host "WebAssistant удалён вместе с журналами и данными."
} else {
    Write-Host "WebAssistant удалён. Данные в $dataRoot сохранены."
}
