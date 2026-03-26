@echo off
pwsh -NoProfile -File "%~dp0tm.ps1" %*
exit /b %ERRORLEVEL%
