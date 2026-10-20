@echo off
rem Double-click to build the latest source, install it, and relaunch Shaker Router.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\update.ps1" %*
