[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant",
    [int]$Port = 17654,
    [string[]]$AllowedOrigins = @(),
    [string]$FileSystemRootDirectory = "$env:ProgramData\WebAssistant\data"
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$sourceDirectory = Join-Path $PSScriptRoot "app"
$logDirectory = "$env:ProgramData\WebAssistant\logs"
$configFile = Join-Path $InstallDirectory "appsettings.json"

if (-not (Test-Path $sourceDirectory)) {
    throw "Не найден каталог package app: $sourceDirectory"
}
if ($Port -lt 1024 -or $Port -gt 65535) {
    throw "Port должен быть от 1024 до 65535."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

if (Test-Path $InstallDirectory) {
    Remove-Item $InstallDirectory -Recurse -Force
}
New-Item $InstallDirectory -ItemType Directory -Force | Out-Null
New-Item $logDirectory -ItemType Directory -Force | Out-Null
New-Item $FileSystemRootDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $sourceDirectory "*") $InstallDirectory -Recurse -Force

$configuration = @{
    WebAssistant = @{
        Port = $Port
        LogDirectory = $logDirectory
        Cors = @{
            Enabled = ($AllowedOrigins.Count -gt 0)
            AllowedOrigins = @($AllowedOrigins)
        }
        FileSystem = @{
            RootDirectory = $FileSystemRootDirectory
        }
    }
}
$configuration | ConvertTo-Json -Depth 6 | Set-Content -Path $configFile -Encoding utf8

$executable = Join-Path $InstallDirectory "WebAssistant.exe"
if (-not (Test-Path $executable)) {
    throw "В package отсутствует WebAssistant.exe."
}

$quotedExecutable = '"' + $executable + '"'
& sc.exe create $serviceName binPath= $quotedExecutable start= auto | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось создать службу WebAssistant."
}
& sc.exe description $serviceName "WebAssistant local service" | Out-Null
Start-Service -Name $serviceName

Write-Host "WebAssistant установлен в $InstallDirectory"
Write-Host "Настройки: $configFile"
