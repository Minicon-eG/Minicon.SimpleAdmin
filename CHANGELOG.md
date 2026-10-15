# Changelog

Alle nennenswerten Änderungen an den Minicon.SimpleAdmin-Paketen. Alle Pakete (Shared, Services, Checkers,
HtmlGenerator, Worker, WebUI) erscheinen immer gemeinsam mit derselben Version.
Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionen nach [SemVer](https://semver.org/lang/de/).

## [2.35.0] – 2026-10-22 – Domains mit Umgebungszuordnung und Namenskonvention

**Domains (DNS-Namen) lassen sich jetzt einer Umgebung, Servern dieser Umgebung und optional einem NetScaler-vServer zuordnen; die Namenskonvention liefert Vorschläge und Warnungen.**

### Neu
- `domains` in der `config.json`: `name`, `environment` (Schlüssel aus `environments`, immer explizit), `servers`, optional `netScalerVServer` (mehrere Domains dürfen denselben teilen), `healthCheck`, `description`, `allowNameMismatch`. Fehlt der Block, gilt eine leere Liste.
- Namenskonvention: `<name>-dev` → development, `<name>-ref` → ref, `<name>-prod` und `<name>` ohne Suffix → production. Beide PROD-Varianten sind eigenständige Einträge mit eigenen Servern und vServern. Weitere Suffixe/Umgebungen über `domainSuffixMap` (z. B. `{ "-test": "test" }`).
- WebUI-Menüpunkt **Domains**: Liste gruppiert nach Umgebung mit Filter, Anlegen/Bearbeiten/Löschen. Die Umgebung wird aus dem Namen vorbelegt, die Serverauswahl zeigt nur Server der gewählten Umgebung und aktualisiert sich beim Wechsel. Passt der Name nicht zur Umgebung, wird erst mit „Ausnahme zulassen“ gespeichert.
- Serverliste: Spalte und Filter **Umgebung**.
- Statische Übersichtsseiten zeigen die Domains jeder Umgebung unter der Umgebungsüberschrift.
- Dokumentation (Hilfe-Seite und `documentation.html`, Kapitel 19) zu Domains, Konvention, PROD-Varianten, Ausnahme-Flag und `domainSuffixMap`.

### Geändert
- Umbenennen oder Löschen eines Servers pflegt die Serverlisten der Domains mit.

### Behoben
- Die zentrale Statusübersicht zeigt jetzt auch Server aus weiteren Umgebungen (bisher nur production, ref, development).

### Intern
- `DomainNamingConvention` (Shared) mit Tests für Suffix-Erkennung, Override-Map, Mismatch/Ausnahme, Deserialisierung und Serverfilter nach Umgebung.

## [2.34.2] – 2026-10-19 – sc.exe-Argumente korrekt splitten

**Der Dienst-Installer aus 2.34.0/2.34.1 legt den Dienst jetzt tatsächlich an.**

### Behoben
- `install` brach mit `ERROR: Invalid start= field` ab: `binPath=`, `start=`, `DisplayName=`, `obj=` und `password=` werden jetzt mit ihren Werten als getrennte Argumente an `sc.exe` übergeben.

### Intern
- Unit-Test im Dispatch-Pfad (`start=` und `auto` als getrennte Argumente) verhindert einen Rückfall.

## [2.34.1] – 2026-10-19 – CPU-Metrik gegen Selbstkontamination des Workers

**Keine falschen 100-%-CPU-Meldungen mehr auf eigentlich ruhigen Servern.**

### Behoben
- Die Windows-CPU-Messung nahm eine einzelne 100-ms-Stichprobe, die fast immer in die Last des Workers selbst fiel (Start, parallel laufende Checker).

### Neu
- Mehrfachabtastung mit Median (Standard 3 Messungen über 900 ms), Abzug der eigenen CPU-Last des Workers, Worker-Prozess mit Priorität `BelowNormal`.
- Einstellbar in der `appsettings.json`: `Metrics:Cpu:WindowMs`, `Metrics:Cpu:Samples`, `Metrics:Cpu:SubtractSelf`.
- Debug-Log mit Median, Eigenanteil und Einzelwerten.

## [2.34.0] – 2026-10-19 – Windows-Dienst-Installer und robuste DataProtection-Keys

**Die WebUI lässt sich ohne IIS als Windows-Dienst installieren; der Startabsturz durch verwaiste DataProtection-Keys ist behoben.**

### Neu
- Dienst-Installer: `<Host>.exe install|uninstall|start|stop` legt Dienst (Autostart), URL-ACL, Firewall-Regel und DataProtection-Keys-Ordner an. Flags u. a. `--port`, `--account`, `--service-name`, `--display-name`, `--description`, `--keys-dir`, `--non-interactive`, `--purge`.
- Im Dienstbetrieb hostet die WebUI über HttpSys, damit die Windows-Anmeldung ohne IIS funktioniert. Konsole und IIS bleiben unverändert.
- `install.cmd` als Starter neben der Host-EXE.

### Geändert
- Neuer Einzeiler für den Host: `return await SimpleAdminWebUIHost.RunAsync(args);`. Die bisherige Variante funktioniert weiter.

### Behoben
- Startabsturz mit `CryptographicException` durch DataProtection-Keys im Benutzerprofil: Keys liegen jetzt unter `C:\ProgramData\SimpleAdmin\dp-keys` (DPAPI, LocalMachine). Einstellbar über `DataProtection:KeysDirectory` bzw. `DataProtection:Enabled`.

### Intern
- `sc.exe`, `netsh` und `icacls` laufen über `IProcessRunner` und sind in den Tests gemockt.

## [2.33.0] – 2026-10-14 – Glossy-Design, Quittieren in der Server-Ansicht, Verlaufsdiagramme

**Glossy-Design für die WebUI (Hell/Dunkel), Quittieren direkt in der Server-Ansicht und klickbare Verlaufsdiagramme.**

### Neu
- **Glossy-Design** für alle WebUI-Seiten mit **Hell/Dunkel-Umschalter** in der Navigation (gespeichert im Browser, sonst Systemeinstellung)
- **Server-Ansicht:** Probleme direkt **quittieren / aufheben** (gleicher Dialog wie auf der Probleme-Seite), inkl. Lösungsanleitung und Lösungsschritten
- **Verlaufsdiagramme:** Metrik-Kacheln mit Mini-Verlauf; Klick auf Kachel, Metrik-Badge oder CPU-/Speicher-/Disk-Problem springt zum Diagramm mit Warn-/Kritisch-Linien, aktuell/Ø/Min/Max und Wert je Messpunkt
- **Dashboard** mit Überwachungsstand: offene und kritische Probleme, nicht gesunde Server, Quittierte, Liste „Aktuell dringend“; Zähler offener Probleme am Menüpunkt *Probleme*
- **Auto-Refresh** (60 s) auf Dashboard, Status, Probleme und Server-Ansicht – pausiert bei offenem Dialog, Eingabe oder Hintergrund-Tab
- **Anwendungstitel** konfigurierbar: *Einstellungen → Darstellung* (`general.title` in der config.json)

### Geändert
- Status-Übersicht: eine Karte pro Server mit allen Diensten, neue Kachel „Nicht erreichbar“, Filter per Tastatur bedienbar und in der URL
- Einheitlich **Ortszeit** bzw. relative Zeiten („vor 5 Minuten“) statt UTC; deutsche Status-Bezeichnungen (Gesund, Beeinträchtigt, Kritisch, …)
- NuGet-Abhängigkeiten aktualisiert (MailKit 4.18.1, Microsoft.Data.SqlClient 7.1.1, Microsoft.Extensions.* 10.0.13, …)

### Behoben
- **Sicherheit:** `System.Security.Cryptography.Pkcs` (transitiv über MimeKit) auf 10.0.13 festgelegt – GHSA-846f-f764-76mx (hoch), GHSA-2fmv-rv2c-59x7
- „Gestoppt seit“ bei AppPools zeigt jetzt auch Tage (vorher fielen sie ab 24 h weg)
- Disk-Problemmeldung: „Disk C:“ statt „Disk C::“ und korrekter Vergleich bei freiem Speicher („<“); Problem-IDs unverändert, bestehende Quittierungen bleiben gültig
- Sprungziel der Charts nicht mehr unter der Navigationsleiste; Debug-Ausgaben aus der Detailansicht entfernt

## [2.32.1] – 2026-10-14 – Problem-Startzeit (First/Last) wird gespeichert

**Problem-Startzeit (First/Last) wird gespeichert**

### Behoben
- „First“ und „Last“ eines aktiven Problems waren immer gleich (Zeitpunkt der Seitenerzeugung), obwohl das Problem schon viele Zyklen bestand. Der Worker speichert jetzt pro Problem-ID, seit wann es besteht (`RuntimeStatus.ProblemsSince`), und führt das jeden Zyklus fort. Statische HTML-Seite, WebUI (Probleme-Seite, Status-Details) und Notifier (inkl. „war seit … aktiv“ in der Behoben-Mail) zeigen die echte Startzeit.
- Problemkarten auf der HTML-Seite zeigen bei Metrik-Problemen nicht mehr `cpu:` mit leerer Quelle.

### Hinweise
- Für bereits bestehende Probleme beginnt die Zeit beim ersten Lauf des neuen Workers.
- Wechselt ein Problem den Schweregrad (Warning → Critical), beginnt die Zeit neu.
- Status-Dateien älterer Worker enthalten das Feld nicht – dort bleibt First = Last.

## [2.32.0] – 2026-10-13 – Assistent für die Datei-/Protokollüberwachung, Quellcode im Host-Projekt

### Einrichtungs-Assistent für die Datei-/Protokollüberwachung, Quellcode im Host-Projekt

### Neu
- **Einrichtungs-Assistent** für die Datei-/Protokollüberwachung (Server → Bearbeiten → „Assistent")
  - **Ordner:** Vorlagen für Übergabe-, Eingangs- und Fehlerordner. Der Probelauf zeigt Anzahl und Typen der Dateien und welche Dateien jetzt gemeldet würden; ein Klick auf einen Dateityp setzt den Filter.
  - **Logdateien:** Trefferzahl und Beispielzeilen je Signatur, ungültige oder zu aufwendige Regex werden gemeldet. Vorschläge gängiger Fehlersignaturen (Exception, ERROR, FATAL, Timeout, Verbindungsfehler, Zugriff verweigert, Speichermangel, Deadlock, WARN) erscheinen mit ihren Treffern und lassen sich per Klick übernehmen. Die letzten Zeilen der neuesten Datei werden angezeigt.
  - Test mit **eingefügten Beispielzeilen**, wenn der WebUI-Server keinen Zugriff auf die Dateien hat
  - „In Formular übernehmen" füllt eine neue Formularzeile
  - REST: `POST /api/filemonitoringtest/{directory|logscan}`
- **Quellcode im Host-Projekt:** Jedes Paket liefert seinen Quellcode mit. Beim Build des Hosts wird er nach `SimpleAdmin.Sources/` entpackt und ist vom Host-Build ausgeschlossen.
  - `<SimpleAdminSourcesNamespace>Kunde.Monitoring</SimpleAdminSourcesNamespace>` benennt `Minicon.SimpleAdmin` beim Entpacken in den eigenen Namespace um (Namespaces, Projekt-/Datei-/Ordnernamen, View-Pfade, Logger-Kategorien). Die LICENSE bleibt unverändert.
  - Abschalten mit `<SimpleAdminExtractSources>false</SimpleAdminExtractSources>`, Zielordner über `SimpleAdminSourcesDirectory`

### Geändert
- `Minicon.SimpleAdmin.WebUI` hängt jetzt von `Minicon.SimpleAdmin.Checkers` ab; NuGet lädt das Paket automatisch mit.
- Die Pakete enthalten zusätzlich die Quelldateien (`sources/`) und `buildTransitive/<PackageId>.targets`.

### Sicherheit
- Der Probelauf liest Dateien vom WebUI-Server. Ist die Anmeldung abgeschaltet (`Authentication:Enabled=false`), ist der Dateizugriff gesperrt. Die Ausgabe ist begrenzt, Regex und Dateizugriffe haben Timeouts.

## [2.31.1] – 2026-10-13 – Skript-Ausführung: Review-Befunde behoben

**Skript-Ausführung: Review-Befunde zu Performance und Wartbarkeit behoben**

### Behoben
- Schalter mit Standard `$true` (`[switch]$X = $true`) lassen sich abschalten — Übergabe als `-X:$false`.
- Der Log-Download liefert immer die vollständige Log-Datei (vorher bei neueren Läufen nur die letzten 20 000 Zeilen).
- „Parameter neu einlesen“: Hat sich der Typ eines Parameters im Skript geändert, wird das Eingabefeld neu erkannt. Ein geänderter Standardwert im Skript wird übernommen. Alle Änderungen werden nach dem Einlesen aufgelistet.
- Ausgeblendete Pflichtparameter werden beim Speichern abgelehnt (vorher scheiterte jeder Start).
- Ein Abbruch direkt nach dem Start geht nicht mehr verloren.
- Ein Lauf endet mit dem Skript, auch wenn ein Hintergrundprozess die Ausgabe offen hält. Die Wartezeiten nach Zeitlimit und Abbruch sind begrenzt.
- Min/Max mit Nachkommastellen werden auf deutschen Servern richtig gelesen.
- Standardwerte wie `= Get-Date` werden nicht mehr als Text „Get-Date“ übernommen.
- **Worker:** `EventLogChecker` liest die Fehlerausgabe mit und beendet die Abfrage nach 2 Minuten — ein voller Fehlerkanal kann den Worker-Zyklus nicht mehr anhalten.

### Performance
- Ausgabe im Ringpuffer (konstanter Aufwand je Zeile), Zeilen auf 8 KB begrenzt, Log-Datei gepuffert geschrieben. Beendete Läufe bleiben nur 2 Minuten im Speicher.
- Bei älteren Läufen wird nur das Ende der Log-Datei gelesen statt der ganzen Datei bei jedem Abruf.
- Die Ausgabe-Seite hält höchstens 4 000 Zeilen, stoppt bei gelöschten Läufen, wartet bei Fehlern länger und fragt in Hintergrund-Tabs seltener ab.
- Die Dateiauswahl bleibt auch bei Ordnern mit sehr vielen Dateien schnell.

### Geändert
- **Ohne Anmeldung automatisch aus:** Läuft die WebUI mit `"Authentication": { "Enabled": false }`, ist die Skript-Ausführung abgeschaltet. Nur für isolierte Test-Hosts: `"ScriptRunner": { "AllowWithoutAuthentication": true }`.
- Log-Dateien sind jetzt lesbarer Text (`2026-10-13 14:05:01.123 | Ausgabe`, Fehler mit `!`). Ältere Logs werden weiter gelesen.
- Der Verlauf (`history.json`) geht beim IIS-Overlapped-Recycle nicht mehr verloren.
- Eine unlesbare `scripts.json` wird angezeigt und nicht überschrieben; Änderungen von Hand werden erkannt.

## [2.31.0] – 2026-10-12 – PowerShell-Skripte aus der WebUI starten

**PowerShell-Skripte direkt aus der WebUI starten**

### Neu
- **Menüpunkt „Skripte“**: PowerShell-Skripte (`.ps1`) hochladen oder einen vorhandenen Pfad auf dem Server auswählen.
- **Automatische Parametererkennung** aus `param()`-Block und Comment-Based-Help, ohne das Skript auszuführen:
  - Datum → Datumsauswahl (Übergabeformat aus der Hilfe, z. B. `YYYY-MM-DD` oder `DD.MM.YYYY`)
  - Uhrzeit (`TimeFrom`, „HH:mm“) → Zeitauswahl
  - `[switch]` → Schalter, `[ValidateSet()]` → Dropdown (bei Arrays Mehrfachauswahl)
  - `[string[]]` → Liste, `[int]`/`[double]` (+ `ValidateRange`) → Zahlenfeld
  - `…Path`/`…File` → Dateiauswahl, `…Folder`/`…Directory` → Ordnerauswahl (Dateisystem des Servers)
  - Pflichtfelder, Standardwerte und `ValidatePattern` werden übernommen
- **Anpassbar**: Bezeichnung, Feldtyp, Format, Auswahlwerte; Parameter fest vorgeben oder ausblenden; „Parameter neu einlesen“ behält eigene Anpassungen.
- **Ausführung**: Aufruf-Vorschau, Live-Ausgabe (Fehler rot), Abbrechen, Zeitlimit, „Mit diesen Werten erneut“, Log-Download.
- **Protokoll**: Jeder Lauf mit Benutzer, Parametern, Dauer und Exit-Code (`<ConfigPath>/script-runs`).
- **Standardmäßig aktiv**, gesteuert über `appsettings.json` der WebUI:
  ```json
  "ScriptRunner": { "Enabled": true, "RequiredRole": "DOMAIN\\SimpleAdmin-Skripte", "AllowedDirectories": [ "D:\\Skripte" ] }
  ```
  Weitere Schlüssel: `BrowseRoots`, `PowerShellPath`, `MaxConcurrentRuns`, `UploadDirectory`, `RunLogDirectory`.

### Sicherheit
- Werte werden Base64-kodiert übergeben und per Splatting gebunden — keine Befehlszeilen-Injection; Ausführung mit `-NonInteractive`.
- `EventLogChecker`: Der Log-Name aus `config.json` wird nur noch als maskiertes Literal eingesetzt, der Aufruf läuft über `-EncodedCommand`. Damit lässt sich über `logName` kein Code mehr in den PowerShell-Aufruf des Workers einschleusen.

### Hinweise
- Skripte laufen auf dem Rechner der WebUI unter deren Konto (IIS: Anwendungspool-Identität). Ein Recycle des Pools beendet laufende Skripte (Status „Unterbrochen“).
- Exit-Code ≠ 0 gilt als fehlgeschlagen. Skripte mit `robocopy` sollten am Ende `exit 0` setzen.
- Abschalten pro Host: `"ScriptRunner": { "Enabled": false }`.

## [2.30.0] – 2026-10-08

### Neu
- **Standardmäßig nur offene Probleme**: Die Probleme-Seite zeigt zunächst nur nicht quittierte Probleme.
- **Filter**: Dropdowns für Status (Offen / Quittiert / Alle), Schweregrad und Server sowie eine Freitextsuche. Der Filter bleibt in der URL erhalten (z. B. `/Problems?status=all`), „Filter zurücksetzen“ stellt die Standardansicht wieder her.
- **Kacheln** (Offen, Critical offen, Warning offen, Quittiert) setzen per Klick den passenden Filter.

### Geändert
- **Modernisiertes Layout**: farbige Schweregrad-Leiste je Zeile, Schweregrad- und Status-Markierungen, lesbare Meldung als Haupttext, Server mit Link auf die Detailseite, relative Zeitangabe („vor 3 Stunden“, genaue Zeiten im Tooltip), einklappbare Lösungsschritte.
- In der Ansicht „Alle“ stehen offene Probleme oben, quittierte darunter und abgeblendet.
- **Mehrfach-Quittierung**: Aktionsleiste „n ausgewählt · Auswahl quittieren“ erscheint bei Auswahl; „Alle auswählen“ wirkt nur auf die angezeigten Probleme; der Dialog listet lesbare Meldungen statt technischer IDs.

### Intern
- Filterung clientseitig, keine API-Änderung; Styles im Abschnitt „Problems page“ in `site.css`. Direktlink aus der Benachrichtigungs-Mail (`?ackServer=…&ackProblem=…`) funktioniert unverändert.

## [2.29.0] – 2026-10-08

### Neu
- **Diagnose auf der Statusseite**: Werden keine Einträge angezeigt, erklärt die Seite unter „Warum werden keine Einträge angezeigt?“ die wahrscheinlichen Ursachen – z. B. `config.json` nicht geladen, Statusverzeichnis fehlt / ist leer / enthält keine gültigen Statusdateien, HTTP-Abruf deaktiviert, kein aktiver Server bzw. keiner mit `baseUrl`.
- Dazu die **aktuellen Einstellungen**: Pfad und Ladezustand der `config.json`, `StatusDirectory` (appsettings) und `output.centralOutputPath` samt Angabe, welcher gilt, die tatsächlich gelesenen Verzeichnisse mit Dateianzahl und neuester Datei, `pullStatusOverHttp` und die Server-Anzahl (gesamt / aktiv / mit `baseUrl`).

### Geändert
- Footer zeigt die tatsächliche Paketversion statt fest „v1.0“.
- Hilfe-Seite: `POST /api/acknowledges/bulk` in der REST-API-Tabelle; README auf aktuelle Paketversion.

### Intern
- `StatusReaderService.GetDiagnostics()` / `StatusSourceDiagnostics` (nur Dateisystem, kein HTTP; wird nur bei leerer Seite ermittelt), `ConfigurationService.FilePath`, 3 neue Tests.

## [2.28.0] – 2026-10-08

### Neu
- **Mehrfach-Quittierung auf der Probleme-Seite**: Unquittierte Probleme lassen sich per Checkbox (oder „Alle auswählen“) markieren und mit **„Auswahl quittieren“** in einem Schritt bestätigen. Dauer, Enddatum und Kommentar gelten für alle ausgewählten Probleme.
- **REST API**: `POST /api/acknowledges/bulk` mit `items: [{ serverId, problemId }]` und denselben Feldern wie die Einzelquittierung (`durationMinutes`, `expiresAt`, `comment`, …). Doppelte Einträge werden ignoriert.

### Geändert
- Die Hilfe-Seite (Acknowledge-System) beschreibt die Mehrfach-Quittierung.

### Intern
- Bearbeiter-Ermittlung (angemeldeter Windows-Benutzer) im `AcknowledgesController` zusammengefasst; `acknowledges.json` wird bei der Mehrfach-Quittierung nur einmal veröffentlicht.

## [2.27.1] – 2026-10-07 – AJAX-Prefetch zurückgenommen

### Behoben
- Das Nachladen der Statusseite per AJAX aus 2.27.0 wurde zurückgenommen – es wurde in einer Kundenumgebung blockiert. Die Statusseite wird wieder vollständig serverseitig gerendert.

## [2.27.0] – 2026-10-06 – Statusseite lädt per AJAX nach

### Geändert
- Statusseite: Die Serverdaten werden per AJAX nachgeladen (Prefetch), statt die Seite erst nach dem Lesen aller Statusquellen auszuliefern. In 2.27.1 wieder zurückgenommen.

## [2.26.0] – 2026-10-02

Erste Version aus dem öffentlichen Repository `Minicon-eG/Minicon.SimpleAdmin`.

### Neu
- Quellcode öffentlich auf GitHub; Paket-Metadaten (Repository, Projektseite, MIT-Lizenz) zeigen auf dieses Repository.
- README wird jetzt auch auf nuget.org angezeigt.

### Geändert
- Beispiele, Platzhalter und Dokumentation verwenden durchgängig neutrale Namen (`server01`, `fileserver01`, `example.org`).
- Veröffentlichung über GitHub-gehostete Runner und nuget.org Trusted Publishing.

### Intern
- Funktional identisch mit 2.25.2 (inkl. Sicherheitsupdate `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.12).
- Die Versionshistorie bis 2.25.2 ist in den GitHub-Releases dieses Repos nicht enthalten; die Pakete bleiben auf nuget.org verfügbar.


[2.35.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.34.2...v2.35.0
[2.34.2]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.34.1...v2.34.2
[2.34.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.34.0...v2.34.1
[2.34.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.33.0...v2.34.0
[2.33.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.32.1...v2.33.0
[2.32.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.32.0...v2.32.1
[2.32.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.31.1...v2.32.0
[2.31.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.31.0...v2.31.1
[2.31.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.30.0...v2.31.0
[2.30.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.29.0...v2.30.0
[2.29.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.28.0...v2.29.0
[2.28.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.27.1...v2.28.0
[2.27.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.27.0...v2.27.1
[2.27.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.26.0...v2.27.0
[2.26.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/releases/tag/v2.26.0
