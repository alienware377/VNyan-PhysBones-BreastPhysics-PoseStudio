@echo off
setlocal
REM Self-elevate to Administrator if not already.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "SRC=%~dp0dist"
set "DST=C:\Program Files\VNyan\Items\Assemblies\JayoPhysBones"
set "DST2=C:\Program Files\VNyan\Items\Assemblies\JigglePhysics"
set "DST3=C:\Program Files\VNyan\Items\Assemblies\PoseStudio"

echo Installing JayoPhysBones into VNyan...
if not exist "%DST%" mkdir "%DST%"
copy /Y "%SRC%\JayoPhysBones.dll"   "%DST%\"            || goto :fail
copy /Y "%SRC%\JayoPhysBones.vnobj" "%DST%\"            || goto :fail
copy /Y "%SRC%\physbones.json"      "C:\Program Files\VNyan\" || goto :fail

echo Installing Jiggle Physics into VNyan...
if not exist "%DST2%" mkdir "%DST2%"
copy /Y "%SRC%\JayoJigglePhysics.dll"   "%DST2%\"      || goto :fail
copy /Y "%SRC%\JayoJigglePhysics.vnobj" "%DST2%\"      || goto :fail
copy /Y "%SRC%\jigglephysics.json"      "C:\Program Files\VNyan\" || goto :fail

echo Installing Pose Studio into VNyan...
if not exist "%DST3%" mkdir "%DST3%"
copy /Y "%SRC%\JayoPoseStudio.dll"   "%DST3%\"         || goto :fail
copy /Y "%SRC%\JayoPoseStudio.vnobj" "%DST3%\"         || goto :fail
copy /Y "%SRC%\posestudio.json"      "C:\Program Files\VNyan\" || goto :fail

echo.
echo Done. Files installed to:
echo   %DST%
echo   %DST2%
echo   %DST3%
echo   C:\Program Files\VNyan\physbones.json
echo   C:\Program Files\VNyan\jigglephysics.json
echo   C:\Program Files\VNyan\posestudio.json
echo.
echo Now (re)start VNyan, open the Plugins window, and click "VRChat PhysBones",
echo "Jiggle Physics", and/or "Pose Studio".
pause
exit /b

:fail
echo.
echo *** Copy failed. Make sure VNyan is at C:\Program Files\VNyan and try again. ***
pause
exit /b 1
