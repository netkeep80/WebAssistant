@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "WEBASSISTANT_DOTNET_EXE="

call :try_dotnet "%ProgramFiles%\dotnet\dotnet.exe"
if defined WEBASSISTANT_DOTNET_EXE goto run_package

for /f "delims=" %%D in ('where dotnet 2^>nul') do (
    if not defined WEBASSISTANT_DOTNET_EXE call :try_dotnet "%%~fD"
)
if defined WEBASSISTANT_DOTNET_EXE goto run_package

echo .NET SDK 10 not found. Installing Microsoft.DotNet.SDK.10...
where winget >nul 2>&1
if errorlevel 1 (
    echo winget is unavailable. Install .NET SDK 10 manually. 1>&2
    exit /b 1
)

winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
if errorlevel 1 exit /b 1

rem Do not rely on the current process PATH being refreshed by winget.
call :try_dotnet "%ProgramFiles%\dotnet\dotnet.exe"
if defined WEBASSISTANT_DOTNET_EXE goto run_package

for /f "delims=" %%D in ('where dotnet 2^>nul') do (
    if not defined WEBASSISTANT_DOTNET_EXE call :try_dotnet "%%~fD"
)

if not defined WEBASSISTANT_DOTNET_EXE (
    echo .NET SDK 10 is still unavailable after installation. 1>&2
    echo Checked "%ProgramFiles%\dotnet\dotnet.exe" and dotnet.exe candidates from the current PATH. 1>&2
    exit /b 1
)

:run_package
echo Using .NET SDK 10 from "%WEBASSISTANT_DOTNET_EXE%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%package.ps1" %*
exit /b %errorlevel%

:try_dotnet
if defined WEBASSISTANT_DOTNET_EXE exit /b 0
if "%~1"=="" exit /b 1
if not exist "%~1" exit /b 1

"%~1" --list-sdks 2>nul | findstr /l /b /c:"10." >nul
if errorlevel 1 exit /b 1

set "WEBASSISTANT_DOTNET_EXE=%~1"
exit /b 0
