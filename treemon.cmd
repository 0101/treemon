@echo off
rem Thin wrapper around the cross-platform lifecycle script. Unlike treemon.ps1 this needs no
rem PowerShell at all, so it works from any Windows shell. All behaviour lives in treemon.fsx.
dotnet fsi "%~dp0treemon.fsx" %*
exit /b %ERRORLEVEL%
