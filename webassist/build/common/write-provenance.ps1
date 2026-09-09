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
    [string]$PackageEntrypoint
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
    packageEntrypoint = $PackageEntrypoint
}

$json = $provenance | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText(
    $provenancePath,
    "$json`n",
    $utf8NoBom)

Write-Output $provenancePath
