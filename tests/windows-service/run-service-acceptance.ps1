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
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') + '\'

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

function Assert-ScannersEndpoint {
    param([int]$ExpectedPort)

    $uri = "http://127.0.0.1:$ExpectedPort/v1/scanners"
    $response = Invoke-WebRequest -Uri $uri -TimeoutSec 15
    if ($response.StatusCode -ne 200) {
        throw "WebAssistant scanners endpoint вернул HTTP $($response.StatusCode)."
    }

    try {
        $payload = $response.Content | ConvertFrom-Json
    }
    catch {
        throw "WebAssistant scanners endpoint вернул некорректный JSON: $($_.Exception.Message)"
    }

    if ($null -eq $payload.scanners -or $null -eq $payload.warnings) {
        throw "WebAssistant scanners endpoint не содержит canonical scanners/warnings shape."
    }
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

function ConvertTo-ProcessIdentity {
    param([Parameter(Mandatory = $true)]$Process)

    $imagePath = [string]$Process.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($imagePath)) {
        throw "Не удалось получить ExecutablePath процесса PID=$($Process.ProcessId)."
    }
    if ($null -eq $Process.CreationDate) {
        throw "Не удалось получить CreationDate процесса PID=$($Process.ProcessId)."
    }

    return [pscustomobject]@{
        ProcessId = [int]$Process.ProcessId
        ParentProcessId = [int]$Process.ParentProcessId
        ImagePath = [IO.Path]::GetFullPath($imagePath)
        CreationDate = ([datetime]$Process.CreationDate).ToUniversalTime()
    }
}

function Get-ProcessIdentityById {
    param([int]$ProcessId)

    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }

    return ConvertTo-ProcessIdentity -Process $process
}

function Test-SameProcessIdentityAlive {
    param([Parameter(Mandatory = $true)]$Identity)

    $current = Get-ProcessIdentityById -ProcessId $Identity.ProcessId
    if ($null -eq $current) {
        return $false
    }

    return `
        $current.CreationDate.Ticks -eq $Identity.CreationDate.Ticks -and `
        [string]::Equals($current.ImagePath, $Identity.ImagePath, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ProcessIdentityGone {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (Test-SameProcessIdentityAlive -Identity $Identity) {
        throw "$Description всё ещё жив после SCM Stopped: PID=$($Identity.ProcessId), path=$($Identity.ImagePath), created=$($Identity.CreationDate.ToString('O'))."
    }
}

function Get-PackageWorkers {
    param([Nullable[int]]$ExpectedParentProcessId = $null)

    $workers = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='NAPS2.Worker.exe'" -ErrorAction SilentlyContinue)) {
        $path = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($null -ne $ExpectedParentProcessId -and
            [int]$process.ParentProcessId -ne $ExpectedParentProcessId.Value) {
            continue
        }

        $workers += ConvertTo-ProcessIdentity -Process $process
    }

    return @($workers)
}

function Wait-CapturedPackageWorkers {
    param([int]$ExpectedParentProcessId)

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $workers = @(Get-PackageWorkers -ExpectedParentProcessId $ExpectedParentProcessId)
        if ($workers.Count -gt 0) {
            return $workers
        }
        Start-Sleep -Milliseconds 100
    }

    throw "После GET /v1/scanners не появился package-owned NAPS2.Worker с ParentProcessId=$ExpectedParentProcessId."
}

function Assert-NoPackageWorkers {
    $workers = @(Get-PackageWorkers)
    if ($workers.Count -ne 0) {
        $description = ($workers | ForEach-Object {
            "PID=$($_.ProcessId), parent=$($_.ParentProcessId), path=$($_.ImagePath), created=$($_.CreationDate.ToString('O'))"
        }) -join '; '
        throw "После SCM Stopped под install root остались package-owned NAPS2.Worker: $description"
    }
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

# Materialize the lazy Windows scanner subsystem before testing service shutdown.
Assert-ScannersEndpoint -ExpectedPort $Port
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
$serviceProcessId = [int]$serviceInfo.ProcessId
if ($serviceProcessId -le 0) {
    throw "SCM не вернул PID работающей службы WebAssistant."
}
$capturedServiceProcess = Get-ProcessIdentityById -ProcessId $serviceProcessId
if ($null -eq $capturedServiceProcess) {
    throw "Не удалось захватить identity процесса службы WebAssistant PID=$serviceProcessId."
}
if (-not [string]::Equals(
    $capturedServiceProcess.ImagePath,
    [IO.Path]::GetFullPath($expectedExecutable),
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Захваченный процесс службы указывает не на canonical WebAssistant.exe: $($capturedServiceProcess.ImagePath)"
}

$capturedWorkers = @(Wait-CapturedPackageWorkers -ExpectedParentProcessId $serviceProcessId)
if ($capturedWorkers.Count -eq 0) {
    throw "Не захвачен ни один package-owned NAPS2.Worker перед Stop-Service."
}
foreach ($worker in $capturedWorkers) {
    if ($worker.ParentProcessId -ne $serviceProcessId) {
        throw "Worker ownership mismatch: PID=$($worker.ProcessId), parent=$($worker.ParentProcessId), expected=$serviceProcessId."
    }
}

Stop-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
    [TimeSpan]::FromSeconds(30))

# Strong invariant: once SCM reports Stopped, exact captured process identities and
# all package-owned workers under the install root must already be gone.
Assert-ProcessIdentityGone -Identity $capturedServiceProcess -Description "WebAssistant service process"
foreach ($worker in $capturedWorkers) {
    Assert-ProcessIdentityGone -Identity $worker -Description "Captured package-owned NAPS2.Worker"
}
Assert-NoPackageWorkers
Wait-NoListener -ExpectedPort $Port

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Running,
    [TimeSpan]::FromSeconds(30))
Wait-Health -ExpectedPort $Port
Assert-ScannersEndpoint -ExpectedPort $Port
Assert-DailyLog
Assert-LoopbackOnly -ExpectedPort $Port

Restart-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Running,
    [TimeSpan]::FromSeconds(30))
Wait-Health -ExpectedPort $Port
Assert-ScannersEndpoint -ExpectedPort $Port
Assert-LoopbackOnly -ExpectedPort $Port

Write-Host "windows_installed_service_acceptance=PASS captured_workers=$($capturedWorkers.Count)"
