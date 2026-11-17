param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\NinePointRotationCalibration-Station-$stamp"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
if (-not $OutputDirectory.StartsWith($artifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside the repository artifacts directory."
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}

& (Join-Path $PSScriptRoot "build-release.ps1") -Configuration $Configuration -Platform $Platform
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

$sourceBundle = Join-Path $OutputDirectory "Source"
$runtimeBundle = Join-Path $OutputDirectory "Run"
New-Item -ItemType Directory -Force -Path $sourceBundle, $runtimeBundle | Out-Null

$robocopyArguments = @(
    $repositoryRoot, $sourceBundle, "/E", "/R:2", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP",
    "/XD", ".git", ".vs", "artifacts", "bin", "obj", "TestResults",
    "/XF", "*.user", "*.suo", "*.cache"
)
& robocopy @robocopyArguments
if ($LASTEXITCODE -ge 8) { throw "Source copy failed with robocopy exit code $LASTEXITCODE." }

$stationOutput = Join-Path $repositoryRoot "src\NinePointRotationCalibration.Station\bin\$Configuration"
if (-not (Test-Path -LiteralPath (Join-Path $stationOutput "NinePointRotationCalibration.Station.exe"))) {
    throw "Station executable was not produced: $stationOutput"
}
Copy-Item -Path (Join-Path $stationOutput "*") -Destination $runtimeBundle -Recurse -Force
$runtimeDataDir = Join-Path $runtimeBundle "data"
if (Test-Path -LiteralPath $runtimeDataDir) {
    Get-ChildItem -LiteralPath $runtimeDataDir -Filter "*.tmp" -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $stationJson = Join-Path $runtimeDataDir "station.json"
    if (Test-Path -LiteralPath $stationJson) {
        Remove-Item -LiteralPath $stationJson -Force
    }
}

$guide = @'
九点标定视觉工作站交付说明

源码：打开 Source\NinePointRotationCalibration.sln，选择 x64 配置。建议 Visual Studio 2019/2022，并安装 .NET 桌面开发与 .NET Framework 4.7.2 Developer Pack。
运行：先运行 Run\NinePointRotationCalibration.Station.exe。首次运行会在程序目录生成 data 配置。
日志：%LOCALAPPDATA%\NinePointRotationCalibration\logs\station.log，日志按 10 MB 滚动并保留 5 份。
相机：相机页选择驱动、枚举设备、选中序列号后连接；切换设备时再次选择并连接，预览会随新设备恢复。大华/海康 native SDK 和设备驱动必须与目标电脑、相机型号和 x64 环境匹配。
HALCON：包内包含用于编译的 halcondotnet.dll。运行模板匹配/标定仍需在目标电脑安装与程序集版本完全一致的 64 位 HALCON Runtime，并配置有效许可。未将开发机 HALCON Runtime 或许可打入交付包。
注意：本包不携带 Visual Studio、.NET Framework、HALCON Runtime 许可或相机厂商安装程序；这些组件需在目标机按其授权和驱动安装要求部署。
'@
Set-Content -LiteralPath (Join-Path $OutputDirectory "交付说明.txt") -Value $guide -Encoding UTF8

$zipPath = $OutputDirectory + ".zip"
Compress-Archive -Path (Join-Path $OutputDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Source and runtime package: $OutputDirectory"
Write-Host "ZIP package: $zipPath"
