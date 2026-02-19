@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  VoidBound Release Publisher
::  Empaqueta el cliente y publica un GitHub Release
::  para que el Launcher descargue las actualizaciones.
::
::  REQUISITOS:
::    - GitHub CLI (gh): https://cli.github.com/
::    - Logueado con: gh auth login
::    - El repo GITHUB_REPO debe ser TUYO (no MUnique/OpenMU)
::
::  USO:
::    release_client.bat 0.99b "Descripcion del cambio"
:: ============================================================

:: ── Configuracion ─────────────────────────────────────────────
set "GITHUB_REPO=matthew7990/VoidboundMU"
:: ^^^^^^^^^^^^^^ CAMBIA ESTO al usuario/repo de tu GitHub propio

set "ROOT=%~dp0"
set "CLIENT_BIN=%ROOT%clients\MuMain\src\bin"
set "LAUNCHER_BIN=%ROOT%clients\Launcher\bin\Release\net10.0-windows"
set "OUTPUT_DIR=%ROOT%dist_release"
set "GAME_DIR=%OUTPUT_DIR%\MuVoid"
set "SERVER_IP=181.97.243.64"
set "SERVER_PORT=44406"

:: ── Argumentos ────────────────────────────────────────────────
set "VERSION=%~1"
set "CHANGELOG=%~2"

if "%VERSION%"=="" (
    echo.
    echo [ERROR] Debes indicar la version.
    echo.
    echo   Uso: release_client.bat 0.99b "Descripcion del cambio"
    echo.
    pause
    exit /b 1
)
if "%CHANGELOG%"=="" set "CHANGELOG=New version %VERSION%"

echo.
echo ============================================================
echo   VoidBound Release Publisher  ^|  v%VERSION%
echo ============================================================
echo   Repo  : %GITHUB_REPO%
echo   Tag   : v%VERSION%
echo   Notes : %CHANGELOG%
echo ============================================================
echo.

:: ── Verificar gh CLI ──────────────────────────────────────────
where gh >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] GitHub CLI ^(gh^) no encontrado.
    echo         Instala desde: https://cli.github.com/
    echo         Luego corre:   gh auth login
    pause
    exit /b 1
)

gh auth status >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] No estas autenticado con GitHub CLI.
    echo         Corre: gh auth login
    pause
    exit /b 1
)

:: ── Verificar ejecutables del cliente ─────────────────────────
echo [1/6] Verificando ejecutables del cliente...
if not exist "%CLIENT_BIN%\Main.exe" (
    echo [ERROR] Main.exe no encontrado en:
    echo         %CLIENT_BIN%\Main.exe
    echo.
    echo Asegurate de que compile_mumain.bat haya terminado exitosamente.
    pause
    exit /b 1
)

:: ── Compilar Launcher si es necesario ─────────────────────────
echo [2/6] Compilando Launcher...
cd /d "%ROOT%clients\Launcher"
dotnet build -c Release >nul 2>&1
if not exist "%LAUNCHER_BIN%\MuVoidLauncher.exe" (
    echo [ERROR] No se pudo compilar el Launcher.
    pause
    exit /b 1
)
cd /d "%ROOT%"

:: ── Armar estructura de archivos de release ───────────────────
echo [3/6] Preparando archivos...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%GAME_DIR%"
mkdir "%GAME_DIR%\Data"

:: Ejecutables del juego
copy /y "%CLIENT_BIN%\Main.exe"                   "%GAME_DIR%\Main.exe"              >nul
copy /y "%CLIENT_BIN%\MUnique.Client.Library.dll" "%GAME_DIR%\"                      >nul
copy /y "%CLIENT_BIN%\glew32.dll"                 "%GAME_DIR%\"                      >nul
copy /y "%CLIENT_BIN%\ogg.dll"                    "%GAME_DIR%\"                      >nul
copy /y "%CLIENT_BIN%\vorbisfile.dll"             "%GAME_DIR%\"                      >nul
copy /y "%CLIENT_BIN%\wzAudio.dll"                "%GAME_DIR%\"                      >nul

:: Assets del juego
xcopy /e /i /q /y "%CLIENT_BIN%\Data"         "%GAME_DIR%\Data"         >nul
xcopy /e /i /q /y "%CLIENT_BIN%\Translations" "%GAME_DIR%\Translations" >nul

:: config.ini (el launcher NO sobreescribe este — se genera una vez para nuevas instalaciones)
(
    echo [LOGIN]
    echo Version=1.03.34
    echo TestVersion=1.03.34
    echo RememberMe=0
    echo Language=Eng
    echo EncryptedUsername=
    echo EncryptedPassword=
    echo [PARTITION]
    echo Version=357
    echo [Window]
    echo Width=1024
    echo Height=768
    echo Windowed=1
    echo [Graphics]
    echo ColorDepth=0
    echo RenderTextType=0
    echo [Audio]
    echo SoundEnabled=0
    echo MusicEnabled=0
    echo VolumeLevel=0
    echo [CONNECTION SETTINGS]
    echo ServerIP=%SERVER_IP%
    echo ServerPort=%SERVER_PORT%
) > "%GAME_DIR%\config.ini"

:: Launcher
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.exe"            "%OUTPUT_DIR%\MuVoidLauncher.exe"            >nul
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.dll"            "%OUTPUT_DIR%\MuVoidLauncher.dll"            >nul
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.runtimeconfig.json" "%OUTPUT_DIR%\"                          >nul

:: ── Generar version.json con SHA256 de cada archivo ──────────
echo [4/6] Calculando hashes y generando version.json...

set "MANIFEST=%OUTPUT_DIR%\version.json"
set "TAG=v%VERSION%"
set "BASE_URL=https://github.com/%GITHUB_REPO%/releases/download/%TAG%"

:: Recolectar todos los archivos del release en un array
:: Los paths son relativos a OUTPUT_DIR (que es lo que va junto al launcher)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$root = '%OUTPUT_DIR%'; ^
$baseUrl = '%BASE_URL%'; ^
$version = '%VERSION%'; ^
$changelog = '%CHANGELOG%'; ^
$serverIp = '%SERVER_IP%'; ^
$serverPort = '%SERVER_PORT%'; ^
$files = @(); ^
Get-ChildItem -Recurse -File $root | Where-Object { $_.Name -ne 'version.json' } | ForEach-Object { ^
    $rel = $_.FullName.Substring($root.Length + 1).Replace('/', '\'); ^
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; ^
    $size = $_.Length; ^
    $urlName = $rel.Replace('\', '/'); ^
    $files += [PSCustomObject]@{ path=$rel; sha256=$hash; size=$size; url=\"$baseUrl/$urlName\" } ^
}; ^
$manifest = [PSCustomObject]@{ version=$version; changelog=$changelog; serverIp=$serverIp; serverPort=$serverPort; files=$files }; ^
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 '%MANIFEST%'; ^
Write-Host \"version.json generado con\" $files.Count \"archivos. IP=$serverIp Port=$serverPort\""

if not exist "%MANIFEST%" (
    echo [ERROR] No se pudo generar version.json
    pause
    exit /b 1
)

:: ── Crear ZIP del cliente completo (para instalacion inicial) ─
echo [5/6] Creando ZIP del cliente completo...
set "ZIP_PATH=%ROOT%VoidboundMU_Client_%VERSION%.zip"
if exist "%ZIP_PATH%" del /f /q "%ZIP_PATH%"

powershell -NoProfile -Command ^
    "Compress-Archive -Path '%OUTPUT_DIR%\*' -DestinationPath '%ZIP_PATH%' -Force"

:: ── Publicar GitHub Release ───────────────────────────────────
echo [6/6] Publicando GitHub Release %TAG%...
echo.

:: Crear el release y subir todos los archivos de la carpeta dist_release
:: version.json va primero (el launcher lo busca por nombre)
gh release create "%TAG%" ^
    --repo "%GITHUB_REPO%" ^
    --title "VoidboundMU %VERSION%" ^
    --notes "%CHANGELOG%" ^
    "%MANIFEST%#version.json" ^
    "%OUTPUT_DIR%\MuVoidLauncher.exe#MuVoidLauncher.exe" ^
    "%OUTPUT_DIR%\MuVoidLauncher.dll#MuVoidLauncher.dll" ^
    "%OUTPUT_DIR%\MuVoidLauncher.runtimeconfig.json#MuVoidLauncher.runtimeconfig.json" ^
    "%ZIP_PATH%#VoidboundMU_Client_%VERSION%.zip"

:: Subir los archivos del juego individualmente (para updates parciales)
echo.
echo Subiendo archivos del juego para actualizaciones parciales...

for %%F in ("%GAME_DIR%\*.exe" "%GAME_DIR%\*.dll" "%GAME_DIR%\*.ini") do (
    echo   Subiendo %%~nxF...
    gh release upload "%TAG%" "%%F#MuVoid/%%~nxF" --repo "%GITHUB_REPO%" --clobber
)

:: Subir Data/ como subcarpetas (assets)
for /r "%GAME_DIR%\Data" %%F in (*) do (
    set "REL=%%F"
    set "REL=!REL:%GAME_DIR%\=!"
    echo   Subiendo !REL!...
    gh release upload "%TAG%" "%%F#MuVoid/!REL!" --repo "%GITHUB_REPO%" --clobber
)

:: Translations
if exist "%GAME_DIR%\Translations" (
    for /r "%GAME_DIR%\Translations" %%F in (*) do (
        set "REL=%%F"
        set "REL=!REL:%GAME_DIR%\=!"
        echo   Subiendo !REL!...
        gh release upload "%TAG%" "%%F#MuVoid/!REL!" --repo "%GITHUB_REPO%" --clobber
    )
)

if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo  [OK] Release publicado exitosamente!
    echo.
    echo  Tag     : %TAG%
    echo  Repo    : https://github.com/%GITHUB_REPO%
    echo  Release : https://github.com/%GITHUB_REPO%/releases/tag/%TAG%
    echo.
    echo  Proximos pasos:
    echo    1. Compartir VoidboundMU_Client_%VERSION%.zip para instalacion inicial
    echo    2. El Launcher descargara actualizaciones automaticamente desde ahora
    echo ============================================================
) else (
    echo [ERROR] Fallo al publicar el Release.
)

echo.
pause
endlocal
