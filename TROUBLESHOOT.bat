@echo off
setlocal
REM ===========================================================================
REM  Read-only diagnostic. Changes NOTHING. Produces a report file to send back.
REM
REM  Double-click this. Pick your VNyan folder when asked. When it finishes,
REM  a file called VNyan_Diagnostics_Report.txt appears next to this .bat -
REM  send that file back.
REM ===========================================================================

set "PS=%~dp0Troubleshoot-VNyan.ps1"
if not exist "%PS%" (
    echo.
    echo *** Could not find Troubleshoot-VNyan.ps1 next to this file. ***
    echo     Keep the whole folder together.
    echo.
    pause
    exit /b 1
)

REM Unblock the script itself so PowerShell will run it after a download/copy.
powershell -NoProfile -Command "Unblock-File -LiteralPath '%PS%' -ErrorAction SilentlyContinue" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%PS%"
echo.
echo If a report opened, great. The file VNyan_Diagnostics_Report.txt is in this
echo folder - please send it back.
echo.
pause
exit /b
