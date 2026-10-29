# Changelog

Alle nennenswerten Änderungen an den Minicon.SimpleAdmin-Paketen. Alle Pakete (Shared, Services, Checkers,
HtmlGenerator, Worker, WebUI) erscheinen immer gemeinsam mit derselben Version.
Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionen nach [SemVer](https://semver.org/lang/de/).

Die Abschnitte *Neu*, *Geändert*, *Behoben*, *Sicherheit*, *Performance* und *Hinweise* erscheinen in der WebUI
unter „Neuigkeiten“ und beschreiben nur, was Anwender bemerken. Technisches für Entwickler und Betreiber
(Pakete, Host-Projekte, REST-API, Abhängigkeiten, Tests, Build) steht unter *Entwickler* bzw. *Intern* und wird
in der WebUI nicht angezeigt.

## [2.36.1] – 2026-10-29 – Verlässlichere CPU-Werte, Übersichten ohne seitliches Scrollen

**Die CPU-Auslastung entspricht jetzt der tatsächlichen Last, und lange Problemtexte passen auf den Bildschirm.**

### Behoben
- Server meldeten trotz Korrektur in 2.34.1 weiterhin hohe CPU-Last, obwohl sie kaum ausgelastet waren. Die Überwachung misst die CPU jetzt erst, wenn ihre übrigen Prüfungen fertig sind, und nimmt den Durchschnitt über eine Minute. Kurze Spitzen lösen keinen Alarm mehr aus.
- Die Probleme-Übersicht musste bei langen Einträgen (z. B. BizTalk-Orchestrierungen) seitlich gescrollt werden, Status und „Quittieren“ lagen außerhalb des Bildes. Lange Texte brechen jetzt um, alle Spalten bleiben sichtbar.
- Dasselbe gilt für die Liste „Aktive Probleme“ und die BizTalk- und Dateiüberwachungstabellen auf der Serverseite sowie für das Dashboard.
- Die Navigationsleiste ragte bei mittleren Fensterbreiten über den Rand. Sie wechselt jetzt rechtzeitig zum Menü-Knopf, ein langer Anwendungstitel wird gekürzt.

### Geändert
- In schmalen Fenstern und auf dem Handy werden Probleme als Karten angezeigt: Schweregrad, Server und Zeit oben, darunter der vollständige Text, unten Status und „Quittieren“.

### Hinweise
- Ein Überwachungslauf dauert wegen der einminütigen CPU-Messung etwa eine Minute länger.

### Entwickler
- Worker: `MetricsCollector` läuft nach `Task.WhenAll` der Checker statt parallel; deren Kindprozesse (`powershell.exe`, `sc.exe`, `appcmd.exe`, `netsh`) fielen vorher ins Messfenster und wurden vom Eigenanteil nicht erfasst.
- CPU = Mittelwert statt Median. Neue Defaults `Metrics:Cpu:WindowMs` = 60000 (200 ms … 180 s), `Metrics:Cpu:Samples` = 12 (2 … 120). Neu `Metrics:Cpu:RuntimeBudgetSeconds` = 240 (30 … 3600): Fenster wird ab Prozessstart so verkürzt, dass inkl. 30 s Reserve das Budget eingehalten wird, mindestens 1 s. Der CPU-Wert wird je Lauf einmal gemessen und für weitere ServiceTypes wiederverwendet.
- Hosts mit explizit gesetztem `Metrics:Cpu:WindowMs` behalten ihr Fenster. Läuft der Worker im Minutentakt: Takt verlängern oder Fenster verkürzen, damit sich Läufe nicht überlappen.
- WebUI: `.problem-table` (fixes Tabellenlayout + `<colgroup>`, Karten-Grid unter 1200 px, Variante `.problem-table-compact` für Status/Details), Hilfsklasse `.text-wrap-anywhere`, Navbar `navbar-expand-xl`.

### Intern
- Tests für Mittelwert, Fensterverkürzung und Konfigurationsgrenzen; Windows-Provider-Test mit kurzem Fenster.

## [2.36.0] – 2026-10-26 – Neuigkeiten-Seite

**Was ist neu? Die WebUI zeigt jetzt selbst, was sich in jeder Version geändert hat.**

### Neu
- **Seite „Neuigkeiten“**: die Änderungen jeder Version (neu, geändert, behoben), die aktuelle Version aufgeklappt, ältere zum Aufklappen.
  - Erreichbar über die Versionsnummer unten links und über die Hilfe-Seite.
  - Nach einem Update erscheint neben der Versionsnummer ein **„Neu“**-Hinweis; auf der Seite sind die Versionen seit Ihrem letzten Besuch markiert.

### Entwickler
- Die Versionshinweise stammen aus der `CHANGELOG.md`, die in die WebUI-DLL eingebettet ist (passend zur installierten Version, ohne Internetzugriff). Angezeigt werden nur die Abschnitte für Anwender; *Entwickler* und *Intern* bleiben ausgeblendet.
- **Testprojekt im Host-Quellcode**: Mit `<SimpleAdminExtractTests>true</SimpleAdminExtractTests>` entpackt der Host zusätzlich das Testprojekt nach `SimpleAdmin.Sources/Minicon.SimpleAdmin.Tests/` (bei `SimpleAdminSourcesNamespace` umbenannt, inkl. `InternalsVisibleTo`). Standardmäßig aus.
  - Das Testprojekt referenziert alle Pakete. Worker- und WebUI-Host müssen dafür denselben `SimpleAdminSourcesDirectory` verwenden (z. B. `..\SimpleAdmin.Sources`); fehlt der WebUI-Quellcode dort, gibt der Build eine Warnung aus.
  - Ausführen mit `dotnet test` auf dem entpackten Testprojekt (benötigt das .NET SDK). Wie der übrige Quellcode wird es bei einem Paket-Update überschrieben – eigene Tests gehören in ein eigenes Projekt.

### Intern
- `ChangelogParser` (Teilmenge von Markdown, alles HTML-encodiert, nur http(s)- und relative Links) und `ChangelogProvider` in der WebUI, Tests dazu. Ein Test stellt sicher, dass der oberste CHANGELOG-Eintrag der Paketversion entspricht.
- CHANGELOG nach Zielgruppen aufgeteilt: technische Punkte älterer Versionen in *Entwickler* verschoben.
- Das Worker-Paket enthält das Testprojekt unter `sources-tests/`; alle Pakete liefern `CHANGELOG.md` in `sources-root/` mit, damit auch ein aus dem entpackten Quellcode gebauter Host die Neuigkeiten-Seite hat.

## [2.35.0] – 2026-10-22 – Domains mit Umgebungszuordnung und Namenskonvention

**Domains (DNS-Namen) lassen sich jetzt einer Umgebung, Servern dieser Umgebung und optional einem NetScaler-vServer zuordnen; die Namenskonvention liefert Vorschläge und Warnungen.**

### Neu
- Menüpunkt **Domains**: Liste gruppiert nach Umgebung mit Filter, Anlegen/Bearbeiten/Löschen. Die Umgebung wird aus dem Namen vorbelegt, die Serverauswahl zeigt nur Server der gewählten Umgebung. Passt der Name nicht zur Umgebung, wird erst mit „Ausnahme zulassen“ gespeichert.
- Namenskonvention: `<name>-dev` → Entwicklung, `<name>-ref` → Ref, `<name>-prod` und `<name>` ohne Endung → Produktion. Beide Produktions-Varianten sind eigenständige Einträge mit eigenen Servern und vServern.
- Serverliste: Spalte und Filter **Umgebung**.
- Die statischen Übersichtsseiten zeigen die Domains jeder Umgebung unter der Umgebungsüberschrift.
- Hilfe-Seite: neues Kapitel zu Domains und Namenskonvention.

### Geändert
- Umbenennen oder Löschen eines Servers pflegt die Serverlisten der Domains mit.

### Behoben
- Die zentrale Statusübersicht zeigt jetzt auch Server aus weiteren Umgebungen (bisher nur production, ref, development).

### Entwickler
- `domains` in der `config.json`: `name`, `environment` (Schlüssel aus `environments`, immer explizit), `servers`, optional `netScalerVServer` (mehrere Domains dürfen denselben teilen), `healthCheck`, `description`, `allowNameMismatch`. Fehlt der Block, gilt eine leere Liste. Weitere Suffixe/Umgebungen über `domainSuffixMap` (z. B. `{ "-test": "test" }`).

### Intern
- `DomainNamingConvention` (Shared) mit Tests für Suffix-Erkennung, Override-Map, Mismatch/Ausnahme, Deserialisierung und Serverfilter nach Umgebung.

## [2.34.2] – 2026-10-19 – Technische Korrekturen

### Entwickler
- Der Dienst-Installer aus 2.34.0/2.34.1 legt den Dienst jetzt tatsächlich an.
- `install` brach mit `ERROR: Invalid start= field` ab: `binPath=`, `start=`, `DisplayName=`, `obj=` und `password=` werden jetzt mit ihren Werten als getrennte Argumente an `sc.exe` übergeben.

### Intern
- Unit-Test im Dispatch-Pfad (`start=` und `auto` als getrennte Argumente) verhindert einen Rückfall.

## [2.34.1] – 2026-10-19 – Keine falschen CPU-Spitzen mehr

**Keine falschen 100-%-CPU-Meldungen mehr auf eigentlich ruhigen Servern.**

### Behoben
- Server meldeten zeitweise 100 % CPU-Last, obwohl sie kaum ausgelastet waren. Die Überwachung hatte dabei ihre eigene Last mitgemessen. Die CPU-Werte sind jetzt deutlich ruhiger und entsprechen der tatsächlichen Auslastung.

### Entwickler
- Mehrfachabtastung mit Median (Standard 3 Messungen über 900 ms), Abzug der eigenen CPU-Last des Workers, Worker-Prozess mit Priorität `BelowNormal`.
- Einstellbar in der `appsettings.json`: `Metrics:Cpu:WindowMs`, `Metrics:Cpu:Samples`, `Metrics:Cpu:SubtractSelf`.
- Debug-Log mit Median, Eigenanteil und Einzelwerten.

## [2.34.0] – 2026-10-19 – Zuverlässiger Start der WebUI

### Behoben
- Die WebUI startete nach einem Neustart des Anwendungspools teilweise nicht mehr. Sie startet jetzt zuverlässig.

### Entwickler
- Dienst-Installer: `<Host>.exe install|uninstall|start|stop` legt Dienst (Autostart), URL-ACL, Firewall-Regel und DataProtection-Keys-Ordner an. Flags u. a. `--port`, `--account`, `--service-name`, `--display-name`, `--description`, `--keys-dir`, `--non-interactive`, `--purge`. `install.cmd` als Starter neben der Host-EXE.
- Im Dienstbetrieb hostet die WebUI über HttpSys, damit die Windows-Anmeldung ohne IIS funktioniert. Konsole und IIS bleiben unverändert.
- Neuer Einzeiler für den Host: `return await SimpleAdminWebUIHost.RunAsync(args);`. Die bisherige Variante funktioniert weiter.
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
- **Anwendungstitel** einstellbar unter *Einstellungen → Darstellung*

### Geändert
- Status-Übersicht: eine Karte pro Server mit allen Diensten, neue Kachel „Nicht erreichbar“, Filter per Tastatur bedienbar und in der URL
- Einheitlich **Ortszeit** bzw. relative Zeiten („vor 5 Minuten“) statt UTC; deutsche Status-Bezeichnungen (Gesund, Beeinträchtigt, Kritisch, …)

### Behoben
- „Gestoppt seit“ bei AppPools zeigt jetzt auch Tage (vorher fielen sie ab 24 h weg)
- Disk-Problemmeldung: „Disk C:“ statt „Disk C::“ und korrekter Vergleich bei freiem Speicher („<“); Problem-IDs unverändert, bestehende Quittierungen bleiben gültig
- Sprungziel der Charts nicht mehr unter der Navigationsleiste; Debug-Ausgaben aus der Detailansicht entfernt

### Sicherheit
- Eine Bibliothek mit bekannter Sicherheitslücke wurde aktualisiert.

### Entwickler
- `System.Security.Cryptography.Pkcs` (transitiv über MimeKit) auf 10.0.13 festgelegt – GHSA-846f-f764-76mx (hoch), GHSA-2fmv-rv2c-59x7.
- NuGet-Abhängigkeiten aktualisiert (MailKit 4.18.1, Microsoft.Data.SqlClient 7.1.1, Microsoft.Extensions.* 10.0.13, …).
- `general.title` in der config.json.

## [2.32.1] – 2026-10-14 – Problem-Startzeit (First/Last) wird gespeichert

**Problem-Startzeit (First/Last) wird gespeichert**

### Behoben
- „First“ und „Last“ eines aktiven Problems waren immer gleich, obwohl das Problem schon länger bestand. Statusseiten, Probleme-Seite, Server-Ansicht und Benachrichtigungs-Mails (inkl. „war seit … aktiv“) zeigen jetzt, seit wann ein Problem wirklich besteht.
- Problemkarten auf der HTML-Seite zeigen bei CPU-Problemen keine leere Quelle mehr.

### Hinweise
- Für bereits bestehende Probleme beginnt die Zeit beim ersten Lauf des neuen Workers.
- Wechselt ein Problem den Schweregrad (Warning → Critical), beginnt die Zeit neu.
- Server mit einer älteren Version melden die Startzeit nicht – dort bleibt First = Last.

### Entwickler
- Der Worker speichert pro Problem-ID den ersten Zeitpunkt (`RuntimeStatus.ProblemsSince`) und führt ihn jeden Zyklus fort.

## [2.32.0] – 2026-10-13 – Assistent für die Datei-/Protokollüberwachung

**Ein Assistent hilft beim Einrichten der Datei- und Protokollüberwachung.**

### Neu
- **Einrichtungs-Assistent** für die Datei-/Protokollüberwachung (Server → Bearbeiten → „Assistent")
  - **Ordner:** Vorlagen für Übergabe-, Eingangs- und Fehlerordner. Der Probelauf zeigt Anzahl und Typen der Dateien und welche Dateien jetzt gemeldet würden; ein Klick auf einen Dateityp setzt den Filter.
  - **Logdateien:** Trefferzahl und Beispielzeilen je Signatur, ungültige oder zu aufwendige Regex werden gemeldet. Vorschläge gängiger Fehlersignaturen (Exception, ERROR, FATAL, Timeout, Verbindungsfehler, Zugriff verweigert, Speichermangel, Deadlock, WARN) erscheinen mit ihren Treffern und lassen sich per Klick übernehmen. Die letzten Zeilen der neuesten Datei werden angezeigt.
  - Test mit **eingefügten Beispielzeilen**, wenn der WebUI-Server keinen Zugriff auf die Dateien hat
  - „In Formular übernehmen" füllt eine neue Formularzeile

### Sicherheit
- Der Probelauf liest Dateien vom WebUI-Server. Ist die Anmeldung abgeschaltet, ist der Dateizugriff gesperrt. Die Ausgabe ist begrenzt, Suchmuster und Dateizugriffe haben Zeitlimits.

### Entwickler
- REST: `POST /api/filemonitoringtest/{directory|logscan}`
- **Quellcode im Host-Projekt:** Jedes Paket liefert seinen Quellcode mit. Beim Build des Hosts wird er nach `SimpleAdmin.Sources/` entpackt und ist vom Host-Build ausgeschlossen.
  - `<SimpleAdminSourcesNamespace>Kunde.Monitoring</SimpleAdminSourcesNamespace>` benennt `Minicon.SimpleAdmin` beim Entpacken in den eigenen Namespace um (Namespaces, Projekt-/Datei-/Ordnernamen, View-Pfade, Logger-Kategorien). Die LICENSE bleibt unverändert.
  - Abschalten mit `<SimpleAdminExtractSources>false</SimpleAdminExtractSources>`, Zielordner über `SimpleAdminSourcesDirectory`

- `Minicon.SimpleAdmin.WebUI` hängt jetzt von `Minicon.SimpleAdmin.Checkers` ab; NuGet lädt das Paket automatisch mit.
- Die Pakete enthalten zusätzlich die Quelldateien (`sources/`) und `buildTransitive/<PackageId>.targets`.
- Der Dateizugriff des Probelaufs ist bei `Authentication:Enabled=false` gesperrt.

## [2.31.1] – 2026-10-13 – Skript-Ausführung verbessert

**Zahlreiche Verbesserungen und Fehlerbehebungen bei der Skript-Ausführung.**

### Behoben
- Schalter mit Standard `$true` (`[switch]$X = $true`) lassen sich abschalten — Übergabe als `-X:$false`.
- Der Log-Download liefert immer die vollständige Log-Datei (vorher bei neueren Läufen nur die letzten 20 000 Zeilen).
- „Parameter neu einlesen“: Hat sich der Typ eines Parameters im Skript geändert, wird das Eingabefeld neu erkannt. Ein geänderter Standardwert im Skript wird übernommen. Alle Änderungen werden nach dem Einlesen aufgelistet.
- Ausgeblendete Pflichtparameter werden beim Speichern abgelehnt (vorher scheiterte jeder Start).
- Ein Abbruch direkt nach dem Start geht nicht mehr verloren.
- Ein Lauf endet mit dem Skript, auch wenn ein Hintergrundprozess die Ausgabe offen hält. Die Wartezeiten nach Zeitlimit und Abbruch sind begrenzt.
- Min/Max mit Nachkommastellen werden auf deutschen Servern richtig gelesen.
- Standardwerte wie `= Get-Date` werden nicht mehr als Text „Get-Date“ übernommen.
- Die Ereignisprotokoll-Prüfung kann die Überwachung nicht mehr anhalten; eine hängende Abfrage wird nach 2 Minuten beendet.

### Performance
- Die Ausgabe laufender und älterer Skripte wird schneller angezeigt und belastet den Server weniger.
- Die Ausgabe-Seite hält höchstens 4 000 Zeilen, stoppt bei gelöschten Läufen, wartet bei Fehlern länger und fragt in Hintergrund-Tabs seltener ab.
- Die Dateiauswahl bleibt auch bei Ordnern mit sehr vielen Dateien schnell.

### Geändert
- Ohne Anmeldung ist die Skript-Ausführung automatisch abgeschaltet.
- Heruntergeladene Logs sind jetzt lesbarer Text mit Zeitstempel je Zeile, Fehler mit `!` markiert.
- Der Verlauf der Skript-Läufe geht beim Neustart des Anwendungspools nicht mehr verloren.
- Eine beschädigte Skript-Liste wird angezeigt statt überschrieben.

### Entwickler
- `EventLogChecker` liest stderr parallel mit und beendet die Abfrage nach `QueryTimeout` (2 min).
- Ausgabe im Ringpuffer (konstanter Aufwand je Zeile), Zeilen auf 8 KB begrenzt, Log-Datei gepuffert geschrieben. Beendete Läufe bleiben nur 2 Minuten im Speicher; ältere Läufe lesen nur das Ende der Log-Datei.
- Ohne Anmeldung (`"Authentication": { "Enabled": false }`) ist der Script-Runner aus. Nur für isolierte Test-Hosts: `"ScriptRunner": { "AllowWithoutAuthentication": true }`.
- Log-Format `2026-10-13 14:05:01.123 | Ausgabe`; ältere Logs werden weiter gelesen. `history.json` übersteht IIS-Overlapped-Recycle; eine unlesbare `scripts.json` wird nicht überschrieben, Änderungen von Hand werden erkannt.

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
- **Protokoll**: Jeder Lauf mit Benutzer, Parametern, Dauer und Exit-Code.

### Sicherheit
- Eingaben im Skript-Formular können keine zusätzlichen Befehle ausführen.
- Die Ereignisprotokoll-Prüfung ist gegen eingeschleusten Code in der Konfiguration abgesichert.

### Hinweise
- Skripte laufen auf dem Rechner der WebUI unter deren Konto. Ein Neustart des Anwendungspools beendet laufende Skripte (Status „Unterbrochen“).
- Exit-Code ≠ 0 gilt als fehlgeschlagen. Skripte mit `robocopy` sollten am Ende `exit 0` setzen.

### Entwickler
- Standardmäßig aktiv, gesteuert über die `appsettings.json` der WebUI (abschalten pro Host: `"ScriptRunner": { "Enabled": false }`):
  ```json
  "ScriptRunner": { "Enabled": true, "RequiredRole": "DOMAIN\\SimpleAdmin-Skripte", "AllowedDirectories": [ "D:\\Skripte" ] }
  ```
  Weitere Schlüssel: `BrowseRoots`, `PowerShellPath`, `MaxConcurrentRuns`, `UploadDirectory`, `RunLogDirectory`. Protokolle unter `<ConfigPath>/script-runs`.
- Werte werden Base64-kodiert übergeben und per Splatting gebunden — keine Befehlszeilen-Injection; Ausführung mit `-NonInteractive`.
- `EventLogChecker`: Der Log-Name aus `config.json` wird nur noch als maskiertes Literal eingesetzt, der Aufruf läuft über `-EncodedCommand`.

## [2.30.0] – 2026-10-08 – Filter auf der Probleme-Seite

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

## [2.29.0] – 2026-10-08 – Diagnose auf der Statusseite

### Neu
- **Diagnose auf der Statusseite**: Werden keine Einträge angezeigt, erklärt die Seite unter „Warum werden keine Einträge angezeigt?“ die wahrscheinlichen Ursachen (z. B. Konfiguration nicht geladen, Statusverzeichnis leer, kein aktiver Server) und zeigt die dafür maßgeblichen Einstellungen.

### Geändert
- Die Fußzeile zeigt die tatsächlich installierte Version.

### Entwickler
- Die Diagnose nennt Pfad und Ladezustand der `config.json`, `StatusDirectory` (appsettings) und `output.centralOutputPath` samt Angabe, welcher gilt, die gelesenen Verzeichnisse mit Dateianzahl und neuester Datei, `pullStatusOverHttp` und die Server-Anzahl (gesamt / aktiv / mit `baseUrl`).
- Hilfe-Seite: `POST /api/acknowledges/bulk` in der REST-API-Tabelle; README auf aktuelle Paketversion.

### Intern
- `StatusReaderService.GetDiagnostics()` / `StatusSourceDiagnostics` (nur Dateisystem, kein HTTP; wird nur bei leerer Seite ermittelt), `ConfigurationService.FilePath`, 3 neue Tests.

## [2.28.0] – 2026-10-08 – Mehrfach-Quittierung

### Neu
- **Mehrfach-Quittierung auf der Probleme-Seite**: Unquittierte Probleme lassen sich per Checkbox (oder „Alle auswählen“) markieren und mit **„Auswahl quittieren“** in einem Schritt bestätigen. Dauer, Enddatum und Kommentar gelten für alle ausgewählten Probleme.

### Geändert
- Die Hilfe-Seite beschreibt die Mehrfach-Quittierung.

### Entwickler
- REST API: `POST /api/acknowledges/bulk` mit `items: [{ serverId, problemId }]` und denselben Feldern wie die Einzelquittierung (`durationMinutes`, `expiresAt`, `comment`, …). Doppelte Einträge werden ignoriert.

### Intern
- Bearbeiter-Ermittlung (angemeldeter Windows-Benutzer) im `AcknowledgesController` zusammengefasst; `acknowledges.json` wird bei der Mehrfach-Quittierung nur einmal veröffentlicht.

## [2.27.1] – 2026-10-07 – Statusseite lädt wieder in einem Stück

### Behoben
- Die Statusseite wird wieder in einem Stück geladen; das Nachladen aus 2.27.0 wurde in einer Umgebung blockiert und ist zurückgenommen.

## [2.27.0] – 2026-10-06 – Statusseite erscheint schneller

### Geändert
- Die Statusseite erscheint sofort und lädt die Serverdaten nach. In 2.27.1 wieder zurückgenommen.

## [2.26.0] – 2026-10-02 – Neutrale Beispiele in der Dokumentation

### Geändert
- Beispiele und Dokumentation verwenden durchgängig neutrale Namen (`server01`, `fileserver01`, `example.org`).

### Entwickler
- Erste Version aus dem öffentlichen Repository `Minicon-eG/Minicon.SimpleAdmin`.
- Quellcode öffentlich auf GitHub; Paket-Metadaten (Repository, Projektseite, MIT-Lizenz) zeigen auf dieses Repository. README auch auf nuget.org.
- Veröffentlichung über GitHub-gehostete Runner und nuget.org Trusted Publishing.

### Intern
- Funktional identisch mit 2.25.2 (inkl. Sicherheitsupdate `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.12).
- Die Versionshistorie bis 2.25.2 ist in den GitHub-Releases dieses Repos nicht enthalten; die Pakete bleiben auf nuget.org verfügbar.


[2.36.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.36.0...v2.36.1
[2.36.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.35.0...v2.36.0
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
