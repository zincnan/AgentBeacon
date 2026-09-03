@echo off
rem 双击运行：卸载 AgentBeacon（停止 + 取消自启；不删除本文件夹）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
