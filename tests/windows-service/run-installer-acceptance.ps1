[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$Sha256Path,

    [Parameter(Mandatory = $true)]
    [string]$ProvenancePath,

    [ValidateRange(1024, 65535)]
    [int]$Port = 17654,

    [string]$InstallDirectory = "$env:ProgramFiles\WebAssistant"
)

$ErrorActionPreference = "Stop"
$serviceName = "WebAssistant"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceAcceptance = Join-Path $scriptDirectory "run-service-acceptance.ps1"

function Get-WebAssistantBundleEntries {
    $registryPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $entries = @()
    foreach ($registryPath in $registryPaths) {
        $entries += @(Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -eq "WebAssistant" })
    }

    return @($entries | Where-Object {
        $uninstall = [string]$_.UninstallString
        $quietUninstall = [string]$_.QuietUninstallString
        $uninstall -match '(?i)(^|\s)/uninstall(\s|$)' -or
            $quietUninstall -match '(?i)(^|\s)/uninstall(\s|$)'
    })
}

function Wait-ServiceAbsent {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Служба WebAssistant осталась зарегистрированной после uninstall."
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
    throw "После uninstall WebAssistant порт $ExpectedPort остаётся занят."
}

function Invoke-ExactBundle {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$Operation
    )

    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList $Arguments `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "WebAssistant bundle $Operation завершился с кодом $($process.ExitCode)."
    }
}

foreach ($requiredPath in @($ArtifactPath, $Sha256Path, $ProvenancePath, $serviceAcceptance)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Отсутствует обязательный Windows installer evidence/input: $requiredPath"
    }
}

$artifactPathFull = [IO.Path]::GetFullPath($ArtifactPath)
$sha256PathFull = [IO.Path]::GetFullPath($Sha256Path)
$provenancePathFull = [IO.Path]::GetFullPath($ProvenancePath)
$artifactInfo = Get-Item -LiteralPath $artifactPathFull
$artifactName = $artifactInfo.Name

$artifactMatch = [regex]::Match(
    $artifactName,
    '^WebAssistant-win-x64-(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.exe$',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $artifactMatch.Success) {
    throw "Некорректное имя Windows installer artifact: $artifactName"
}
$version = $artifactMatch.Groups['version'].Value

if ($sha256PathFull -ne "$artifactPathFull.sha256") {
    throw "SHA-256 evidence должен быть sidecar exact artifact: $artifactName.sha256"
}
if ($provenancePathFull -ne "$artifactPathFull.provenance.json") {
    throw "Provenance должен быть sidecar exact artifact: $artifactName.provenance.json"
}

$checksumLine = (Get-Content -LiteralPath $sha256PathFull -Raw).Trim()
$checksumParts = @($checksumLine -split '\s+', 2)
if ($checksumParts.Count -ne 2) {
    throw "Некорректный формат Windows SHA-256 evidence."
}
$recordedSha256 = $checksumParts[0].ToLowerInvariant()
$recordedArtifact = $checksumParts[1].Trim()
$computedSha256 = (Get-FileHash -LiteralPath $artifactPathFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($recordedSha256 -ne $computedSha256) {
    throw "SHA-256 evidence не соответствует exact Windows artifact bytes."
}
if ($recordedArtifact -ne $artifactName) {
    throw "SHA-256 evidence относится к другому artifact: $recordedArtifact"
}

$provenance = Get-Content -LiteralPath $provenancePathFull -Raw | ConvertFrom-Json
$requiredFields = @(
    "artifact",
    "version",
    "sourceSha",
    "rid",
    "sha256",
    "size",
    "sdkVersion",
    "configMode",
    "packageEntrypoint"
)
foreach ($field in $requiredFields) {
    if ($provenance.PSObject.Properties.Name -notcontains $field) {
        throw "Provenance не содержит обязательное поле: $field"
    }
}

if ([string]$provenance.artifact -ne $artifactName) {
    throw "Provenance artifact mismatch."
}
if ([string]$provenance.version -ne $version) {
    throw "Provenance version mismatch."
}
if ([string]$provenance.rid -ne "win-x64") {
    throw "Provenance rid mismatch."
}
if ([string]$provenance.sha256 -ne $computedSha256) {
    throw "Provenance sha256 mismatch."
}
if ([int64]$provenance.size -ne [int64]$artifactInfo.Length) {
    throw "Provenance size mismatch."
}
if ([string]$provenance.configMode -notin @("source-appsettings", "generated-default")) {
    throw "Provenance configMode находится вне закрытого enum."
}
foreach ($field in @("sourceSha", "sdkVersion")) {
    if ([string]::IsNullOrWhiteSpace([string]$provenance.$field)) {
        throw "Provenance $field должен быть непустой строкой."
    }
}
if ([string]$provenance.packageEntrypoint -ne "build/windows/package.bat") {
    throw "Provenance packageEntrypoint не соответствует canonical Windows producer."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Windows installer acceptance требует elevated runner для проверки machine-wide install."
}

$installDirectoryFull = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$programFilesFull = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
if (-not $installDirectoryFull.StartsWith(
    "$programFilesFull\",
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Machine-wide WebAssistant должен устанавливаться под Program Files: $installDirectoryFull"
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "До acceptance уже зарегистрирована служба WebAssistant."
}
if (Test-Path -LiteralPath $installDirectoryFull) {
    throw "До acceptance уже существует install directory WebAssistant: $installDirectoryFull"
}
if ((Get-WebAssistantBundleEntries).Count -ne 0) {
    throw "До acceptance уже существует WebAssistant entry в Installed Apps."
}

$installed = $false
try {
    Invoke-ExactBundle `
        -Executable $artifactPathFull `
        -Arguments @('/quiet', '/norestart') `
        -Operation "install"
    $installed = $true

    $installedConfig = Join-Path $installDirectoryFull "appsettings.json"
    if (-not (Test-Path -LiteralPath $installedConfig -PathType Leaf)) {
        throw "WiX installer не установил package-owned appsettings.json."
    }
    $installedConfigHash = (Get-FileHash -LiteralPath $installedConfig -Algorithm SHA256).Hash.ToLowerInvariant()

    $bundleEntries = @(Get-WebAssistantBundleEntries | Where-Object {
        [string]$_.DisplayVersion -eq $version
    })
    if ($bundleEntries.Count -ne 1) {
        $versions = @((Get-WebAssistantBundleEntries | ForEach-Object { [string]$_.DisplayVersion })) -join ', '
        throw "Ожидался ровно один WebAssistant Installed Apps entry с DisplayVersion=$version; найдено $($bundleEntries.Count), versions=[$versions]."
    }

    $uninstallCommand = [string]$bundleEntries[0].UninstallString
    $quietUninstallCommand = [string]$bundleEntries[0].QuietUninstallString
    if ($uninstallCommand -notmatch '(?i)(^|\s)/uninstall(\s|$)' -and
        $quietUninstallCommand -notmatch '(?i)(^|\s)/uninstall(\s|$)') {
        throw "Installed Apps uninstall command не принадлежит WiX Burn bundle."
    }

    & $serviceAcceptance `
        -InstallDirectory $installDirectoryFull `
        -Port $Port `
        -ExpectedVersion $version

    $configHashAfterLifecycle = (Get-FileHash -LiteralPath $installedConfig -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($configHashAfterLifecycle -ne $installedConfigHash) {
        throw "Installed package-owned appsettings.json изменился во время lifecycle acceptance."
    }

    $shaAfterLifecycle = (Get-FileHash -LiteralPath $artifactPathFull -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($shaAfterLifecycle -ne $computedSha256) {
        throw "Windows artifact bytes изменились после install/lifecycle acceptance."
    }

    Invoke-ExactBundle `
        -Executable $artifactPathFull `
        -Arguments @('/uninstall', '/quiet', '/norestart') `
        -Operation "uninstall"
    $installed = $false

    Wait-ServiceAbsent
    Wait-NoListener -ExpectedPort $Port

    if (Test-Path -LiteralPath $installDirectoryFull) {
        throw "Program Files directory WebAssistant остался после uninstall."
    }
    if (Get-Process -Name "WebAssistant" -ErrorAction SilentlyContinue) {
        throw "Процесс WebAssistant остался после uninstall."
    }
    if (@(Get-WebAssistantBundleEntries | Where-Object {
        [string]$_.DisplayVersion -eq $version
    }).Count -ne 0) {
        throw "WebAssistant Installed Apps entry остался после uninstall."
    }

    $finalSha256 = (Get-FileHash -LiteralPath $artifactPathFull -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($finalSha256 -ne $computedSha256) {
        throw "Windows artifact bytes изменились после uninstall."
    }

    Write-Host "windows_installer_artifact_acceptance=PASS version=$version sha256=$computedSha256"
}
finally {
    if ($installed) {
        try {
            Invoke-ExactBundle `
                -Executable $artifactPathFull `
                -Arguments @('/uninstall', '/quiet', '/norestart') `
                -Operation "cleanup uninstall"
        }
        catch {
            Write-Warning "Emergency WiX cleanup failed: $($_.Exception.Message)"
        }
    }
}
