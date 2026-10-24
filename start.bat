@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
title Undertone - player for Yandex Music (.NET)
color 0B

REM ============================================================
REM   Undertone Launcher (.NET)
REM   Положи этот .bat рядом с папкой проекта (где .csproj)
REM   или укажи путь в PROJECT_DIR ниже.
REM ============================================================

set "PROJECT_DIR=Undertone"
set "CSPROJ=%PROJECT_DIR%\Undertone.csproj"
set "OUT_EXE=%PROJECT_DIR%\bin\Release\net10.0-windows10.0.19041.0\Undertone.exe"

cls
echo.
echo   _   _ _  _ ___  ___ ___ _____ ___  _  _ ___ 
echo  ^| ^| ^| ^| \^| ^|   \^| __^| _ \_   _/ _ \^| \^| ^| __^|
echo  ^| ^|_^| ^| .` ^| ^|) ^| _^|^|   / ^| ^|^| (_) ^| .` ^| _^| 
echo   \___/^|_^|\_^|___/^|___^|_^|_\ ^|_^| \___/^|_^|\_^|___^|
echo.
echo        ~ player for Yandex Music ~
echo        by Reuz  (.NET)
echo.
echo   --------------------------------------------------------
echo.

REM ── 1. Проверка .NET SDK ────────────────────────────────────
where dotnet >nul 2>nul
if errorlevel 1 (
    echo   [!] .NET SDK не найден в PATH.
    echo.
    echo   Установи .NET 10 SDK с https://dotnet.microsoft.com/download
    echo   После установки перезапусти этот файл.
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version 2^>nul') do set "DOTNETVER=%%v"
echo   [OK] .NET SDK !DOTNETVER!

REM ── 2. Проверка проекта ─────────────────────────────────────
if not exist "%CSPROJ%" (
    echo   [!] Не найден %CSPROJ%
    echo.
    echo   Положи start.bat рядом с папкой проекта или поправь PROJECT_DIR.
    echo.
    pause
    exit /b 1
)
echo   [OK] Найден %CSPROJ%

REM ── 3. Проверка .env / TOKEN ────────────────────────────────
set "HASTOKEN="
if defined TOKEN set "HASTOKEN=1"
if exist ".env" (
    findstr /i /c:"access_token" ".env" >nul 2>nul
    if not errorlevel 1 set "HASTOKEN=1"
)
if exist "%PROJECT_DIR%\.env" (
    findstr /i /c:"access_token" "%PROJECT_DIR%\.env" >nul 2>nul
    if not errorlevel 1 set "HASTOKEN=1"
)
if defined HASTOKEN (
    echo   [OK] Токен найден
) else (
    echo   [..] Токена нет — при запуске предложу войти: Яндекс Музыка или SoundCloud
    echo        ^(токены сами запишутся в .env в этой папке^)
)

REM ── 4. Сборка ───────────────────────────────────────────────
echo.
echo   [..] Собираю проект (Release)...
dotnet build "%CSPROJ%" -c Release --nologo -v quiet
if errorlevel 1 (
    echo.
    echo   [!] Сборка не удалась. Смотри ошибки выше.
    echo.
    pause
    exit /b 1
)
echo   [OK] Сборка успешна

REM ── 5. Запуск ───────────────────────────────────────────────
if not exist "%OUT_EXE%" (
    echo   [!] Не найден %OUT_EXE% после сборки.
    echo       Проверь TargetFramework в .csproj ^(ожидается net10.0-windows10.0.19041.0^).
    pause
    exit /b 1
)

echo.
echo   --------------------------------------------------------
echo   Запускаю %OUT_EXE% ...
echo   --------------------------------------------------------
echo.

"%OUT_EXE%"
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
    echo   [OK] Программа завершена.
) else (
    echo   [!] Программа завершилась с кодом %EXITCODE%.
)
echo.
echo   Нажми любую клавишу, чтобы закрыть...
pause >nul
endlocal