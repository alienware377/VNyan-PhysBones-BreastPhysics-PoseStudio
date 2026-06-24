@echo off
REM Portable installer launcher for the VNyan plugins:
REM   VRChat PhysBones, Jiggle Physics, Pose Studio.
REM Double-click this file. It runs install.ps1 (sitting next to it), which
REM auto-detects VNyan (or lets you browse to it) and copies the plugins in.
REM No Unity or compiler is required - the bundles here are already built.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0install.ps1"
if errorlevel 1 (
    echo.
    echo Installer reported a problem. See the message box for details.
    pause
)
