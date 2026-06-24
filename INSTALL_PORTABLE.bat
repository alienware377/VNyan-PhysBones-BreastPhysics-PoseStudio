@echo off
setlocal enableextensions
REM ===========================================================================
REM  Portable installer - NO Unity, NO compiler, NO build required.
REM
REM  The .dll and .vnobj files in dist\ are ALREADY built and load on VNyan's
REM  Unity 2022.3 runtime, so nothing has to be compiled on this PC. This
REM  installer simply lets you browse to your VNyan folder and copies them in.
REM
REM  (The Unity Editor itself can't be bundled - it's several gigabytes and its
REM   license forbids redistribution - but it isn't needed here: the build
REM   output it would produce is already included in dist\. Only use
REM   INSTALL_BUILD.bat if these prebuilt bundles ever refuse to load.)
REM ===========================================================================

REM --- self-elevate to Administrator (needed to write into Program Files) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "SRC=%~dp0dist"
if not exist "%SRC%\JayoPoseStudio.vnobj" (
    echo.
    echo *** Could not find the dist\ folder next to this file. ***
    echo     Keep INSTALL_PORTABLE.bat in the same folder as dist\.
    pause
    exit /b 1
)

REM --- remove "Mark of the Web" from the source files so .NET will load them.
REM     (When this folder is zipped / downloaded / copied from another PC,
REM      Windows tags the .dll and .vnobj as blocked, and VNyan silently
REM      refuses to load blocked assemblies - which leaves the Plugins panel
REM      empty. Unblocking here AND at the destination prevents that.)
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

echo.
echo ============================================================
echo   JayoPhysBones + Jiggle Physics + Pose Studio
echo   Portable installer (no Unity needed)
echo ============================================================
echo.
echo Select your VNyan install folder (the folder that contains VNyan.exe)...

REM --- folder picker via PowerShell; result written to a temp file ---
set "SELFILE=%TEMP%\_vnyan_pick.txt"
del "%SELFILE%" >nul 2>&1
powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $f=New-Object System.Windows.Forms.FolderBrowserDialog; $f.Description='Select your VNyan install folder (the folder containing VNyan.exe)'; $f.ShowNewFolderButton=$false; if(Test-Path 'C:\Program Files\VNyan'){ $f.SelectedPath='C:\Program Files\VNyan' }; if($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){ Set-Content -LiteralPath '%SELFILE%' -Value $f.SelectedPath -Encoding Default -NoNewline }"

if not exist "%SELFILE%" (
    echo No folder selected. Aborting.
    pause
    exit /b 1
)
set "VNYAN="
set /p VNYAN=<"%SELFILE%"
del "%SELFILE%" >nul 2>&1

if not defined VNYAN (
    echo No folder selected. Aborting.
    pause
    exit /b 1
)
echo   VNyan: "%VNYAN%"

if not exist "%VNYAN%\VNyan_Data\Managed\VNyanInterface.dll" (
    echo.
    echo *** That folder is not a VNyan install ^(no VNyan_Data\Managed\VNyanInterface.dll^). ***
    echo     Pick the folder that contains VNyan.exe.
    pause
    exit /b 1
)

set "DST=%VNYAN%\Items\Assemblies\JayoPhysBones"
set "DST2=%VNYAN%\Items\Assemblies\JigglePhysics"
set "DST3=%VNYAN%\Items\Assemblies\PoseStudio"

echo.
echo Installing VRChat PhysBones...
if not exist "%DST%" mkdir "%DST%"
copy /Y "%SRC%\JayoPhysBones.dll"   "%DST%\"  || goto :fail
copy /Y "%SRC%\JayoPhysBones.vnobj" "%DST%\"  || goto :fail
if not exist "%VNYAN%\physbones.json" copy /Y "%SRC%\physbones.json" "%VNYAN%\" >nul

echo Installing Jiggle Physics...
if not exist "%DST2%" mkdir "%DST2%"
copy /Y "%SRC%\JayoJigglePhysics.dll"   "%DST2%\" || goto :fail
copy /Y "%SRC%\JayoJigglePhysics.vnobj" "%DST2%\" || goto :fail
if not exist "%VNYAN%\jigglephysics.json" copy /Y "%SRC%\jigglephysics.json" "%VNYAN%\" >nul

echo Installing Pose Studio...
if not exist "%DST3%" mkdir "%DST3%"
copy /Y "%SRC%\JayoPoseStudio.dll"   "%DST3%\" || goto :fail
copy /Y "%SRC%\JayoPoseStudio.vnobj" "%DST3%\" || goto :fail
if not exist "%VNYAN%\posestudio.json" copy /Y "%SRC%\posestudio.json" "%VNYAN%\" >nul

REM --- unblock again at the destination, in case copy carried the MotW tag ---
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%VNYAN%\Items\Assemblies' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

echo.
echo Done. Installed into:
echo   %DST%
echo   %DST2%
echo   %DST3%
echo Starter .json files were placed in %VNYAN% (existing configs were kept).
echo.
echo Now FULLY CLOSE VNyan (not just the window) and start it again,
echo then open the Plugins window and click
echo "VRChat PhysBones", "Jiggle Physics", and/or "Pose Studio".
echo.
echo If the Plugins panel is still empty after restarting:
echo   1. VNyan ^> Settings ^> Misc ^> "Allow Third Party Plugins" must be ON.
echo      VNyan logs nothing when this is off - the panel is silently empty.
echo      Check this first.
echo   2. Make sure you picked the folder that actually contains VNyan.exe.
echo      If you have multiple VNyan installs, launch the same one you installed into.
echo   3. The files were unblocked automatically, but if VNyan was open
echo      during install, close it completely (check the system tray) and reopen it.
echo   4. If they still don't appear, run TROUBLESHOOT.bat for a full diagnostic,
echo      or run INSTALL_BUILD.bat to rebuild against your own VNyan/Unity.
pause
exit /b

:fail
echo.
echo *** Copy failed. Check that you picked the right VNyan folder and try again. ***
pause
exit /b 1
