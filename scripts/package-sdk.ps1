param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot "src\NinePointRotationCalibration.Sdk\bin\$Configuration"
$packageDirectory = Join-Path $repositoryRoot "artifacts\sdk"
if (-not (Test-Path -LiteralPath $sourceDirectory)) {
    throw "SDK output was not found. Run scripts\build-release.ps1 first."
}

New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
$fileNames = @(
    "NinePointRotationCalibration.Sdk.dll",
    "NinePointRotationCalibration.Sdk.xml",
    "NinePointRotationCalibration.Core.dll",
    "NinePointRotationCalibration.Core.xml",
    "NinePointRotationCalibration.Halcon.dll",
    "NinePointRotationCalibration.WinForms.dll",
    "halcondotnet.dll"
)
foreach ($fileName in $fileNames) {
    $sourcePath = Join-Path $sourceDirectory $fileName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required SDK package file was not found: $sourcePath"
    }
    Copy-Item -LiteralPath $sourcePath -Destination $packageDirectory -Force
}
$integrationGuide = Join-Path $repositoryRoot "docs\HostIntegration.md"
if (-not (Test-Path -LiteralPath $integrationGuide -PathType Leaf)) {
    throw "Required SDK integration guide was not found: $integrationGuide"
}
Copy-Item -LiteralPath $integrationGuide -Destination $packageDirectory -Force

Write-Host "SDK package: $packageDirectory"
