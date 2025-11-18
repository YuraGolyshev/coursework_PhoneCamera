@echo off
echo Публикация CameraReceiver...

REM Создаем папку для опубликованной версии
mkdir "%~dp0\publish-output" 2>nul

REM Публикуем приложение с теми же настройками, что и работающая версия
dotnet publish "%~dp0\CameraReceiver\CameraReceiver.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0\publish-output"

echo.
echo Публикация завершена! Приложение находится в папке: %~dp0\publish-output
echo.

pause 