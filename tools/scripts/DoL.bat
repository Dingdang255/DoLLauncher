@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem 无参数时优先启动图形界面；带参数（-List / -Stop / 版本名）时走命令行脚本
if "%~1"=="" (
    if exist "DoLLauncher.exe" (
        start "" "DoLLauncher.exe"
        exit /b 0
    )
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1" %*
if errorlevel 1 pause
if "%~1"=="" pause
