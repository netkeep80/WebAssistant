[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,
    [ValidateRange(1024, 65535)]
    [int]$Port = 17654
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$productRoot = Join-Path $repositoryRoot "webassist"
$packageBatch = Join-Path $productRoot "build/windows/package.bat"
$sourceInstallBatch = Join-Path $productRoot "install/windows/install.bat"
$packageDirectory = Join-Path $productRoot "artifacts/windows-x64"
$installScript = Join-Path $packageDirectory "install.ps1"
$uninstallScript = Join-Path $packageDirectory "uninstall.ps1"
$logDirectory = Join-Path $env:ProgramData "WebAssistant\logs"
$uninstalled = $false

function Wait-Health {
    param([int]$ExpectedPort)
    $uri = "http://127.0.0.1:$ExpectedPort/v1/health"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and $response.Content -match '"status"\s*:\s*"ok"') { return }
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
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($logFile) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "WebAssistant не создал суточный журнал после локального запроса."
}

function Assert-LoopbackOnly {
    param([int]$ExpectedPort)
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $ExpectedPort -ErrorAction SilentlyContinue)
    if ($listeners.Count -eq 0) { throw "Не найден слушающий сокет WebAssistant на порту $ExpectedPort." }
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
        if ($listeners.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "После остановки WebAssistant порт $ExpectedPort остаётся занят."
}

if (-not (Test-Path $packageBatch)) { throw "Отсутствует package.bat: $packageBatch" }
if (-not (Test-Path $sourceInstallBatch)) { throw "Отсутствует source-tree install.bat: $sourceInstallBatch" }

$originalLocation = Get-Location
try {
    Set-Location $env:RUNNER_TEMP
    & $packageBatch
    if ($LASTEXITCODE -ne 0) { throw "package.bat без аргументов завершился с кодом $LASTEXITCODE." }
}
finally {
    Set-Location $originalLocation
}

if (-not (Test-Path (Join-Path $packageDirectory "app/WebAssistant.exe"))) {
    throw "Canonical Windows package не содержит app/WebAssistant.exe."
}
if (-not (Test-Path $installScript)) { throw "В canonical package отсутствует install.ps1." }
if (-not (Test-Path $uninstallScript)) { throw "В canonical package отсутствует uninstall.ps1." }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "На контрольной машине уже зарегистрирована служба WebAssistant."
}

try {
    & $sourceInstallBatch -InstallDirectory $InstallDirectory -Port $Port
    if ($LASTEXITCODE -ne 0) {
        throw "Source-tree install.bat завершился с кодом $LASTEXITCODE."
    }

    $service = Get-Service -Name $serviceName
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw "После установки WebAssistant не находится в состоянии Running."
    }

    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($serviceInfo.StartMode -ne "Auto") {
        throw "WebAssistant зарегистрирован не с автоматическим запуском: $($serviceInfo.StartMode)."
    }

    $expectedExecutable = Join-Path $InstallDirectory "WebAssistant.exe"
    if (-not (Test-Path $expectedExecutable)) { throw "После установки отсутствует WebAssistant.exe." }
    if ($serviceInfo.PathName -notlike "*$expectedExecutable*") {
        throw "SCM указывает не на установленный WebAssistant.exe: $($serviceInfo.PathName)"
    }

    Wait-Health -ExpectedPort $Port
    Assert-DailyLog
    Assert-LoopbackOnly -ExpectedPort $Port

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
    Assert-DailyLog
    Assert-LoopbackOnly -ExpectedPort $Port

    & $uninstallScript -InstallDirectory $InstallDirectory
    $uninstalled = $true

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "Служба осталась после uninstall." }
    if (Test-Path $InstallDirectory) { throw "Каталог приложения остался после uninstall." }
    if (Get-Process -Name "WebAssistant" -ErrorAction SilentlyContinue) { throw "Процесс остался после uninstall." }
    if (-not (Test-Path $logDirectory -PathType Container)) { throw "Uninstall не должен удалять журналы по умолчанию." }
    Wait-NoListener -ExpectedPort $Port

    Write-Host "windows_source_tree_service_acceptance=PASS"
}
finally {
    if (-not $uninstalled) {
        try {
            if (Test-Path $uninstallScript) { & $uninstallScript -InstallDirectory $InstallDirectory }
        }
        catch {
            Write-Warning "Штатная очистка после ошибки не удалась: $($_.Exception.Message)"
            & sc.exe stop $serviceName 2>$null | Out-Null
            & sc.exe delete $serviceName 2>$null | Out-Null
            if (Test-Path $InstallDirectory) { Remove-Item $InstallDirectory -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
