[CmdletBinding()]
param(
    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant",
    [ValidateRange(1024, 65535)]
    [int]$Port = 17654,
    [string]$ExpectedVersion = ""
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$installedConfigFile = Join-Path $InstallDirectory "appsettings.json"
$logDirectory = Join-Path $env:ProgramData "WebAssistant\logs"
$dataDirectory = Join-Path $env:ProgramData "WebAssistant\data"

function Wait-Health {
    param([int]$ExpectedPort)
    $uri = "http://127.0.0.1:$ExpectedPort/v1/health"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and $response.Content -match '"status"\s*:\s*"ok"') {
                return
            }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "WebAssistant health не стал доступен на $uri."
}

function Assert-DailyLog {
    if (-not (Test-Path $logDirectory -PathType Container)) {
        throw "Не создан каталог журналов WebAssistant: $logDirectory"
    }
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $logFile = Get-ChildItem -Path $logDirectory -Filter "webassistant-*.log" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($logFile) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "WebAssistant не создал суточный журнал после локального запроса."
}

function Assert-LoopbackOnly {
    param([int]$ExpectedPort)
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $ExpectedPort -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 0) {
        throw "Не найден слушающий сокет WebAssistant на порту $ExpectedPort."
    }
    $unexpected = @($listeners | Where-Object { $_.LocalAddress -ne "127.0.0.1" })
    if ($unexpected.Count -ne 0) {
        $addresses = ($unexpected | Select-Object -ExpandProperty LocalAddress -Unique) -join ", "
        throw "WebAssistant слушает не только loopback: $addresses"
    }
}

function Wait-NoListener {
    param([int]$ExpectedPort)
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $ExpectedPort -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "После остановки WebAssistant порт $ExpectedPort остаётся занят."
}

$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    throw "После установки WebAssistant не находится в состоянии Running."
}

$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($serviceInfo.StartMode -ne "Auto") {
    throw "WebAssistant зарегистрирован не с автоматическим запуском: $($serviceInfo.StartMode)."
}

$expectedExecutable = Join-Path $InstallDirectory "WebAssistant.exe"
if (-not (Test-Path -LiteralPath $expectedExecutable -PathType Leaf)) {
    throw "После установки отсутствует WebAssistant.exe."
}
if ($serviceInfo.PathName -notlike "*$expectedExecutable*") {
    throw "SCM указывает не на установленный WebAssistant.exe: $($serviceInfo.PathName)"
}
if (-not (Test-Path -LiteralPath $installedConfigFile -PathType Leaf)) {
    throw "Installer не установил package-owned appsettings.json: $installedConfigFile"
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $productVersion = (Get-Item -LiteralPath $expectedExecutable).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion) -or -not $productVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Installed executable version '$productVersion' не соответствует VERSION '$ExpectedVersion'."
    }
}

Wait-Health -ExpectedPort $Port
Assert-DailyLog
Assert-LoopbackOnly -ExpectedPort $Port
if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
    throw "Не создан runtime data directory: $dataDirectory"
}

Stop-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
    [TimeSpan]::FromSeconds(30))
Wait-NoListener -ExpectedPort $Port

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Running,
    [TimeSpan]::FromSeconds(30))
Wait-Health -ExpectedPort $Port
Assert-DailyLog
Assert-LoopbackOnly -ExpectedPort $Port

Restart-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Running,
    [TimeSpan]::FromSeconds(30))
Wait-Health -ExpectedPort $Port
Assert-LoopbackOnly -ExpectedPort $Port

Write-Host "windows_installed_service_acceptance=PASS"
