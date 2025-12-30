@echo off
setlocal
cd /d "%~dp0"

REM Start Python REST server (minimized)
start "iCUE Python REST" /min py -3 "%~dp0icue_listener_color.py"

REM Give server a moment to start
timeout /t 1 /nobreak >nul

REM Start C# listener (minimized)
start "LangChangeToiCUE" /min "%~dp0LangChangeToiCUE.exe"

endlocal
