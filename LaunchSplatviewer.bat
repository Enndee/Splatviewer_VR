@echo off
setlocal

set RELEASE_DIR=%~dp0releases
set EXE_NAME=Splatviewer_VR.exe

if exist "%RELEASE_DIR%\%EXE_NAME%" (
    start "" "%RELEASE_DIR%\%EXE_NAME%" %*
) else (
    echo.
    echo  Splatviewer_VR.exe not found in releases\.
    echo.
    echo  Please extract the latest release zip into the releases\ folder:
    echo    releases\Splatviewer_VR.exe
    echo    releases\Splatviewer_VR_Data\
    echo    releases\...
    echo.
    pause
)

endlocal
