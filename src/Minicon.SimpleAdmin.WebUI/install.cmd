@echo off
rem SimpleAdmin WebUI – Windows-Dienst anlegen und starten.
rem Erwartet eine Host-EXE (z. B. "Minicon.SimpleAdmin.WebUI.Host.exe") im selben Ordner.
rem Beispiel: install.cmd --port=5080

setlocal
cd /d "%~dp0"

for %%F in ("*.exe") do (
    if /I not "%%~nxF"=="install.exe" (
        "%%~fF" install %*
        goto :eof
    )
)

echo Fehler: Keine Host-EXE neben install.cmd gefunden.
exit /b 2
