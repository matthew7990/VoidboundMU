@echo off
set "VSCMD=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"

echo.
echo ==========================================
echo [MuMain] Optimizando compilacion incremental...
echo ==========================================
echo Configurando entorno VS para x86...
call "%VSCMD%" x86 >nul

cd /d "%~dp0clients\MuMain"

if not exist build (
    echo [INFO] Creando carpeta build por primera vez...
    mkdir build
)

cd build

:: Solo ejecutar CMake si no existe el archivo de proyecto o si se desea refrescar
if not exist Main.sln (
    echo [INFO] Generando archivos de proyecto con CMake...
    cmake -S .. -B . -A Win32
    if %errorlevel% neq 0 (
        echo [ERROR] Error al configurar CMake.
        exit /b %errorlevel%
    )
)

echo.
echo [INFO] Compilando cambios (Incremental)...
:: Compilar especificamente el proyecto Main para ganar tiempo
cmake --build . --target Main --config Release -- /m /p:CL_MPcount=8
if %errorlevel% neq 0 (
    echo [ERROR] Error durante la compilacion.
    exit /b %errorlevel%
)

echo.
echo ==========================================
echo Compilacion COMPLETADA.
echo Ubicacion: clients\MuMain\build\src\Release\Main.exe
echo ==========================================
