@echo off
rem 双击运行：安装 AgentBeacon（开机自启 + 立即后台启动）
rem 可带参数，例如：install.bat -Token 你的key -Port 8765
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
