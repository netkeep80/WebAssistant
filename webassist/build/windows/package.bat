@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"

call :has_dotnet_10
if not errorlevel 1 goto run_package

echo .NET SDK 10 not found. Installing Microsoft.DotNet.SDK.10...
where winget >nul 2>&1
if errorlevel 1 (
    echo winget is unavailable. Install .NET SDK 10 manually. 1>&2
    exit /b 1
)

winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
if errorlevel 1 exit /b 1

call :has_dotnet_10
if errorlevel 1 (
    echo .NET SDK 10 is still unavailable after installation. 1>&2
    exit /b 1
)

:run_package
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%package.ps1" %*
exit /b %errorlevel%

:has_dotnet_10
where dotnet >nul 2>&1 || exit /b 1
for /f "tokens=1" %%V in ('dotnet --list-sdks 2^>nul') do (
    echo %%V | findstr /b /c:"10." >nul && exit /b 0
)
exit /b 1
