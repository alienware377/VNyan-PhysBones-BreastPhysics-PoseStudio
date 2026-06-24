@echo off
setlocal
REM ===========================================================================
REM  Build-from-source installer for other PCs.
REM
REM  Unlike INSTALL.bat (which copies the prebuilt files from dist\), this one:
REM    * lets YOU pick your own VNyan install folder with a file browser,
REM    * rebuilds the plugin DLLs against YOUR VNyan's assemblies,
REM    * rebuilds the .vnobj bundles with YOUR own Unity installation,
REM    * then installs everything into the VNyan folder you chose.
REM
REM  Requirements on this PC:
REM    * The full source tree (this folder, with Assets\, _unitybuild\, dist\).
REM    * Unity 2022.3.x installed (via Unity Hub is fine).
REM    * .NET Framework 4.x (already on every modern Windows).
REM
REM  Just double-click this file. The real work is in Install-FromSource.ps1.
REM ===========================================================================

REM --- self-elevate to Administrator (needed to write into Program Files) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "PS=%~dp0Install-FromSource.ps1"
if not exist "%PS%" (
    echo.
    echo *** Could not find Install-FromSource.ps1 next to this file. ***
    echo     Make sure you kept the whole folder together.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%PS%"
echo.
pause
exit /b
