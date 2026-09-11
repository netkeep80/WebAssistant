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

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$historicalVersion = "0.3.21"
$historicalSourceSha = "77a5c66c431c746d2be2f283640c7951730911eb"
$historicalArtifactName = "WebAssistant-win-x64-0.3.21.exe"
$historicalSha256 = "68fe8a0145721c13f14dad4a4d3fea333c9ccb240b036d8869aa2152af7b7271"
$installDirectoryFull = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')

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
$capturedWorkerPids = @()
$oldServicePid = 0
$burnLog = Join-Path $env:RUNNER_TEMP 'webassistant-live-worker-upgrade.log'

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

    $scannerResponse = Invoke-WebRequest `
        -Uri "http://127.0.0.1:$Port/v1/scanners" `
        -TimeoutSec 15 `
        -SkipHttpErrorCheck
    if ($scannerResponse.StatusCode -notin @(200, 503)) {
        throw "Unexpected historical /v1/scanners status: $($scannerResponse.StatusCode)."
    }

    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $oldServicePid = [int]$serviceInfo.ProcessId
    if ($oldServicePid -le 0) {
        throw "Historical WebAssistant service has no running process id."
    }

    $ownedWorkers = @(Wait-PackageOwnedWorker -ParentProcessId $oldServicePid)
    $capturedWorkerPids = @($ownedWorkers | ForEach-Object { [int]$_.ProcessId })
    Write-Host "historical_service_pid=$oldServicePid owned_worker_pids=$($capturedWorkerPids -join ',')"

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

    foreach ($processId in @($oldServicePid) + $capturedWorkerPids) {
        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            throw "Old runtime process pid=$processId survived successful candidate upgrade."
        }
    }

    $entries = @(Get-WebAssistantBundleEntries | Where-Object {
        [string]$_.DisplayVersion -eq $candidate.Version
    })
    if ($entries.Count -ne 1) {
        throw "Expected exactly one WebAssistant Installed Apps entry at $($candidate.Version); found $($entries.Count)."
    }

    $installedExe = Join-Path $installDirectoryFull 'WebAssistant.exe'
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($installedVersion) -or
        -not $installedVersion.StartsWith($candidate.Version, [StringComparison]::Ordinal)) {
        throw "Installed executable version '$installedVersion' does not match candidate $($candidate.Version)."
    }

    (Get-Service -Name $serviceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Wait-Health -ExpectedPort $Port

    $finalCandidateSha = (Get-FileHash -LiteralPath $candidate.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($finalCandidateSha -ne $candidate.Sha256) {
        throw "Candidate artifact bytes changed during upgrade acceptance."
    }

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
        foreach ($worker in @(Get-CimInstance Win32_Process -Filter "Name='NAPS2.Worker.exe'" -ErrorAction SilentlyContinue)) {
            $path = [string]$worker.ExecutablePath
            if (-not [string]::IsNullOrWhiteSpace($path) -and
                [IO.Path]::GetFullPath($path).StartsWith("$installDirectoryFull\", [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id ([int]$worker.ProcessId) -Force -ErrorAction SilentlyContinue
            }
        }
        Invoke-Bundle `
            -Executable $historical.Path `
            -Arguments @('/uninstall', '/quiet', '/norestart') `
            -Operation 'historical cleanup uninstall' `
            -AllowFailure | Out-Null
    }
}
