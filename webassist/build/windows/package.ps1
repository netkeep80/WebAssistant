[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$productRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$projectPath = Join-Path $productRoot "src/WebAssistant/WebAssistant.csproj"
$sourceConfigPath = Join-Path $productRoot "src/WebAssistant/appsettings.json"
$defaultConfigPath = Join-Path $productRoot "build/common/default-appsettings.json"
$provenanceWriter = Join-Path $productRoot "build/common/write-provenance.ps1"
$installerRoot = Join-Path $productRoot "build/windows/installer"
$packageProject = Join-Path $installerRoot "WebAssistant.Package.wixproj"
$bundleProject = Join-Path $installerRoot "WebAssistant.Bundle.wixproj"
$versionPath = Join-Path $productRoot "VERSION"

foreach ($requiredPath in @(
    $versionPath,
    $defaultConfigPath,
    $provenanceWriter,
    $packageProject,
    $bundleProject)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Отсутствует обязательный installer build input: $requiredPath"
    }
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Некорректный VERSION: $version"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $productRoot "artifacts/windows-x64"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item $outputRoot -ItemType Directory -Force | Out-Null

$artifactName = "WebAssistant-win-x64-$version.exe"
$artifactPath = Join-Path $outputRoot $artifactName
$stagingRoot = Join-Path $outputRoot (".webassistant-windows-stage-" + [Guid]::NewGuid().ToString("N"))
$appDirectory = Join-Path $stagingRoot "app"
$msiOutput = Join-Path $stagingRoot "msi"
$bundleOutput = Join-Path $stagingRoot "bundle"

foreach ($path in @($artifactPath, "$artifactPath.sha256", "$artifactPath.provenance.json")) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
New-Item $appDirectory -ItemType Directory -Force | Out-Null
New-Item $msiOutput -ItemType Directory -Force | Out-Null
New-Item $bundleOutput -ItemType Directory -Force | Out-Null

try {
    & dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        "-p:ProductVersion=$version" `
        --output $appDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось собрать self-contained Windows payload WebAssistant."
    }

    $packageConfigPath = Join-Path $appDirectory "appsettings.json"
    if (Test-Path -LiteralPath $sourceConfigPath) {
        Copy-Item -LiteralPath $sourceConfigPath -Destination $packageConfigPath -Force
        $configMode = "source-appsettings"
    }
    else {
        Copy-Item -LiteralPath $defaultConfigPath -Destination $packageConfigPath -Force
        $configMode = "generated-default"
    }

    $executable = Join-Path $appDirectory "WebAssistant.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "В staged payload отсутствует WebAssistant.exe."
    }
    if (-not (Test-Path -LiteralPath $packageConfigPath -PathType Leaf)) {
        throw "В staged payload отсутствует appsettings.json."
    }

    & dotnet build $packageProject `
        --configuration Release `
        "-p:PayloadRoot=$appDirectory" `
        "-p:ProductVersion=$version" `
        "-p:OutputPath=$msiOutput"
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось собрать внутренний MSI WebAssistant."
    }

    $msiCandidates = @(Get-ChildItem -LiteralPath $msiOutput -Filter "WebAssistant.Package.msi" -File -Recurse)
    if ($msiCandidates.Count -ne 1) {
        throw "Ожидался ровно один внутренний MSI WebAssistant.Package.msi; найдено: $($msiCandidates.Count)."
    }
    $msiPath = $msiCandidates[0].FullName

    & dotnet build $bundleProject `
        --configuration Release `
        "-p:MsiPath=$msiPath" `
        "-p:ProductVersion=$version" `
        "-p:OutputPath=$bundleOutput"
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось собрать финальный WiX bundle WebAssistant."
    }

    $bundleCandidates = @(Get-ChildItem -LiteralPath $bundleOutput -Filter "WebAssistant.Bundle.exe" -File -Recurse)
    if ($bundleCandidates.Count -ne 1) {
        throw "Ожидался ровно один WebAssistant.Bundle.exe; найдено: $($bundleCandidates.Count)."
    }

    Copy-Item -LiteralPath $bundleCandidates[0].FullName -Destination $artifactPath -Force
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Не создан canonical Windows artifact: $artifactPath"
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
        $sdkVersion = "unknown"
    }
    $sourceSha = $env:WEBASSISTANT_SOURCE_SHA
    if ([string]::IsNullOrWhiteSpace($sourceSha)) {
        $sourceSha = "unknown"
    }

    & $provenanceWriter `
        -ArtifactPath $artifactPath `
        -Version $version `
        -SourceSha $sourceSha `
        -Rid "win-x64" `
        -SdkVersion $sdkVersion `
        -ConfigMode $configMode `
        -PackageEntrypoint "build/windows/package.bat" | Out-Null

    if (-not (Test-Path -LiteralPath "$artifactPath.sha256" -PathType Leaf)) {
        throw "Не создан SHA-256 evidence: $artifactPath.sha256"
    }
    if (-not (Test-Path -LiteralPath "$artifactPath.provenance.json" -PathType Leaf)) {
        throw "Не создан provenance evidence: $artifactPath.provenance.json"
    }

    $recordedSha = ((Get-Content -LiteralPath "$artifactPath.sha256" -Raw).Trim() -split '\s+')[0]
    $finalSha = Get-Sha256Hex -Path $artifactPath
    if ($recordedSha -ne $finalSha) {
        throw "Windows artifact bytes изменились после фиксации SHA-256."
    }

    Write-Host "Windows artifact создан: $artifactPath (version $version, config $configMode)"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
