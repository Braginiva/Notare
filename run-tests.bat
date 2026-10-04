@echo off
chcp 65001 >nul
REM ============================================================
REM  Notare — тесты сервера с замером покрытия
REM  Если сервер запущен и его код не менялся — тесты
REM  спокойно работают рядом с ним.
REM ============================================================
cd /d "%~dp0"

dotnet test Diary.Server.Tests --nologo --collect:"XPlat Code Coverage" --results-directory TestResults

echo.
echo Отчёт покрытия: TestResults\*\coverage.cobertura.xml
pause
