[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$SourceSha,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$SdkVersion,

    [Parameter(Mandatory = $true)]
    [ValidateSet("source-appsettings", "generated-default")]
    [string]$ConfigMode,

    [Parameter(Mandatory = $true)]
    [string]$PackageEntrypoint,

    [Parameter(Mandatory = $true)]
    [ValidateSet("defaults", "override")]
    [string]$MetadataMode,

    [Parameter(Mandatory = $true)]
    [string]$ApplicationName,

    [Parameter(Mandatory = $true)]
    [string]$InstallerBaseName,

    [Parameter(Mandatory = $true)]
    [string]$MetadataInputSha256,

    [Parameter(Mandatory = $true)]
    [string]$EffectiveMetadataSha256
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

foreach ($metadataHash in @($MetadataInputSha256, $EffectiveMetadataSha256)) {
    if ($metadataHash -notmatch '^[0-9a-f]{64}$') {
        throw "Некорректный product metadata SHA-256: $metadataHash"
    }
}

$resolvedArtifact = [IO.Path]::GetFullPath($ArtifactPath)
if (-not (Test-Path -LiteralPath $resolvedArtifact -PathType Leaf)) {
    throw "Artifact does not exist: $resolvedArtifact"
}

$artifactInfo = Get-Item -LiteralPath $resolvedArtifact
$sha256 = Get-Sha256Hex -Path $resolvedArtifact
$artifact = $artifactInfo.Name
$size = $artifactInfo.Length

$shaPath = "$resolvedArtifact.sha256"
$provenancePath = "$resolvedArtifact.provenance.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

[IO.File]::WriteAllText(
    $shaPath,
    "$sha256  $artifact`n",
    $utf8NoBom)

$provenance = [ordered]@{
    artifact = $artifact
    version = $Version
    sourceSha = $SourceSha
    rid = $Rid
    sha256 = $sha256
    size = $size
    sdkVersion = $SdkVersion
    configMode = $ConfigMode
    metadataMode = $MetadataMode
    applicationName = $ApplicationName
    installerBaseName = $InstallerBaseName
    metadataInputSha256 = $MetadataInputSha256
    effectiveMetadataSha256 = $EffectiveMetadataSha256
    packageEntrypoint = $PackageEntrypoint
}

$json = $provenance | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText(
    $provenancePath,
    "$json`n",
    $utf8NoBom)

Write-Output $provenancePath
