param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repositoryRoot ("artifacts\CodeUpdate-" + $stamp)
}

& (Join-Path $PSScriptRoot "build-release.ps1") -Configuration $Configuration -Platform $Platform
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

$stationOutput = Join-Path $repositoryRoot "src\NinePointRotationCalibration.Station\bin\$Configuration"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$ownFiles = @(
    "NinePointRotationCalibration.Station.exe",
    "NinePointRotationCalibration.Station.exe.config",
    "NinePointRotationCalibration.Core.dll",
    "NinePointRotationCalibration.Halcon.dll",
    "NinePointRotationCalibration.WinForms.dll",
    "NinePointRotationCalibration.Sdk.dll"
)
foreach ($fileName in $ownFiles) {
    $src = Join-Path $stationOutput $fileName
    if (-not (Test-Path -LiteralPath $src)) {
        throw "Missing expected pure-code output file: $src"
    }
    Copy-Item -LiteralPath $src -Destination $OutputDirectory -Force
}

$zipPath = $OutputDirectory + ".zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Start-Sleep -Milliseconds 300
        Compress-Archive -Path (Join-Path $OutputDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
        break
    }
    catch {
        if ($attempt -eq 5) { throw }
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "Pure-code update directory: $OutputDirectory"
Write-Host "Pure-code update ZIP: $zipPath"
Write-Host "On-site update: extract/overwrite these 6 files into the site's Run\ root folder (do NOT touch libs\ or data\)."
