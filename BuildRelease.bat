@echo off
setlocal

set PROJECT_DIR=%~dp0projects\Splatviewer_VR
set BUILD_METHOD=BuildSetup.BuildWindowsRelease
set LOG_FILE=%~dp0build.log

:: Try to find Unity from the Hub default install path
set UNITY_EXE=
for /d %%D in ("%ProgramFiles%\Unity\Hub\Editor\*") do (
    if exist "%%D\Editor\Unity.exe" set UNITY_EXE=%%D\Editor\Unity.exe
)

if "%UNITY_EXE%"=="" (
    echo.
    echo  Unity not found in the default Hub install path.
    echo  Please set UNITY_EXE manually in this script or add Unity to your PATH.
    echo.
    pause
    exit /b 1
)

echo.
echo  Unity:   %UNITY_EXE%
echo  Project: %PROJECT_DIR%
echo  Method:  %BUILD_METHOD%
echo  Log:     %LOG_FILE%
echo.
echo  Building... (this may take several minutes)
echo.

"%UNITY_EXE%" -quit -batchmode -nographics -projectPath "%PROJECT_DIR%" -executeMethod %BUILD_METHOD% -logFile "%LOG_FILE%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  Build FAILED. Check %LOG_FILE% for details.
    echo.
    pause
    exit /b 1
)

echo.
echo  Build succeeded!
echo  Output: %PROJECT_DIR%\Release\1.0\SplatViewer_VR.exe
echo.
pause

endlocal
