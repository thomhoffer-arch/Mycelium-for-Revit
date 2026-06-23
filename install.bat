@echo off
echo.
echo  Mycelium Studio Revit Connector -- installer
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/thomhoffer-arch/Mycelium-for-Revit/main/install.ps1 | iex"
if %errorlevel% neq 0 (
    echo.
    echo  Installation failed. See error above.
    pause
)
