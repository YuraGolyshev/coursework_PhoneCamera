@echo off

REM Путь к исполняемому файлу Debug версии
set EXE_PATH=%~dp0CameraReceiver\bin\Debug\net8.0-windows\win-x64\CameraReceiver.exe

echo Запуск %EXE_PATH% с правами администратора...

REM Запуск приложения с правами администратора
powershell -Command "Start-Process -FilePath '%EXE_PATH%' -Verb RunAs"

echo Команда запуска отправлена. 