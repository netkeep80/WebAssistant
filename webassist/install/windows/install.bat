@echo off
setlocal EnableExtensions

set "PACKAGE_ROOT=%~dp0"
if not exist "%PACKAGE_ROOT%app\WebAssistant.exe" (
    for %%I in ("%~dp0..\..\artifacts\windows-x64") do set "PACKAGE_ROOT=%%~fI\"
)

if not exist "%PACKAGE_ROOT%app\WebAssistant.exe" (
    echo ERROR: WebAssistant Windows package is not built.
    echo Expected: "%PACKAGE_ROOT%app\WebAssistant.exe"
    echo Run webassist\build\windows\package.bat first.
    endlocal & exit /b 1
)

set "SCRIPT=%PACKAGE_ROOT%install.ps1"
if not exist "%SCRIPT%" (
    echo ERROR: install.ps1 is missing from Windows package.
    endlocal & exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
