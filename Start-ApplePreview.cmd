@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo This preview needs the .NET 10 SDK.
    pause
    exit /b 1
)
dotnet run --project "Tests\Luma.SmokeTests.csproj" -c Release -- --preview
if errorlevel 1 pause
