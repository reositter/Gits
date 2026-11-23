@echo off
rem Dubbelklicka för att installera eller uppdatera Inventering. Ber om administratörsbehörighet.
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installera.ps1"
echo.
pause
