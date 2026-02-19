@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  VoidBound Release Publisher
::  - Empaqueta los archivos del juego y los sube como assets
::    individuales al GitHub Release (para updates incrementales)
::  - El ZIP de distribucion SOLO contiene el Launcher (~5MB)
::  - El Launcher descarga el juego completo al primer inicio
::
::  REQUISITOS:
::    - GitHub CLI (gh): https://cli.github.com/
::    - Logueado con: gh auth login
::
::  USO:
::    release_client.bat 0.99b "Descripcion del cambio"
:: ============================================================

:: ── Configuracion ─────────────────────────────────────────────
set "GITHUB_REPO=matthew7990/VoidboundMU"
set "SERVER_IP=181.97.243.64"
set "SERVER_PORT=44406"

set "ROOT=%~dp0"
set "CLIENT_BIN=%ROOT%clients\MuMain\src\bin"
set "LAUNCHER_BIN=%ROOT%clients\Launcher\bin\Release\net10.0-windows"
set "STAGE_DIR=%ROOT%dist_release"
set "GAME_DIR=%STAGE_DIR%\MuVoid"

:: ── Argumentos ────────────────────────────────────────────────
set "VERSION=%~1"
set "CHANGELOG=%~2"

if "%VERSION%"=="" (
    echo.
    echo [ERROR] Debes indicar la version.
    echo   Uso: release_client.bat 0.99b "Descripcion del cambio"
    echo.
    pause & exit /b 1
)
if "%CHANGELOG%"=="" set "CHANGELOG=New version %VERSION%"

set "TAG=v%VERSION%"
set "LAUNCHER_ZIP=%ROOT%VoidboundMU_Launcher_%VERSION%.zip"
set "MANIFEST=%STAGE_DIR%\version.json"
set "BASE_URL=https://github.com/%GITHUB_REPO%/releases/download/%TAG%"

echo.
echo ============================================================
echo   VoidBound Release Publisher  ^|  v%VERSION%
echo ============================================================
echo   Repo    : %GITHUB_REPO%
echo   Tag     : %TAG%
echo   Notas   : %CHANGELOG%
echo   Server  : %SERVER_IP%:%SERVER_PORT%
echo ============================================================
echo.

:: ── Verificar gh CLI ──────────────────────────────────────────
where gh >nul 2>&1 || (
    echo [ERROR] GitHub CLI no encontrado. Instala: https://cli.github.com/
    pause & exit /b 1
)
gh auth status >nul 2>&1 || (
    echo [ERROR] No autenticado. Corre: gh auth login
    pause & exit /b 1
)

:: ── [1/5] Verificar Main.exe ──────────────────────────────────
echo [1/5] Verificando ejecutables del cliente...
if not exist "%CLIENT_BIN%\Main.exe" (
    echo [ERROR] Main.exe no encontrado en %CLIENT_BIN%
    echo         Compila primero con compile_mumain.bat
    pause & exit /b 1
)
echo       OK: Main.exe encontrado.

:: ── [2/5] Compilar Launcher ───────────────────────────────────
echo [2/5] Compilando Launcher...
cd /d "%ROOT%clients\Launcher"
dotnet build -c Release >nul 2>&1
if not exist "%LAUNCHER_BIN%\MuVoidLauncher.exe" (
    echo [ERROR] No se pudo compilar el Launcher.
    pause & exit /b 1
)
cd /d "%ROOT%"
echo       OK: MuVoidLauncher.exe compilado.

:: ── [3/5] Staging de archivos ─────────────────────────────────
echo [3/5] Preparando archivos del juego...
if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
mkdir "%GAME_DIR%"

:: Ejecutables y DLLs del juego
copy /y "%CLIENT_BIN%\Main.exe"                   "%GAME_DIR%\" >nul
copy /y "%CLIENT_BIN%\MUnique.Client.Library.dll" "%GAME_DIR%\" >nul
copy /y "%CLIENT_BIN%\glew32.dll"                 "%GAME_DIR%\" >nul
copy /y "%CLIENT_BIN%\ogg.dll"                    "%GAME_DIR%\" >nul
copy /y "%CLIENT_BIN%\vorbisfile.dll"             "%GAME_DIR%\" >nul
copy /y "%CLIENT_BIN%\wzAudio.dll"                "%GAME_DIR%\" >nul

:: Assets (Data + Translations)
xcopy /e /i /q /y "%CLIENT_BIN%\Data"         "%GAME_DIR%\Data"         >nul
xcopy /e /i /q /y "%CLIENT_BIN%\Translations" "%GAME_DIR%\Translations" >nul

:: config.ini — gestionado por PatchConfigIni del launcher
:: NO va en la lista de files del manifest (el launcher lo crea/parchea solo)
echo       OK: %GAME_DIR% armado.

:: ── [4/5] Generar version.json ────────────────────────────────
echo [4/5] Calculando SHA256 y generando version.json...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$gameDir   = '%GAME_DIR%'; ^
$baseUrl    = '%BASE_URL%'; ^
$version    = '%VERSION%'; ^
$changelog  = '%CHANGELOG%'; ^
$serverIp   = '%SERVER_IP%'; ^
$serverPort = '%SERVER_PORT%'; ^
$files = @(); ^
Get-ChildItem -Recurse -File $gameDir | ForEach-Object { ^
    $rel     = 'MuVoid\' + $_.FullName.Substring($gameDir.Length + 1); ^
    $hash    = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; ^
    $size    = $_.Length; ^
    $urlSlug = $rel.Replace('\','/'); ^
    $files  += [PSCustomObject]@{ path=$rel; sha256=$hash; size=$size; url=\"$baseUrl/$urlSlug\" } ^
}; ^
$manifest = [PSCustomObject]@{ ^
    version=$version; changelog=$changelog; ^
    serverIp=$serverIp; serverPort=$serverPort; files=$files ^
}; ^
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 '%MANIFEST%'; ^
Write-Host ('       OK: version.json con ' + $files.Count + ' archivos. IP=' + $serverIp)"

if not exist "%MANIFEST%" (
    echo [ERROR] Fallo al generar version.json.
    pause & exit /b 1
)

:: ── [5/5] ZIP del Launcher (solo, sin juego) ──────────────────
echo [5/5] Creando ZIP del Launcher para distribucion inicial...

:: Carpeta temporal solo con el launcher
set "LAUNCHER_STAGE=%STAGE_DIR%\launcher_only"
mkdir "%LAUNCHER_STAGE%"
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.exe"                "%LAUNCHER_STAGE%\" >nul
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.dll"                "%LAUNCHER_STAGE%\" >nul
copy /y "%LAUNCHER_BIN%\MuVoidLauncher.runtimeconfig.json" "%LAUNCHER_STAGE%\" >nul

:: README para los jugadores dentro del ZIP
(
    echo ============================
    echo     VoidboundMU Launcher
    echo ============================
    echo.
    echo REQUISITO:
    echo   .NET 10 Desktop Runtime
    echo   https://dotnet.microsoft.com/en-us/download/dotnet/10.0
    echo.
    echo COMO JUGAR:
    echo   1. Ejecutar MuVoidLauncher.exe
    echo   2. Esperar que descargue los archivos del juego ^(solo primera vez^)
    echo   3. Presionar PLAY
    echo.
    echo Si el launcher no abre: instalar .NET 10 Runtime
) > "%LAUNCHER_STAGE%\LEEME.txt"

if exist "%LAUNCHER_ZIP%" del /f /q "%LAUNCHER_ZIP%"
powershell -NoProfile -Command ^
    "Compress-Archive -Path '%LAUNCHER_STAGE%\*' -DestinationPath '%LAUNCHER_ZIP%' -Force"

echo.
echo ── Publicando en GitHub Releases ────────────────────────────
echo.

:: Crear el release con version.json + launcher + ZIP del launcher
gh release create "%TAG%" ^
    --repo "%GITHUB_REPO%" ^
    --title "VoidboundMU %VERSION%" ^
    --notes "%CHANGELOG%" ^
    "%MANIFEST%#version.json" ^
    "%LAUNCHER_ZIP%#VoidboundMU_Launcher_%VERSION%.zip"

if %errorlevel% neq 0 (
    echo [ERROR] Fallo al crear el release.
    pause & exit /b 1
)

:: Subir los archivos del juego como assets individuales
:: (para que el launcher pueda ir descargando solo lo que cambio)
echo.
echo Subiendo archivos del juego ^(esto puede tardar segun el tamano de Data/^)...
echo.

set "UPLOAD_ERRORS=0"

for %%F in ("%GAME_DIR%\*.exe" "%GAME_DIR%\*.dll") do (
    echo   [EXE/DLL] %%~nxF
    gh release upload "%TAG%" "%%F#MuVoid/%%~nxF" --repo "%GITHUB_REPO%" --clobber
    if errorlevel 1 set /a UPLOAD_ERRORS+=1
)

for /r "%GAME_DIR%\Data" %%F in (*) do (
    set "REL=%%F"
    set "REL=!REL:%GAME_DIR%\=!"
    echo   [Data] !REL!
    gh release upload "%TAG%" "%%F#MuVoid/!REL!" --repo "%GITHUB_REPO%" --clobber
    if errorlevel 1 set /a UPLOAD_ERRORS+=1
)

if exist "%GAME_DIR%\Translations" (
    for /r "%GAME_DIR%\Translations" %%F in (*) do (
        set "REL=%%F"
        set "REL=!REL:%GAME_DIR%\=!"
        echo   [Translations] !REL!
        gh release upload "%TAG%" "%%F#MuVoid/!REL!" --repo "%GITHUB_REPO%" --clobber
        if errorlevel 1 set /a UPLOAD_ERRORS+=1
    )
)

echo.
if "%UPLOAD_ERRORS%"=="0" (
    echo ============================================================
    echo  [OK] Release publicado!
    echo.
    echo  Release : https://github.com/%GITHUB_REPO%/releases/tag/%TAG%
    echo.
    echo  Pasos para los jugadores:
    echo    1. Descargar VoidboundMU_Launcher_%VERSION%.zip  (~5MB^)
    echo    2. Descomprimir y ejecutar MuVoidLauncher.exe
    echo    3. El launcher descarga el juego automaticamente al primer inicio
    echo    4. PLAY
    echo ============================================================
) else (
    echo [WARN] Release creado pero %UPLOAD_ERRORS% archivo(s) fallaron al subir.
    echo        Reintenta con: release_client.bat %VERSION% "%CHANGELOG%"
)

echo.
pause
endlocal
