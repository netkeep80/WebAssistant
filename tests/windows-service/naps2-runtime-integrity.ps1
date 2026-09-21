[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Get-CandidateNaps2SdkEvidence {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $projectPath = Join-Path $RepositoryRoot "webassist/src/WebAssistant/WebAssistant.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Не найден WebAssistant.csproj для определения repository-owned NAPS2 SDK: $projectPath"
    }

    $projectText = Get-Content -LiteralPath $projectPath -Raw
    $packageMatch = [regex]::Match(
        $projectText,
        '<PackageReference\s+Include="WebAssistant\.NAPS2\.Sdk"\s+Version="(?<version>[^"]+)"\s*/>',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $packageMatch.Success) {
        throw "WebAssistant.csproj не содержит однозначную ссылку на WebAssistant.NAPS2.Sdk."
    }

    $packageVersion = $packageMatch.Groups['version'].Value
    $packagePath = Join-Path $RepositoryRoot "webassist/vendor/nuget/WebAssistant.NAPS2.Sdk.$packageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Не найден repository-owned NAPS2 SDK package: $packagePath"
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName -eq 'lib/net10.0/NAPS2.Sdk.dll'
        })
        if ($entries.Count -ne 1) {
            throw "Ожидался ровно один lib/net10.0/NAPS2.Sdk.dll в $packagePath; найдено $($entries.Count)."
        }

        $stream = $entries[0].Open()
        try {
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $sha256 = [Convert]::ToHexString($algorithm.ComputeHash($stream)).ToLowerInvariant()
            }
            finally {
                $algorithm.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    return [pscustomobject]@{
        PackagePath = [IO.Path]::GetFullPath($packagePath)
        PackageVersion = $packageVersion
        Entry = 'lib/net10.0/NAPS2.Sdk.dll'
        Sha256 = $sha256
    }
}

function Assert-InstalledNaps2SdkMatchesCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Stage
    )

    $installedSdk = Join-Path $InstallDirectory "NAPS2.Sdk.dll"
    if (-not (Test-Path -LiteralPath $installedSdk -PathType Leaf)) {
        throw "Installed NAPS2.Sdk.dll отсутствует после $Stage: $installedSdk"
    }

    $actualSha256 = (Get-FileHash -LiteralPath $installedSdk -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "NAPS2.Sdk.dll byte identity mismatch после $Stage. expected=$ExpectedSha256 actual=$actualSha256 path=$installedSdk"
    }

    Write-Host "windows_naps2_runtime_integrity=PASS stage=$Stage sha256=$actualSha256"
}
