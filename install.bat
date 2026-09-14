@echo off
rem VNyan Physics Plugins installer launcher
rem Looks for your VNyan folder (or asks you to open it); asks for admin rights ONLY if that folder needs them.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0install.ps1"
