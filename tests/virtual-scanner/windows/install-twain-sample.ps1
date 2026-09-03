$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Join-Path $env:RUNNER_TEMP 'webassistant-twain-sample'
$archive = Join-Path $root 'twain-sample.zip'
$expanded = Join-Path $root 'twain-sample'
$sourceUrl = 'https://github.com/twain/twain-samples/releases/download/v2.5.0/Twain_sample01_02050000.zip'

if (Test-Path $root) {
    Remove-Item $root -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $root | Out-Null

Invoke-WebRequest -Uri $sourceUrl -OutFile $archive
Expand-Archive -Path $archive -DestinationPath $expanded -Force

$msiFiles = @(Get-ChildItem -Path $expanded -Recurse -File -Filter '*.msi')
if ($msiFiles.Count -eq 0) {
    throw 'В официальном TWAIN sample archive не найден MSI.'
}

foreach ($msi in $msiFiles) {
    Write-Host "Установка $($msi.Name)"
    $process = Start-Process `
        -FilePath 'msiexec.exe' `
        -ArgumentList @('/i', "`"$($msi.FullName)`"", '/qn', '/norestart') `
        -Wait `
        -PassThru

    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Установка $($msi.Name) завершилась с кодом $($process.ExitCode)."
    }
}

Write-Host 'twain_sample=v2.5.0'
Write-Host "installed_msi_count=$($msiFiles.Count)"
Write-Host 'result=PASS'
