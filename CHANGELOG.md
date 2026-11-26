# Changelog

Alle nennenswerten Änderungen an den Minicon.SimpleAdmin-Paketen. Alle Pakete (Shared, Services, Checkers,
HtmlGenerator, Worker, WebUI) erscheinen immer gemeinsam mit derselben Version.
Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionen nach [SemVer](https://semver.org/lang/de/).

Die Abschnitte *Neu*, *Geändert*, *Behoben*, *Sicherheit*, *Performance* und *Hinweise* erscheinen in der WebUI
unter „Neuigkeiten“ und beschreiben nur, was Anwender bemerken. Technisches für Entwickler und Betreiber
(Pakete, Host-Projekte, REST-API, Abhängigkeiten, Tests, Build) steht unter *Entwickler* bzw. *Intern* und wird
in der WebUI nicht angezeigt.

## [2.39.0] – 2026-11-26 – Einstellungen sichern und Überwachung im Blick behalten

**Einstellungen lassen sich jetzt sichern und wiederherstellen, Serverkarten zeigen alle aktiven Überwachungen, und wichtige Daten sind besser vor Verlust geschützt.**

### Neu
- Unter **Einstellungen → Sicherung** laden Sie Einstellungen, Skriptdefinitionen und hochgeladene Skripte als ZIP-Datei herunter. Vor dem Wiedereinspielen zeigt eine Vorschau alle Unterschiede, ohne etwas zu ändern. Erst nach Ihrer Bestätigung wird eingespielt; der vorherige Stand wird automatisch gesichert und kann heruntergeladen werden. Vorhandene Einzelsicherungen lassen sich dort ebenfalls herunterladen und wiederherstellen.
- Die Serverkarten zeigen für jede aktive Überwachung ein Badge mit Anzahl und Zustand. Ein Klick auf ein Badge öffnet den passenden Abschnitt in den Serverdetails; fehlende Messdaten werden neutral als „noch keine Daten“ angezeigt. Über das **Zahnrad** neben dem Gesamtstatus gelangen Sie direkt zur Konfiguration der Überwachungen. Dieselbe Badge-Leiste steht auch oben in den Serverdetails.

### Behoben
- Der **Assistent für die Datei-/Protokollüberwachung** erscheint wieder im Vordergrund und lässt sich bedienen. Dialoge werden nicht mehr von einer Glas-Karte oder ihrer Abdunklung verdeckt.
- Ein beschädigter oder vorübergehend nicht lesbarer **Skriptverlauf** wird nicht mehr als leer behandelt und überschrieben. Die bisherige Datei bleibt erhalten, wird zusätzlich gesichert und eine Warnung erscheint. Neue Läufe und ihre Protokolle bleiben erhalten.
- **Einstellungen und Skriptdefinitionen** werden vor jedem Speichern automatisch gesichert. Auch Angaben aus neueren Versionen, die diese Version nicht kennt, bleiben beim Bearbeiten erhalten, statt verloren zu gehen.

### Hinweise
- Legen Sie den **Datenordner außerhalb des Programmordners** an, damit Einstellungen, Skripte und Verlauf bei einem Update nicht mit den Programmdateien ersetzt werden. SimpleAdmin warnt beim Start und in den Einstellungen, wenn Daten im Programmordner liegen. Kopieren Sie vorhandene Daten vor einem Umzug; sie werden nicht automatisch verschoben.
- Sicherungen können vertrauliche Zugangsdaten enthalten und müssen geschützt aufbewahrt werden. Der Schlüssel für verschlüsselte Werte ist nicht Teil der Sicherung und wird zum späteren Verwenden dieser Werte benötigt.

### Entwickler
- Für produktive WebUI-Hosts wird `ConfigPath` außerhalb des Anwendungsverzeichnisses empfohlen, beispielsweise `C:\ProgramData\SimpleAdmin`; vorhandene Dateien sind vor der Umstellung manuell zu kopieren. Die Startwarnung prüft auch `ScriptRunner:RunLogDirectory`, `ScriptRunner:UploadDirectory` sowie die Audit-Verzeichnisse für `ScheduledTasks` und `BizTalk`. Relative Datenpfade und Pfade unter `AppContext.BaseDirectory` oder `ContentRootPath` gelten als unsicher.
- Alle Config-Modelle erben `ConfigModel` mit `[JsonExtensionData]`; Replacement-Updates übernehmen unbekannte Felder rekursiv und gleichen listenbasierte Modelle über stabile Schlüssel ab. Vor dem atomaren Ersetzen entstehen bis zu 20 rotierende Einzelsicherungen unter `<ConfigPath>/backup/config.json.<UTC>` beziehungsweise `<ConfigPath>/backup/scripts.json.<UTC>`. Ein nicht lesbarer Skriptverlauf bleibt unverändert und wird als `script-runs/history.json.unreadable-<UTC>` kopiert; Schreibvorgänge erfolgen nur nach erfolgreichem Lesen und Sperren.
- Die Sicherungsseite `/Settings/Backup` exportiert `config.json`, `scripts.json`, erlaubte flache Upload-Dateien und ein Manifest mit SHA-256-Prüfsummen. Vorschau und Restore validieren Archive und Inhalte erneut; vor jedem Restore ist eine vollständige ZIP-Sicherung des aktuellen Stands unter `<ConfigPath>/backup/` Pflicht. Die Host-Schalter `Backup:RestoreEnabled` (Standard `true`) und `Backup:AllowWithoutAuthentication` (Standard `false`) stehen ausschließlich in `appsettings.json`. Datei-Audit erfolgt in `settings-backup-actions.json`, zusätzliche nicht blockierende Windows-Ereignisse verwenden die IDs 3200–3202.
- `ServiceConfigValidator` kapselt die Worker-Validierung gemeinsam für Worker und Restore. Restore ersetzt die validierten Originalbytes atomar, lädt Konfiguration und Skriptdefinitionen neu und invalidiert den Statuscache; unbekannte Felder und JSON-Kommentare bleiben dabei erhalten.

### Intern
- Die Badge-Ermittlung ist in `MonitoringBadges` zentralisiert; Übersicht und Detailkopf verwenden gemeinsam `_MonitoringBadges.cshtml`. `site.js` hebt betroffene Modale beim Öffnen aus Containing-Blocks in den `body` und stellt ihre ursprüngliche DOM-Position beim Schließen wieder her.
- Regressionstests decken Badge-Gates und -Zustände, Config-Roundtrips, Sicherungsrotation, unlesbaren und gesperrten Skriptverlauf, Datenordner-Warnungen sowie Backup-Export, -Vorschau, -Validierung, -Restore und -Audit ab. Die Entwicklungsberichte enthalten zusätzlich Browsernachweise für Serverkarten, Dialoge und Sicherungsabläufe in mehreren Breiten und beiden Farbschemata.

## [2.38.1] – 2026-11-19 – Technische Korrekturen

### Entwickler
- Das extrahierte WebUI-Projekt erzwingt den Razor-Source-Generator auch bei abweichenden Host-Einstellungen in `Directory.Build.props`/`.targets`, Umgebungsvariablen oder globalem `-p:UseRazorSourceGenerator=false`. Views bleiben dadurch mit den benötigten internen Typen in derselben Assembly; die öffentliche API wird nicht erweitert.

## [2.38.0] – 2026-11-19 – BizTalk-Instanzen prüfen, fortsetzen und beenden

**Suspendierte BizTalk-Instanzen lassen sich jetzt direkt ansehen und nach Bestätigung einzeln fortsetzen oder endgültig beenden; Aktionen werden protokolliert.**

### Neu
- Aus den Serverdetails und aus suspendierten Problemen öffnet „Instanzen prüfen“ eine frisch abgefragte Liste. Sie zeigt Anwendung, Zustand, Fehler und Zeitpunkt der Suspendierung; Filter und weitere Ergebnisse helfen bei der Suche. Begrenzte Ausschnitte und fehlende Angaben werden gekennzeichnet.
- Einzelne Instanzen lassen sich nach Bestätigung fortsetzen oder beenden. „Fortsetzen“ ist nur bei dafür geeigneten Instanzen verfügbar; laufende Änderungen sperren weitere Aktionen. Anschließend wird die Liste neu abgefragt.
- Der Verlauf zeigt, wer wann welche Instanz bearbeitet hat und mit welchem Ergebnis. Filter nach Server und Instanz finden auch ältere Vorgänge zu inzwischen entfernten Servern.

### Sicherheit
- Jede Aktion wird vorab geprüft und protokolliert. Kann das notwendige Protokoll nicht sicher geschrieben werden, wird die Aktion nicht gestartet. Fehler und ein unklarer Ausgang werden ausdrücklich angezeigt.

### Hinweise
- **Beenden ist endgültig:** Die betroffene Nachricht geht verloren. Den Bestätigungsdialog deshalb sorgfältig prüfen.
- Eine angenommene Aktion bedeutet noch nicht, dass die Nachrichtenverarbeitung abgeschlossen ist. Bei unklarem Ausgang zuerst den aktuellen Zustand prüfen; die Aktion wird nicht automatisch wiederholt.
- Zeitangaben der Instanzen und des Verlaufs zeigen die Zeitzone der Oberfläche mit UTC-Abweichung.

### Entwickler
- Neue WebUI-Seite `/BizTalk/Instances` und Endpunkte `GET /api/biztalk/instances/live`, `POST /api/biztalk/instances/action` sowie `GET /api/biztalk/instances/audit`. Einzelaktionen `Resume` und `Terminate` benötigen ein Antiforgery-Token im Header `RequestVerificationToken`; globale Anmeldung und `Authentication:RequiredRole` gelten weiterhin. Keine zusätzliche BizTalk-Rolle, keine Sammelaktionen, kein WMI und kein automatischer Aktionsretry.
- Optionen ausschließlich im WebUI-Host unter `BizTalk` in `appsettings.json`: `ControlEnabled=true`, `AllowWithoutAuthentication=false`, `QueryTimeoutSeconds=30` (5–120), `ActionTimeoutSeconds=60` (5–600), `MaxInstances=1000` (1–5000), `MaxResponseBytes=8388608` (1–8388608), `AllowedApiHosts` als Zuordnung von kanonischer Server-ID zu API-Host, `AuditLogDirectory` (Standard: ConfigPath-Verzeichnis), `AuditHistorySize=2000` (mindestens 10 Vorgänge). Ohne Anmeldung sind Live-Zugriff und Aktionen standardmäßig gesperrt. Zielserver, Feature und Überwachung müssen freigegeben sein; andere API-Hosts benötigen die explizite Hostfreigabe.
- Live-Abfragen sind vor der Filterung begrenzt. Die Management-REST-API wird unter dem WebUI-Dienstkonto angesprochen, ohne Benutzer-Impersonation, Redirects oder Umgehung der TLS-Prüfung. Das Konto benötigt Netz-/API-Leserechte und PUT-Rechte sowie Schreibrechte im Audit-Verzeichnis; die Rechte des Worker-Kontos reichen dafür nicht aus.
- Pflicht-Datei-Audit in `biztalk-instance-actions.json`: Requested wird vor der Mutation dauerhaft geschrieben, Completed hält das Ergebnis fest. Fehlende Abschlussprotokollierung bleibt sichtbar und sperrt weitere Mutationen bis zum Nachschreiben. Offene Vorgänge bleiben erhalten; ältere abgeschlossene Vorgänge werden dauerhaft in `biztalk-instance-actions.archive-<SHA256>.json` archiviert. Aktive Datei und Archive gemeinsam sichern und durch Verzeichnisrechte schützen. Vor neuen Mutationen werden alle Archive auf Hash und gültiges JSON geprüft; beschädigte Nachweise bleiben erhalten und sperren neue Mutationen. Speicherbedarf und Prüfaufwand wachsen mit der Historie. Keine gemeinsame Transaktion zwischen Datei und BizTalk.
- Zusätzliches Windows-Application-Protokoll über Quelle `Minicon.SimpleAdmin.WebUI`, ersatzweise vorhandene, korrekt zugeordnete und beschreibbare Quelle `SimpleAdmin`: Ereignisse 3100 (Accepted), 3101 (Rejected/Unknown), 3102 (Failed), korreliert über OperationId und Instanz-GUID. Quelle und Rechte bei der Installation vorbereiten; die Laufzeit legt keine Quelle an. Eine begrenzte Hintergrundwarteschlange entkoppelt Windows-Zugriffe. Ausfälle beeinträchtigen das Pflicht-Datei-Audit nicht und lösen keine erneute BizTalk-Aktion aus; außerhalb von Windows bleibt es beim Datei-Audit.
- Management REST wurde mit BizTalk Server 2016 FP1 eingeführt; die aktuelle Microsoft-Installationsanleitung nennt FP2 oder neuer. Ab 2020 ManagementService und OperationalDataService unterscheiden und die Management-Service-Basis verwenden. Konkrete Kunden-Version, neue GET-/PUT-Routen, Antworten und Statuswerte sowie Netzweg, TLS, AD-Rechte und echte Windows-Ereignisse müssen vor Kundenfreigabe geprüft werden. Resume nur an freigegebener Testinstanz, Terminate nur an freigegebener Wegwerf-Nachricht erproben.

### Intern
- Gemeinsamer atomarer Datei-Audit-Kern für BizTalk und geplante Tasks; bestehendes Task-Dateiformat und dessen Fehler-/Lesepolitik bleiben erhalten. Keine zusätzlichen Windows-Ereignisse für geplante Tasks.
- Regressionen für Live-Abfragen, Zielprüfung, Anmeldung, Antiforgery, Einzelaktionen, Archivintegrität, Audit-Aufbewahrung, Windows-Ereignisausfälle und Zeitanzeige; lokale Browserprüfungen für Einstiege, Filter, Bestätigungen und Verlauf bei mehreren Breiten und beiden Farbschemata. Reale Kunden- und Windows-Nachweise sowie eine manuelle Screenreader-Abnahme bleiben gesondert offen.

## [2.37.1] – 2026-11-10 – Technische Korrekturen

### Entwickler
- Die Quellcode- und Testextraktion akzeptiert jetzt Leerraum um die Werte von `SimpleAdminExtractSources` und `SimpleAdminExtractTests`.
- Bei eingeschalteter Testextraktion nennt der Host-Build fehlende Voraussetzungen und konkrete Abhilfe: deaktivierte Quellcode-Extraktion, fehlende Worker-Testquellen oder fehlende WebUI-Quellen. Die Erfolgsmeldung nennt den vollständigen Pfad der Testprojektdatei.
- Diagnosemeldungen erscheinen höchstens einmal je Projekt-Build; unveränderte Folgebuilds bleiben ruhig. Explizit gemeinsam konfigurierte Quellordner verursachen beim ersten Build keine falsche Warnung wegen der noch ausstehenden WebUI-Extraktion.
- README und Integrationshinweise erläutern Paketversion, Worker-Opt-in, Build-Assets, Restore und den gemeinsamen Quellordner für ausführbare extrahierte Tests.

## [2.37.0] – 2026-11-05 – Geplante Tasks überwachen und steuern

**SimpleAdmin überwacht jetzt geplante Windows-Tasks, warnt, wenn eine Task auf mehreren oder auf keinem Server aktiv ist, und schaltet Tasks auf Knopfdruck.**

### Neu
- **Seite „Geplante Tasks“** im Menü: Für jede Umgebung eine Übersicht aller überwachten Tasks und Server mit Zustand, letztem und nächstem Lauf und dem Ergebnis des letzten Laufs. Filter nach Umgebung, Text und „Nur Probleme“ bleiben in der Adresse erhalten.
- **Nur auf einem Server aktiv**: Darf eine Task in einer Umgebung nur auf genau einem Server laufen, meldet SimpleAdmin ein Problem, wenn sie auf mehreren Servern aktiv ist, und ebenso, wenn sie auf keinem aktiv ist. Liefert ein Server gerade keine Daten, erscheint er als „unbekannt“; solange wird „auf keinem aktiv“ nicht gemeldet, weil die Task dort aktiv sein könnte. Tasks, die auf allen Servern laufen dürfen, werden nur einzeln geprüft.
- **Fehlgeschlagene Läufe**: Endet eine Task mit einem Fehlercode, wird das als Problem gemeldet, mit dem Fehlercode und – soweit bekannt – seiner Bedeutung. Bekannte harmlose Ergebnisse (z. B. „vom Benutzer beendet“) zählen nicht als Fehler.
- **Zu lange Laufzeit**: Läuft eine Task länger als erlaubt (Standard 2 Stunden), erscheint eine Warnung.
- **Verpasste Läufe**: Wurde ein geplanter Lauf nicht ausgeführt, erscheint eine Warnung – nicht, solange die Task gerade läuft oder der Zeitpunkt nur knapp überschritten ist. Die Markierung verschwindet, sobald die Task wieder gelaufen ist, spätestens nach 24 Stunden.
- **Fehlerereignisse der Aufgabenplanung** (z. B. „Start fehlgeschlagen“, „wegen Zeitüberschreitung beendet“) werden gezählt; ein Lauf zählt dabei nur einmal. Ist der Aufgabenverlauf auf einem Server abgeschaltet, weist SimpleAdmin darauf hin („Aufgabenverlauf ist deaktiviert“).
- **Ignorieren**: Harmlose Ereignisse lassen sich in den Serverdetails mit „Ignorieren“ ausblenden – für 1, 7, 30 oder 90 Tage, bis zu einem Datum oder dauerhaft, immer mit Begründung. Die Regel wirkt sofort und lässt sich unter Einstellungen → Feature-Toggles → Geplante Tasks bearbeiten.
- **Steuern mit einem Klick**: Angemeldete Benutzer können eine Task direkt aus der Übersicht aktivieren, deaktivieren, starten oder eine laufende Ausführung beenden. Jede Aktion wird vorher bestätigt, danach zeigt die Zelle den aktuellen Stand direkt vom Server.
- **„Hier exklusiv aktiv“**: Schaltet eine Task auf dem gewählten Server ein und auf allen anderen Servern der Umgebung aus. Schlägt das Ausschalten irgendwo fehl, bleibt der gewählte Server unverändert, damit die Task nie doppelt läuft.
- **Verlauf der Aktionen** unten auf der Seite: wer wann welche Task auf welchem Server geschaltet hat, mit Ergebnis.
- **Serverdetails**: neuer Abschnitt „Geplante Tasks“ mit allen Tasks des Servers, ihren Befunden und den letzten Fehlerereignissen. Auch die statischen Statusseiten zeigen die Tasks und das Ergebnis der Prüfung „nur auf einem Server aktiv“.
- Einrichtung pro Server unter Server → Bearbeiten → „Geplante Tasks“ über Task-Muster (z. B. `\Minicon\*`) mit Ausschlüssen; die Hilfe-Seite hat ein neues Kapitel dazu.

### Sicherheit
- Links zu Anleitungen aus der Konfiguration werden nur noch als Link angezeigt, wenn sie auf eine Webseite oder E-Mail-Adresse zeigen. Andere Angaben (z. B. Dateipfade) erscheinen als Text „Anleitung: …“ zum manuellen Öffnen.

### Hinweise
- Ist die Anmeldung an der Oberfläche abgeschaltet, ist das Steuern geplanter Tasks gesperrt; die Seite nennt dann den Grund. Die Anzeige bleibt verfügbar.

### Entwickler
- Konfiguration (`config.json`): `features.scheduledTasks { enabled, checkIntervalSeconds, defaults { maxRuntimeMinutes (120, 0 = aus), missedRunGraceMinutes (30), allowedResultCodes, eventTimeWindowMinutes (60), eventWarningCount (1), eventCriticalCount (5), maxReportedEvents (50) }, ignoreRules[] { taskPath, eventIds, messageContains, servers, reason, createdBy, expiresAt } }`; pro Server `checks.scheduledTasks { enabled, checkEvents, tasks[] { pattern, excludePatterns, mode: SingleActive|AllServers, maxRuntimeMinutes, missedRunGraceMinutes, checkMissedRun, allowedResultCodes, failureIsCritical, wikiUrl, remediationSteps } }`. Global und pro Server standardmäßig aus.
- Auswahl: Wildcards `*`/`?` auf dem vollen Pfad; das erste Muster, dessen Include passt, entscheidet, sein Exclude gewinnt (spätere Muster werden nicht mehr geprüft). Tasks unter `\Microsoft\` nur mit einem Muster, das selbst mit `\Microsoft\` beginnt. Ein Muster ohne Treffer wird als Problem gemeldet.
- Zentrale Gruppenprüfung (WebUI, Notifier, HTML-Generator gleich): Gruppe = gleicher Taskpfad je Umgebung, Server in mehreren Umgebungen nehmen in jeder teil, ohne Umgebung = `""`. Ein Server gilt als unbekannt bei fehlendem Status, fehlgeschlagener Abfrage oder Status älter als max(`notifications.staleThresholdMinutes`, 3 × max(`general.checkIntervalSeconds`, `scheduledTasks.checkIntervalSeconds`) + 240 s) – `general.checkIntervalSeconds` muss dem echten Worker-Takt entsprechen. Gruppenprobleme hängen am alphabetisch ersten Teilnehmer der Umgebung laut config.json (dort quittieren); ihr „seit“ steht in `schtask-groups-since.json` (WebUI: ConfigPath, Notifier: neben `notify-state.json`).
- WebUI-Host (`appsettings.json`, Abschnitt `ScheduledTasks`): `ControlEnabled` (true), `AllowWithoutAuthentication` (false; bei `Authentication:Enabled=false` ist die Steuerung sonst aus), `PowerShellPath`, `ActionTimeoutSeconds` (60, 5…600), `AuditLogDirectory` (Standard: ConfigPath), `AuditHistorySize` (2000). Audit-Log `scheduled-task-actions.json`. Jeder angemeldete Benutzer darf steuern; der Start-Log nennt Zustand und ausführendes Konto.
- Rechte: Der Worker braucht lokalen Admin bzw. SYSTEM (sonst fehlen Tasks stillschweigend) und mindestens „Event Log Readers“ für `Microsoft-Windows-TaskScheduler/Operational`; der Aufgabenverlauf muss aktiviert sein. Die WebUI steuert direkt per PowerShell + CIM über DCOM (kein WinRM): Ihr Dienstkonto braucht auf den Zielservern RPC/DCOM (Firewall-Gruppen „Remote Scheduled Tasks Management“ und WMI), DCOM-/WMI-Remotezugriff und lokalen Admin bzw. Vollzugriff auf den Aufgabenordner. Kein automatisches Failover.
- REST: `GET /api/scheduledtasks`, `GET /api/scheduledtasks/live`, `POST /api/scheduledtasks/action`, `POST /api/scheduledtasks/activate-exclusive`, `GET /api/scheduledtasks/audit`, `POST /api/scheduledtasksignore` (POSTs nur mit JSON-Body; live/action/exclusive liefern 403, solange die Steuerung aus ist).
- `RuntimeStatus.ScheduledTasks` (`ScheduledTaskState`) ist im HTTP-Status-JSON enthalten; neue Problem-IDs `schtask_*`. Logger-Kategorie `Minicon.SimpleAdmin.Checkers.ScheduledTasks`.
- WebUI: Wiki-/Anleitungs-Links laufen über `Display.SafeHref` (nur http(s), `mailto:` und relative Pfade); betrifft Probleme-Seite, Serverdetails und Dateiüberwachung.

### Intern
- Neue Bausteine: `ScheduledTaskChecker` (Checkers), `ScheduledTaskGroupEvaluator`, `ScheduledTaskIgnoreMatcher`, `ScheduledTaskGroupSinceStore`, `ScheduledTaskPaths` (Shared), `ScheduledTaskControlService`, `ScheduledTaskAuditLog`, `ScheduledTaskTargets`, `KeyedAsyncLock`, `ScheduledTaskProcessRunner` (WebUI).
- Tests für Checker-Auswertung, Gruppenprüfung, Problemableitung, Notifier, HTML, Steuerung, REST-API und Einstellungen.

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


[2.39.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.38.1...v2.39.0
[2.38.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.38.0...v2.38.1
[2.38.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.37.1...v2.38.0
[2.37.1]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.37.0...v2.37.1
[2.37.0]: https://github.com/Minicon-eG/Minicon.SimpleAdmin/compare/v2.36.1...v2.37.0
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

