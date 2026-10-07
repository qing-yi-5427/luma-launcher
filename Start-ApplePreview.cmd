@echo off
setlocal
title Luma Apple Preview
cd /d "%~dp0"
echo Luma Apple Preview uses temporary settings and connects to an existing Everything instance.
echo Esc hides it; the active hotkey or tray icon restores it. Use the tray menu Exit to quit.
echo If Alt+Space is already owned by another app, the preview will show its fallback hotkey.
set "PREVIEW_EXE=%~dp0Tests\bin\Release\net10.0-windows10.0.19041.0\Luma.SmokeTests.exe"
if not exist "%PREVIEW_EXE%" (
    where dotnet >nul 2>nul
    if errorlevel 1 (
        echo This preview needs the .NET 10 SDK for its first build.
        pause
        exit /b 1
    )
    dotnet build "Tests\Luma.SmokeTests.csproj" -c Release
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
if not exist "%PREVIEW_EXE%" (
    echo Preview build completed without an executable.
    pause
    exit /b 1
)
"%PREVIEW_EXE%" --preview
if errorlevel 1 pause
