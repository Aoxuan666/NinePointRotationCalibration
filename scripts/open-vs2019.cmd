@echo off
setlocal
set "SOLUTION=%~dp0..\NinePointRotationCalibration.sln"

set "DEVENV="
for %%P in (
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\IDE\devenv.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.exe"
) do if not defined DEVENV if exist %%~P set "DEVENV=%%~P"

if not defined DEVENV (
  echo 未找到 Visual Studio 2019，请先安装 VS2019 及 .NET Framework 4.7.2 targeting pack。
  pause
  exit /b 1
)

start "" "%DEVENV%" "%SOLUTION%"
exit /b 0
