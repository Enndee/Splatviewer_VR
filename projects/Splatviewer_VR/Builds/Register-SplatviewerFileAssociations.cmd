@echo off
setlocal

set SCRIPT_DIR=%~dp0
pushd "%SCRIPT_DIR%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Register-SplatviewerFileAssociations.ps1" %*
set EXIT_CODE=%ERRORLEVEL%
popd

endlocal
exit /b %EXIT_CODE%