[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$CandidateSha256Path,

    [Parameter(Mandatory = $true)]
    [string]$CandidateProvenancePath,

    [Parameter(Mandatory = $true)]
    [string]$HistoricalArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$HistoricalSha256Path,

    [Parameter(Mandatory = $true)]
    [string]$HistoricalProvenancePath,

    [ValidateRange(1024, 65535)]
    [int]$Port = 17654,

    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant"
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$historicalVersion = "0.3.21"
$historicalSourceSha = "77a5c66c431c746d2be2f283640c7951730911eb"
$historicalArtifactName = "WebAssistant-win-x64-0.3.21.exe"
$historicalSha256 = "68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271"
$installDirectoryFull = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$programDataRoot = Join-Path $env:ProgramData "WebAssistant"
$logDirectory = Join-Path $programDataRoot "logs"
$dataDirectory = Join-Path $programDataRoot "data"
$logSentinel = Join-Path $logDirectory "upgrade-preserve-log.sentinel"
$dataSentinel = Join-Path $dataDirectory "upgrade-preserve-data.sentinel"
$logSentinelContent = "webassistant-upgrade-log-state"
$dataSentinelContent = "webassistant-upgrade-data-state"

function Assert-ArtifactEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$Sha256Path,
        [Parameter(Mandatory = $true)][string]$ProvenancePath,
        [string]$ExpectedVersion = "",
        [string]$ExpectedSourceSha = "",
        [string]$ExpectedSha256 = ""
    )

    foreach ($path in @($ArtifactPath, $Sha256Path, $ProvenancePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing installer evidence/input: $path"
        }
    }

    $artifact = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ArtifactPath))
    $artifactMatch = [regex]::Match(
        $artifact.Name,
        '^WebAssistant-win-x64-(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.exe$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $artifactMatch.Success) {
        throw "Invalid Windows artifact name: $($artifact.Name)"
    }
    $version = $artifactMatch.Groups['version'].Value

    $shaFull = [IO.Path]::GetFullPath($Sha256Path)
    $provenanceFull = [IO.Path]::GetFullPath($ProvenancePath)
    if ($shaFull -ne "$($artifact.FullName).sha256") {
        throw "SHA evidence must be exact sidecar of $($artifact.Name)."
    }
    if ($provenanceFull -ne "$($artifact.FullName).provenance.json") {
        throw "Provenance must be exact sidecar of $($artifact.Name)."
    }

    $checksumParts = @(((Get-Content -LiteralPath $shaFull -Raw).Trim()) -split '\s+', 2)
    if ($checksumParts.Count -ne 2) {
        throw "Invalid SHA-256 evidence format for $($artifact.Name)."
    }
    $computedSha = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($checksumParts[0].ToLowerInvariant() -ne $computedSha -or $checksumParts[1].Trim() -ne $artifact.Name) {
        throw "SHA-256 sidecar mismatch for $($artifact.Name)."
    }

    $provenance = Get-Content -LiteralPath $provenanceFull -Raw | ConvertFrom-Json
    if ([string]$provenance.artifact -ne $artifact.Name -or
        [string]$provenance.version -ne $version -or
        [string]$provenance.rid -ne 'win-x64' -or
        [string]$provenance.sha256 -ne $computedSha -or
        [int64]$provenance.size -ne [int64]$artifact.Length) {
        throw "Provenance mismatch for $($artifact.Name)."
    }

    if ($ExpectedVersion -and $version -ne $ExpectedVersion) {
        throw "Historical version mismatch: expected $ExpectedVersion, got $version."
    }
    if ($ExpectedSourceSha -and [string]$provenance.sourceSha -ne $ExpectedSourceSha) {
        throw "Historical sourceSha mismatch."
    }
    if ($ExpectedSha256 -and $computedSha -ne $ExpectedSha256) {
        throw "Historical artifact SHA-256 mismatch."
    }

    return [pscustomobject]@{
        Path = $artifact.FullName
        Version = $version
        Sha256 = $computedSha
    }
}

function Get-WebAssistantBundleEntries {
    $entries = @()
    foreach ($registryPath in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        $entries += @(Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -eq 'WebAssistant' })
    }
    return @($entries | Where-Object {
        ([string]$_.UninstallString -match '(?i)(^|\s)/uninstall(\s|$)') -or
        ([string]$_.QuietUninstallString -match '(?i)(^|\s)/uninstall(\s|$)')
    })
}

function Invoke-Bundle {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Operation,
        [switch]$AllowFailure
    )

    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru
    if (-not $AllowFailure -and $process.ExitCode -ne 0) {
        throw "WebAssistant bundle $Operation failed with exit code $($process.ExitCode)."
    }
    return $process.ExitCode
}

function Wait-Health {
    param([int]$ExpectedPort)
    $uri = "http://127.0.0.1:$ExpectedPort/v1/health"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200 -and $response.Content -match '"status"\s*:\s*"ok"') {
                return
            }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "WebAssistant health did not become available on $uri."
}

function Invoke-Scanners {
    param([int]$ExpectedPort)

    $response = Invoke-WebRequest `
        -Uri "http://127.0.0.1:$ExpectedPort/v1/scanners" `
        -TimeoutSec 15 `
        -SkipHttpErrorCheck
    if ($response.StatusCode -notin @(200, 503)) {
        throw "Unexpected /v1/scanners status: $($response.StatusCode)."
    }
    return $response
}

function Get-PackageOwnedWorkers {
    param([int]$ParentProcessId)

    $rootPrefix = "$installDirectoryFull\"
    return @(Get-CimInstance Win32_Process -Filter "Name='NAPS2.Worker.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            [int]$_.ParentProcessId -eq $ParentProcessId -and
            -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
            [IO.Path]::GetFullPath([string]$_.ExecutablePath).StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase)
        })
}

function Wait-PackageOwnedWorker {
    param([int]$ParentProcessId)

    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $workers = @(Get-PackageOwnedWorkers -ParentProcessId $ParentProcessId)
        if ($workers.Count -gt 0) {
            return $workers
        }
        Start-Sleep -Milliseconds 250
    }
    throw "GET /v1/scanners did not materialize a package-owned NAPS2.Worker for service pid $ParentProcessId."
}

function Get-ProcessIdentity {
    param([int]$ProcessId)

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    return [pscustomobject]@{
        ProcessId = $ProcessId
        StartTimeUtc = $process.StartTime.ToUniversalTime()
    }
}

function Assert-ProcessIdentityGone {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $process = Get-Process -Id ([int]$Identity.ProcessId) -ErrorAction SilentlyContinue
    if (-not $process) {
        return
    }

    $startTimeUtc = $process.StartTime.ToUniversalTime()
    if ($startTimeUtc -eq $Identity.StartTimeUtc) {
        throw "$Description pid=$($Identity.ProcessId) survived when its exact process identity had to be gone."
    }
}

function Assert-NoFilesInUseEvidence {
    param([Parameter(Mandatory = $true)][string]$PrimaryLog)

    if (-not (Test-Path -LiteralPath $PrimaryLog -PathType Leaf)) {
        throw "Burn did not create expected log: $PrimaryLog"
    }

    $directory = Split-Path -Parent $PrimaryLog
    $prefix = [IO.Path]::GetFileName($PrimaryLog)
    $logs = @(Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })

    $matches = @()
    foreach ($log in $logs) {
        $matches += @(Select-String `
            -LiteralPath $log.FullName `
            -Pattern 'FilesInUse|MsiRMFilesInUse' `
            -CaseSensitive:$false `
            -ErrorAction SilentlyContinue)
    }

    if ($matches.Count -gt 0) {
        $evidence = @($matches | Select-Object -First 10 | ForEach-Object {
            "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
        }) -join [Environment]::NewLine
        throw "Installer emitted Files In Use evidence:`n$evidence"
    }
}

function Assert-ProgramDataSentinels {
    foreach ($pair in @(
        @($logSentinel, $logSentinelContent),
        @($dataSentinel, $dataSentinelContent))) {
        $path = [string]$pair[0]
        $expected = [string]$pair[1]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "ProgramData preservation sentinel disappeared: $path"
        }
        if ((Get-Content -LiteralPath $path -Raw).Trim() -ne $expected) {
            throw "ProgramData preservation sentinel changed: $path"
        }
    }
}

function Assert-CandidateInstalled {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [int]$ExpectedPort
    )

    $entries = @(Get-WebAssistantBundleEntries | Where-Object {
        [string]$_.DisplayVersion -eq $ExpectedVersion
    })
    if ($entries.Count -ne 1) {
        $versions = @((Get-WebAssistantBundleEntries | ForEach-Object { [string]$_.DisplayVersion })) -join ', '
        throw "Expected exactly one WebAssistant Installed Apps entry at $ExpectedVersion; found $($entries.Count), versions=[$versions]."
    }

    $installedExe = Join-Path $installDirectoryFull 'WebAssistant.exe'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "Installed WebAssistant.exe is missing after candidate operation."
    }
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($installedVersion) -or
        -not $installedVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Installed executable version '$installedVersion' does not match candidate $ExpectedVersion."
    }

    (Get-Service -Name $serviceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Wait-Health -ExpectedPort $ExpectedPort
}

function Wait-ServiceAbsent {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "WebAssistant service remained registered after uninstall."
}

$candidate = Assert-ArtifactEvidence `
    -ArtifactPath $CandidateArtifactPath `
    -Sha256Path $CandidateSha256Path `
    -ProvenancePath $CandidateProvenancePath
$historical = Assert-ArtifactEvidence `
    -ArtifactPath $HistoricalArtifactPath `
    -Sha256Path $HistoricalSha256Path `
    -ProvenancePath $HistoricalProvenancePath `
    -ExpectedVersion $historicalVersion `
    -ExpectedSourceSha $historicalSourceSha `
    -ExpectedSha256 $historicalSha256

if ((Split-Path -Leaf $historical.Path) -ne $historicalArtifactName) {
    throw "Historical artifact must be exactly $historicalArtifactName."
}
if ([version]$candidate.Version -le [version]$historical.Version) {
    throw "Candidate version $($candidate.Version) must be newer than historical $($historical.Version)."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Windows upgrade acceptance requires an elevated runner."
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "WebAssistant service already exists before upgrade acceptance."
}
if (Test-Path -LiteralPath $installDirectoryFull) {
    throw "WebAssistant install directory already exists before upgrade acceptance."
}
if ((Get-WebAssistantBundleEntries).Count -ne 0) {
    throw "WebAssistant Installed Apps entry already exists before upgrade acceptance."
}

$historicalInstalled = $false
$candidateInstalled = $false
$capturedHistoricalIdentities = @()
$capturedCandidateIdentities = @()
$burnLog = Join-Path $env:RUNNER_TEMP 'webassistant-live-worker-upgrade.log'
$repairLog = Join-Path $env:RUNNER_TEMP 'webassistant-same-version-repair.log'
$downgradeLog = Join-Path $env:RUNNER_TEMP 'webassistant-downgrade-rejection.log'
$uninstallLog = Join-Path $env:RUNNER_TEMP 'webassistant-candidate-uninstall.log'

try {
    Invoke-Bundle `
        -Executable $historical.Path `
        -Arguments @('/quiet', '/norestart') `
        -Operation 'historical install' | Out-Null
    $historicalInstalled = $true

    (Get-Service -Name $serviceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Wait-Health -ExpectedPort $Port

    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
        throw "Historical runtime did not create ProgramData logs/data directories."
    }
    Set-Content -LiteralPath $logSentinel -Value $logSentinelContent -NoNewline
    Set-Content -LiteralPath $dataSentinel -Value $dataSentinelContent -NoNewline

    Invoke-Scanners -ExpectedPort $Port | Out-Null

    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $oldServicePid = [int]$serviceInfo.ProcessId
    if ($oldServicePid -le 0) {
        throw "Historical WebAssistant service has no running process id."
    }

    $capturedHistoricalIdentities += Get-ProcessIdentity -ProcessId $oldServicePid
    $ownedWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $oldServicePid)
    foreach ($worker in $ownedWorkers) {
        $capturedHistoricalIdentities += Get-ProcessIdentity -ProcessId ([int]$worker.ProcessId)
    }
    Write-Host "historical_service_pid=$oldServicePid owned_worker_pids=$(@($ownedWorkers | ForEach-Object { [int]$_.ProcessId }) -join ',')"

    if (Test-Path -LiteralPath $burnLog) {
        Remove-Item -LiteralPath $burnLog -Force
    }

    # Critical reproduction: no Stop-Service and no historical uninstall before candidate execution.
    Invoke-Bundle `
        -Executable $candidate.Path `
        -Arguments @('/quiet', '/norestart', '/log', $burnLog) `
        -Operation "live-worker upgrade $historicalVersion -> $($candidate.Version)" | Out-Null
    $candidateInstalled = $true
    $historicalInstalled = $false

    Assert-NoFilesInUseEvidence -PrimaryLog $burnLog
    foreach ($capturedIdentity in $capturedHistoricalIdentities) {
        Assert-ProcessIdentityGone -Identity $capturedIdentity -Description 'Historical runtime process'
    }
    Assert-CandidateInstalled -ExpectedVersion $candidate.Version -ExpectedPort $Port
    Assert-ProgramDataSentinels

    # Candidate runtime shutdown invariant: once SCM reports stopped, no worker owned by that service instance may remain.
    Invoke-Scanners -ExpectedPort $Port | Out-Null
    $candidateServiceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $candidateServicePid = [int]$candidateServiceInfo.ProcessId
    if ($candidateServicePid -le 0) {
        throw "Candidate WebAssistant service has no running process id."
    }
    $candidateWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $candidateServicePid)
    $candidateServiceIdentity = Get-ProcessIdentity -ProcessId $candidateServicePid
    $candidateWorkerIdentities = @($candidateWorkers | ForEach-Object {
        Get-ProcessIdentity -ProcessId ([int]$_.ProcessId)
    })
    $capturedCandidateIdentities += $candidateServiceIdentity
    $capturedCandidateIdentities += $candidateWorkerIdentities

    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))
    Assert-ProcessIdentityGone -Identity $candidateServiceIdentity -Description 'Candidate service process after Stop-Service'
    foreach ($workerIdentity in $candidateWorkerIdentities) {
        Assert-ProcessIdentityGone -Identity $workerIdentity -Description 'Candidate NAPS2.Worker after Stop-Service'
    }

    Start-Service -Name $serviceName
    Assert-CandidateInstalled -ExpectedVersion $candidate.Version -ExpectedPort $Port
    Invoke-Scanners -ExpectedPort $Port | Out-Null

    Restart-Service -Name $serviceName
    Assert-CandidateInstalled -ExpectedVersion $candidate.Version -ExpectedPort $Port
    Invoke-Scanners -ExpectedPort $Port | Out-Null
    Assert-ProgramDataSentinels

    # Same-version behavior is canonical Burn maintenance/repair, with the service running and scanner worker materialized.
    $repairServiceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $repairServicePid = [int]$repairServiceInfo.ProcessId
    Wait-PackageOwnedWorker -ParentProcessId $repairServicePid | Out-Null
    if (Test-Path -LiteralPath $repairLog) {
        Remove-Item -LiteralPath $repairLog -Force
    }
    Invoke-Bundle `
        -Executable $candidate.Path `
        -Arguments @('/repair', '/quiet', '/norestart', '/log', $repairLog) `
        -Operation "same-version repair $($candidate.Version)" | Out-Null
    Assert-NoFilesInUseEvidence -PrimaryLog $repairLog
    Assert-CandidateInstalled -ExpectedVersion $candidate.Version -ExpectedPort $Port
    Invoke-Scanners -ExpectedPort $Port | Out-Null
    Assert-ProgramDataSentinels

    # Downgrade must be rejected and leave the candidate intact.
    if (Test-Path -LiteralPath $downgradeLog) {
        Remove-Item -LiteralPath $downgradeLog -Force
    }
    $downgradeExitCode = Invoke-Bundle `
        -Executable $historical.Path `
        -Arguments @('/quiet', '/norestart', '/log', $downgradeLog) `
        -Operation "downgrade $($candidate.Version) -> $historicalVersion" `
        -AllowFailure
    if ($downgradeExitCode -eq 0) {
        throw "Historical $historicalVersion installer did not reject downgrade from candidate $($candidate.Version)."
    }
    Assert-CandidateInstalled -ExpectedVersion $candidate.Version -ExpectedPort $Port
    Invoke-Scanners -ExpectedPort $Port | Out-Null
    Assert-ProgramDataSentinels

    $finalCandidateSha = (Get-FileHash -LiteralPath $candidate.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($finalCandidateSha -ne $candidate.Sha256) {
        throw "Candidate artifact bytes changed during upgrade acceptance."
    }

    # Uninstall the candidate from a live scanner state, then prove product removal and ProgramData preservation.
    $uninstallServiceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $uninstallServicePid = [int]$uninstallServiceInfo.ProcessId
    $uninstallWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $uninstallServicePid)
    $uninstallWorkerIdentities = @($uninstallWorkers | ForEach-Object {
        Get-ProcessIdentity -ProcessId ([int]$_.ProcessId)
    })

    if (Test-Path -LiteralPath $uninstallLog) {
        Remove-Item -LiteralPath $uninstallLog -Force
    }
    Invoke-Bundle `
        -Executable $candidate.Path `
        -Arguments @('/uninstall', '/quiet', '/norestart', '/log', $uninstallLog) `
        -Operation 'candidate uninstall' | Out-Null
    $candidateInstalled = $false

    Wait-ServiceAbsent
    if (Test-Path -LiteralPath $installDirectoryFull) {
        throw "Program Files WebAssistant payload remained after candidate uninstall."
    }
    if ((Get-WebAssistantBundleEntries).Count -ne 0) {
        throw "WebAssistant Installed Apps entry remained after candidate uninstall."
    }
    foreach ($workerIdentity in $uninstallWorkerIdentities) {
        Assert-ProcessIdentityGone -Identity $workerIdentity -Description 'Candidate NAPS2.Worker after uninstall'
    }
    Assert-ProgramDataSentinels

    Write-Host "windows_live_worker_upgrade_acceptance=PASS from=$historicalVersion to=$($candidate.Version)"
}
finally {
    $ErrorActionPreference = 'Continue'

    if ($candidateInstalled) {
        Invoke-Bundle `
            -Executable $candidate.Path `
            -Arguments @('/uninstall', '/quiet', '/norestart') `
            -Operation 'candidate cleanup uninstall' `
            -AllowFailure | Out-Null
    }
    elseif ($historicalInstalled) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        foreach ($capturedIdentity in $capturedHistoricalIdentities) {
            $process = Get-Process -Id ([int]$capturedIdentity.ProcessId) -ErrorAction SilentlyContinue
            if ($process -and $process.StartTime.ToUniversalTime() -eq $capturedIdentity.StartTimeUtc) {
                Stop-Process -Id ([int]$capturedIdentity.ProcessId) -Force -ErrorAction SilentlyContinue
            }
        }
        Invoke-Bundle `
            -Executable $historical.Path `
            -Arguments @('/uninstall', '/quiet', '/norestart') `
            -Operation 'historical cleanup uninstall' `
            -AllowFailure | Out-Null
    }
}