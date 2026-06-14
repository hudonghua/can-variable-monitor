@echo off
cd /d "%~dp0"
set "APP_X86=.\CanVariableMonitor\bin\Release\net9.0-windows\win-x86\publish\CanVariableMonitor.exe"
set "APP_X64=.\CanVariableMonitor\bin\Release\net9.0-windows\win-x64\publish\CanVariableMonitor.exe"

if exist "%APP_X86%" (
    start "" "%APP_X86%"
    exit /b
)

if exist "%APP_X64%" (
    start "" "%APP_X64%"
    exit /b
)

echo CanVariableMonitor.exe not found.
pause
