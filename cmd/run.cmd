@echo off
chcp 65001
cls

rem Variables d'environnement
set APP_NAME=Realmlet
set DATA_DIR=%APPDATA%\Realmlet
set DATADIR_KIND=appdata

cd ..
dotnet run || (
  echo.
  pause
  exit /b %ERRORLEVEL%
)
