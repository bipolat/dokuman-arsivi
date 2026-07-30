@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
title Dokuman Arsivi - Kurulum ve Baslatma

rem Bu dosya bulundugu klasoru proje koku kabul eder. Sabit yol yoktur.
cd /d "%~dp0"

echo.
echo ==========================================================
echo   DOKUMAN ARSIVI - KURULUM
echo   Proje koku: %CD%
echo ==========================================================
echo.
echo [1/3] Gereksinimler kontrol ediliyor...
echo.

set "INSTALLED="
set "MISSING="

rem winget yoksa otomatik kurulum yapilamaz, sadece rapor edilir.
where /q winget
if errorlevel 1 (
    set "NOWINGET=1"
    echo   UYARI: winget bulunamadi. Eksik araclar otomatik kurulamayacak.
    echo          Microsoft Store'dan "App Installer" kurmaniz gerekir.
    echo.
)

call :ensure dotnet "Microsoft.DotNet.SDK.10" ".NET SDK"
call :ensure node   "OpenJS.NodeJS.LTS"       "Node.js"
call :ensure git    "Git.Git"                 "Git"

rem Kurulum yapildiysa PATH'i bu oturuma yeniden yukle,
rem yoksa yeni kurulan araclar "bulunamadi" gorunur.
if defined INSTALLED (
    echo.
    echo   PATH yenileniyor...
    for /f "delims=" %%p in ('powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')"') do set "PATH=%%p"
)

echo.
echo [2/3] Dogrulama...
echo.

set "FAIL="
call :verify dotnet ".NET SDK"
call :verify node   "Node.js"
call :verify npm    "npm"
call :verify git    "Git"

if defined FAIL (
    echo.
    echo ==========================================================
    echo   EKSIK ARAC VAR - devam edilemiyor
    echo ==========================================================
    echo   Eksik: !FAIL!
    echo.
    if defined INSTALLED (
        echo   Kurulum yapildi ama PATH henuz gecerli olmamis olabilir.
        echo   BU PENCEREYI KAPATIP kurulum.bat dosyasini tekrar calistirin.
    ) else (
        echo   Eksik araclari kurup tekrar deneyin:
        echo     .NET SDK : https://dotnet.microsoft.com/download
        echo     Node.js  : https://nodejs.org
        echo     Git      : https://git-scm.com/download/win
    )
    echo.
    pause
    exit /b 1
)

echo.
echo [3/3] Klasorler, repo ve sunucular...
echo.

set "URLFILE=%TEMP%\dokuman-arsivi-url.txt"
if exist "%URLFILE%" del "%URLFILE%" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kurulum.ps1"
set "PSEXIT=%errorlevel%"

echo.
if not "%PSEXIT%"=="0" (
    echo Kurulum betigi hata ile bitti. Yukaridaki mesajlara bakin.
    echo.
    pause
    exit /b %PSEXIT%
)

rem Adresi ps1 yaziyor: port tespiti orada yapiliyor, burada tahmin edilmiyor.
if exist "%URLFILE%" (
    set /p APPURL=<"%URLFILE%"
    del "%URLFILE%" >nul 2>&1
    echo Tarayici aciliyor: !APPURL!
    start "" "!APPURL!"
) else (
    echo Arayuz portu tespit edilemedi, tarayici acilmadi.
    echo Frontend penceresindeki adrese bakin.
)

echo.
echo Islem tamamlandi.
echo.
pause
exit /b 0


rem ---------------------------------------------------------------
rem  :ensure  komut  wingetId  gorunenAd
rem  Arac varsa surumunu yazar, yoksa winget ile kurar.
rem ---------------------------------------------------------------
:ensure
where /q %~1
if not errorlevel 1 (
    for /f "delims=" %%v in ('%~1 --version 2^>nul') do (
        echo   [VAR]  %~3 - %%v
        goto :eof
    )
    echo   [VAR]  %~3
    goto :eof
)

if defined NOWINGET (
    echo   [YOK]  %~3 - winget olmadigi icin kurulamiyor
    goto :eof
)

echo   [YOK]  %~3 kuruluyor... ^(%~2^)
echo          UAC izin penceresi cikabilir, onaylayin.
winget install --id %~2 --exact --source winget --accept-package-agreements --accept-source-agreements --silent
if errorlevel 1 (
    echo   HATA: %~3 kurulumu basarisiz oldu.
) else (
    echo   [OK]   %~3 kuruldu
    set "INSTALLED=1"
)
goto :eof


rem ---------------------------------------------------------------
rem  :verify  komut  gorunenAd
rem ---------------------------------------------------------------
:verify
where /q %~1
if errorlevel 1 (
    echo   [EKSIK] %~2
    set "FAIL=!FAIL! %~2"
) else (
    for /f "delims=" %%v in ('%~1 --version 2^>nul') do (
        echo   [OK]    %~2 - %%v
        goto :eof
    )
    echo   [OK]    %~2
)
goto :eof
