# Minicon SimpleAdmin

A monitoring platform for PRTG and NetScaler environments, distributed as a set of
reusable NuGet packages. It consists of a **console worker** that collects system
metrics and generates static HTML status pages, and an **ASP.NET Core admin UI**
for configuration management, real-time status viewing and problem acknowledgement.

**Key design philosophy:** maximum availability through static file generation.
The generated HTML pages have no dynamic dependencies and remain accessible even
if workers, databases or external systems are down (as long as the web server is
operational).

## Packages

| Package | Purpose |
|---|---|
| `Minicon.SimpleAdmin.Shared` | Core models (Config/State/Status), `AcknowledgeService`, `ProblemDerivationService`, `ConnectionStringEncryption`, `StateFilesLock` |
| `Minicon.SimpleAdmin.Services` | Platform metrics abstraction (Windows/Linux/macOS), `MetricsCollector`, `StatusEvaluator`, `RuntimeStatusStore` |
| `Minicon.SimpleAdmin.Checkers` | AppPool, Windows service, event log, certificate, SQL, email, BizTalk and file/log checkers + the central e-mail notifier (`NotificationService`) |
| `Minicon.SimpleAdmin.HtmlGenerator` | `StaticHtmlGenerator` — renders the static status pages |
| `Minicon.SimpleAdmin.Worker` | Console worker host (`SimpleAdminWorkerHost`) that wires everything together |
| `Minicon.SimpleAdmin.WebUI` | Razor Class Library with the complete admin UI (controllers, views, REST API, services) |

## Consuming the packages

The entire monitoring logic lives in these packages. A host application only needs
a few lines of bootstrapping plus its own configuration files.

### Console worker

```csharp
using Minicon.SimpleAdmin.Worker;

// [configPath] [statusDir] [wwwrootDir] [--force-probe]
return await SimpleAdminWorkerHost.RunAsync(args);
```

```xml
<PackageReference Include="Minicon.SimpleAdmin.Worker" Version="2.36.0" />
```

### Admin UI

```csharp
using Minicon.SimpleAdmin.WebUI;

return await SimpleAdminWebUIHost.RunAsync(args);
```

```xml
<PackageReference Include="Minicon.SimpleAdmin.WebUI" Version="2.36.0" />
```

`SimpleAdminWebUIHost.RunAsync` handles the regular WebApplication build and, when
the first argument is `install`, `uninstall`, `start` or `stop`, dispatches to the
Windows-service installer (see "Installation without IIS" below). A custom host can
still use the long form:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSimpleAdminWebUI(builder.Configuration);
var app = builder.Build();
app.UseSimpleAdminWebUI();
app.Run();
```

### Installation without IIS (Windows service)

The WebUI ships with a one-click installer so it can run as a plain Windows
service — no IIS required.

```cmd
rem Als Administrator ausführen
install.cmd                                  rem Default: Port 5080, LocalSystem
install.cmd --port=5080 --account=CORP\svc-sa  rem Dienstkonto, Passwort-Prompt
<EigeneEXE>.exe uninstall                    rem Dienst + URL-ACL + Firewall entfernen
```

`install` legt den Dienst `SimpleAdminWebUI` an (`sc.exe create binPath=<EigeneEXE>`,
Autostart), reserviert die URL-ACL (`netsh http add urlacl url=http://+:<Port>/
user=<Konto>`), öffnet die Firewall (`netsh advfirewall firewall add rule`), erstellt
`C:\ProgramData\SimpleAdmin\dp-keys` und erteilt dem Dienstkonto per `icacls` dort
Modify-Rechte (`(OI)(CI)M`) und startet den Dienst. Alle Schritte sind idempotent.
Flags: `--port=5080`, `--account=…`, `--password=…`, `--service-name=SimpleAdminWebUI`,
`--display-name=…`, `--description=…`, `--keys-dir=…`, `--non-interactive`, `--purge`
(nur `uninstall`, löscht den Keys-Ordner mit).

Im Windows-Dienst-Modus hostet die Anwendung über **HttpSys**, sodass Negotiate
weiterhin ohne IIS funktioniert. Interaktive Starts und IIS-Deployments bleiben
auf Kestrel bzw. In-Process — kein Eingriff ins bestehende IIS-Setup nötig.

### DataProtection keys

Die WebUI persistiert ihre DataProtection-Keys per Default unter
`C:\ProgramData\SimpleAdmin\dp-keys` und verschlüsselt sie mit **DPAPI im
LocalMachine-Scope** — damit sind sie unabhängig vom Benutzerprofil des
AppPools/Dienstkontos (behebt den `CryptographicException 0xe0434352`-Startabsturz
nach AppPool-Recycle). Overrides: `DataProtection:KeysDirectory` (anderer Pfad)
oder `DataProtection:Enabled = false` (zurück zum Default-Verhalten des Frameworks).

The host's `appsettings.json` supplies deployment specifics:

```json
{
  "ConfigPath": "C:\\path\\to\\config-directory",
  "StatusDirectory": "C:\\path\\to\\status",
  "BasePath": "/SimpleAdmin",
  "Authentication": {
    "Enabled": true,
    "RequiredRole": "DOMAIN\\SimpleAdmin-Admins"
  }
}
```

## Betrieb: Datenordner, Sicherungen und Wiederherstellung

**Legen Sie den Datenordner außerhalb des WebUI-Anwendungsordners fest.** Bei leerem
`ConfigPath` liegen `config.json`, `scripts.json`, Uploads, Skriptverlauf und standardmäßig
auch die Audit-Dateien im Anwendungsordner und können durch ein Deployment gefährdet sein.
Empfohlen ist beispielsweise:

```json
{
  "ConfigPath": "C:\\ProgramData\\SimpleAdmin"
}
```

Die WebUI warnt beim Start und auf der Einstellungsseite, wenn ConfigPath-, RunLog-,
Upload- oder Audit-Verzeichnisse innerhalb von `AppContext.BaseDirectory` oder
`ContentRootPath` liegen. Ein relativer `ConfigPath` gilt unabhängig vom aufgelösten Pfad
ebenfalls als unsicher. Individuell gesetzte `ScriptRunner:RunLogDirectory`,
`ScriptRunner:UploadDirectory`, `ScheduledTasks:AuditLogDirectory` und
`BizTalk:AuditLogDirectory` müssen ebenfalls auf stabile absolute Pfade außerhalb der
Anwendung zeigen. Das Dienstkonto der WebUI benötigt dort Lese- und Schreibrechte.

**Vor der Umstellung kopieren Sie die vorhandenen Daten selbst**; es gibt keine automatische
Migration. Beenden Sie dafür die WebUI und kopieren Sie mindestens `config.json`,
`scripts.json`, `scripts/`, `script-runs/`, `metadata/` sowie vorhandene
`*-actions*.json`-Auditdateien und `schtask-groups-since.json` in den neuen Datenordner.
Erst danach ändern Sie `appsettings.json` und starten die WebUI wieder. Prüfen Sie Domains,
Skriptdefinitionen, Verlauf und offene Audit-Vorgänge. Ein Deployment-Paket darf diese
Laufzeitdaten nicht enthalten.

### Automatische Sicherungen und Download über die WebUI

- Vor jedem Speichern wird eine vorhandene `config.json` nach
  `<ConfigPath>/backup/config.json.<UTC-Zeitstempel>` kopiert. Entsprechend entsteht für
  Skriptdefinitionen `<ConfigPath>/backup/scripts.json.<UTC-Zeitstempel>`.
- Pro JSON-Datei bleiben die letzten 20 F4-Sicherungen erhalten. Namenskollisionen innerhalb eines
  Zeitstempels erhalten einen numerischen Zusatz. Ein Fehler beim Sichern oder Rotieren wird als
  Warning protokolliert und verhindert das normale Speichern **nicht**.
- Unter **Einstellungen → Sicherung** lädt jeder angemeldete Benutzer eine ZIP-Datei
  `simpleadmin-einstellungen-<Hostname>-<yyyyMMdd-HHmmss>.zip` herunter. Sie enthält nur
  `config.json`, `scripts.json`, die Dateien des `ScriptRunner:UploadDirectory` unter `scripts/`
  und `manifest.json`. Das Manifest (Format-Version 1) enthält SimpleAdmin-Version, UTC-Erstellzeit,
  Hostname, Identitätsnamen sowie Größe und SHA-256 jeder Payload-Datei.
- **Nicht enthalten** sind Status, Verläufe, Skriptläufe, Quittierungen und Auditdateien. Es gibt
  keine zeitgesteuerten Exporte an fremde Orte und kein Worker-Backup.
- Die Seite listet die F4-Einzelsicherungen und die vollständigen Sicherungen vor einem Restore.
  F4-Einzelsicherungen werden beim Download mit einem Manifest als ZIP verpackt und stellen nur
  ihre jeweilige JSON-Datei wieder her.
- Eine unlesbare `script-runs/history.json` wird einmalig je Dateiversion als
  `script-runs/history.json.unreadable-<UTC-Zeitstempel>` kopiert. Das Original bleibt
  unverändert. Unbekannte Statuswerte werden als `Unknown` angezeigt und blockieren das
  Speichern nicht. Lese-, Deserialisierungs- und Sperrfehler blockieren das Überschreiben
  und werden auf der Skriptseite gemeldet. Logdateien werden bei einem Persistenzfehler
  nicht gelöscht.

**Konfigurieren Sie `Encryption:ConnectionStringKey` verbindlich**, sobald Verbindungszeichenfolgen
oder SMTP-/IMAP-Kennwörter verwendet werden. Ohne Schlüssel stehen diese Werte in `config.json`
und dadurch auch in bis zu 20 F4-Sicherungen im Klartext. Auch eine ZIP-Sicherung kann solche
Klartextwerte enthalten. Backup-Verzeichnis und Downloads sind zu schützen; beziehen Sie den
Datenordner zusätzlich in eine unabhängige externe Datensicherung ein.

Unbekannte Felder in `config.json` bleiben beim Laden und Speichern durch die WebUI im Root
und in den verschachtelten Config-Modellen erhalten. Auch die CRUD-/Settings-Setter übernehmen
diese Felder aus dem geladenen Objekt und dessen verschachtelten Config-Modellen, bevor sie es
ersetzen. Dictionaries werden per Schlüssel abgeglichen. Server, ServiceTypes und Domains in
Listen werden anhand ihrer eindeutigen Namen beziehungsweise Typen abgeglichen; nur Listen ohne
solchen Schlüssel verwenden bei unveränderter Elementanzahl die Position. Dadurch wandern
unbekannte Felder nach Einfügen, Löschen oder Umsortieren nicht zu einem anderen Eintrag.
`ServiceConfigReader` liest die Datei im Worker nur und schreibt sie nicht zurück. Worker und
WebUI-Restore verwenden dieselbe `ServiceConfigValidator`-Prüfung.

### Wiederherstellung über die WebUI

1. Unter **Einstellungen → Sicherung** die ZIP-Datei (max. 50 MB) oder eine automatische
   Sicherung auswählen. Die Vorschau ändert keine Einstellungen. Sie zeigt Manifest,
   Versionswarnung, Unterschiede bei Servern, Umgebungen, Domains, Skriptdefinitionen und
   Skriptdateien, die Namen nicht enthaltener Upload-Skripte sowie das Ergebnis der
   Entschlüsselungsprüfung.
2. Warnungen prüfen. Eine Sicherung mit `enc:v1`-Werten ist später nur mit demselben
   `Encryption:ConnectionStringKey` lesbar. Nicht entschlüsselbare Werte werden angezeigt,
   blockieren das Einspielen aber nicht. Der Schlüssel steht weder im ZIP noch im Manifest.
3. **Jetzt einspielen** bestätigen. Vorher muss eine vollständige ZIP-Sicherung des aktuellen
   Stands unter `<ConfigPath>/backup/` erstellt werden; schlägt dies fehl, bleibt alles unverändert.
   Danach ersetzen die vorhandenen Stores die enthaltenen JSON-Dateien jeweils atomar und lesen
   sie erneut ein. Der Statuscache wird verworfen. Bei einer Vollsicherung werden enthaltene
   Uploads ersetzt. Nicht enthaltene `.ps1`-, `.psm1`-, `.psd1`- und `.txt`-Dateien bleiben
   standardmäßig erhalten; nur die in der Vorschau namentlich aufgeführten Dateien werden nach
   ausdrücklicher Bestätigung per Kontrollkästchen entfernt. Andere Pfade und Dateien werden
   nicht angerührt.
4. Ergebnis und Link **Vorherigen Stand herunterladen** kontrollieren. Nach einem Schreibfehler
   versucht die WebUI, den vorher gesicherten Stand erneut herzustellen; dies ist keine
   verteilte Transaktion über mehrere Dateien oder Prozesse. Das Ergebnis meldet ausdrücklich,
   falls auch diese Rücknahme fehlschlägt.

Vor dem Restore werden Pfad-Traversal/Zip-Slip, absolute und unbekannte Einträge, doppelte
Einträge, nicht flache oder unerlaubte Skriptdateien, reservierte Windows-Gerätenamen,
Steuer-/Bidi-Zeichen, führende/abschließende Leerzeichen/Punkte, Größen-/Anzahlgrenzen,
Manifest und alle SHA-256-Werte geprüft. `config.json` muss deserialisierbar sein und die
Worker-Validierung bestehen; `scripts.json` muss zum Store-Schema passen. Ungültige Uploads
verändern nichts. Die Entpackgrenzen sind 1.024 ZIP-Einträge, 100 MB insgesamt und 16 MB je
JSON-/Skriptdatei. Downloads, Vorschau und Restore verlangen POST mit Antiforgery-Token.
Der Benutzername kommt aus der Identität, niemals aus einem Formularfeld. Vorschau-Tokens
sind an den hochladenden Benutzer gebunden; fremde oder gesperrte Versuche verbrauchen sie nicht.

Vorschau-ZIPs enthalten die vollständige Konfiguration und bleiben höchstens 15 Minuten unter
`<ConfigPath>/backup/.preview`. Abgelaufene Dateien werden minütlich, verwaiste Dateien beim
WebUI-Start gelöscht. Das Verzeichnis benötigt trotzdem restriktive Dateisystemrechte.

Host-Einstellungen ausschließlich in `appsettings.json`:

```json
{
  "Backup": {
    "RestoreEnabled": true,
    "AllowWithoutAuthentication": false
  }
}
```

`RestoreEnabled` ist standardmäßig `true`. Bei `Authentication:Enabled=false` ist Restore dennoch
gesperrt; `AllowWithoutAuthentication=true` gibt es nur für isolierte Hosts frei. Jeder
angemeldete Benutzer innerhalb der globalen WebUI-Rolle darf wiederherstellen. Downloads bleiben
auch bei deaktiviertem Restore möglich.

Downloads und Restore-Ergebnisse werden mit UTC, Identität, Dateiname, Manifest-SHA-256 und
Ergebnis in `<ConfigPath>/settings-backup-actions.json` gespeichert. Zusätzlich verwendet die
WebUI unter Windows denselben begrenzten, nicht blockierenden Application-Event-Kanal wie
BizTalk (IDs 3200 Information, 3201 Warning, 3202 Error; Quellen `Minicon.SimpleAdmin.WebUI`
oder vorhandene `SimpleAdmin`). Datei-Audit ist maßgeblich; Eventfehler blockieren nichts.

### Manuelle Wiederherstellung im Notfall

1. WebUI beenden und die aktuellen Dateien einschließlich `history.json` und allen
   `.unreadable-*`-Dateien zusätzlich zur Seite kopieren.
2. Eine fachlich passende Sicherung auswählen und deren JSON prüfen; die neueste Sicherung ist
   nicht automatisch die letzte fehlerfreie. Für den Skriptverlauf kann auch die
   `.unreadable-*`-Kopie die einzige vollständige Quelle sein.
3. `config.json` beziehungsweise `scripts.json` aus der gewählten Backup-Datei in den
   ConfigPath-Ordner zurückkopieren. Eine beschädigte History nur durch eine geprüfte Kopie als
   `script-runs/history.json` wiederherstellen; bei einer bloßen Sperre stattdessen den
   blockierenden Prozess oder die Zugriffsrechte beheben und die Originaldatei behalten.
4. Rechte für das WebUI-Dienstkonto prüfen, WebUI starten und Domains, Einstellungen,
   Skriptdefinitionen und Verlauf kontrollieren. Vorhandene `.log`-Dateien nicht löschen: Sie
   können die Ausgabe auch dann noch enthalten, wenn ein History-Eintrag rekonstruiert werden
   muss.

### Securing the admin UI

The admin UI protects **every** controller and REST endpoint with a global
authorization fallback policy that requires an authenticated Windows user
(Negotiate/Kerberos). This matters because endpoints such as
`/api/sqlquerytest` and `/api/emailtest` open outbound SQL/SMTP/IMAP
connections to caller-supplied hosts — they must never be reachable
anonymously.

- **Enabled by default** — no configuration needed for a Windows/AD host. Set
  `"Authentication:Enabled": false` only for isolated/dev hosts with no AD.
- **`RequiredRole`** (optional) restricts access to a single AD group.
- The worker and peer servers are **not** affected: they never call these
  endpoints — the only cross-process HTTP traffic reads the static
  `status/*.json` files, which are served *before* the auth middleware and
  therefore stay reachable.

### Getting the source code into your project

Every package also ships its complete source code. On build, a host project that
references the packages automatically extracts it to `SimpleAdmin.Sources/` (one
folder per package, including the `.csproj` files). The folder is excluded from the
host's compilation — the host keeps using the package DLLs until you switch to
`ProjectReference`s on the extracted projects.

```xml
<PropertyGroup>
  <!-- Optional: rename "Minicon.SimpleAdmin" to your own namespace while extracting
       (namespaces, project/file/folder names, view paths, logger categories). -->
  <SimpleAdminSourcesNamespace>Contoso.Monitoring</SimpleAdminSourcesNamespace>

  <!-- Optional: different target folder. -->
  <SimpleAdminSourcesDirectory>SimpleAdmin.Sources</SimpleAdminSourcesDirectory>

  <!-- Optional: also extract the test project (shipped in the Worker package). -->
  <SimpleAdminExtractTests>true</SimpleAdminExtractTests>
</PropertyGroup>
```

Das Testprojekt benötigt die Quellen aller Pakete einschließlich WebUI. Worker- und
WebUI-Host müssen für `dotnet test` denselben Ordner verwenden, beispielsweise in
einer gemeinsamen `Directory.Build.props` neben der Solution:

```xml
<Project>
  <PropertyGroup>
    <SimpleAdminExtractTests>true</SimpleAdminExtractTests>
    <SimpleAdminSourcesDirectory>$(MSBuildThisFileDirectory)SimpleAdmin.Sources</SimpleAdminSourcesDirectory>
  </PropertyGroup>
</Project>
```

**Fehlersuche: Testprojekt entsteht nicht**

- Nur `Minicon.SimpleAdmin.Worker` ab **2.36.0** liefert Testquellen. Tatsächlich
  aufgelöste Worker-Version prüfen, bei Bedarf anheben und Restore ausführen.
- `SimpleAdminExtractTests=true` gehört in den **Worker-Host** oder gemeinsame
  `Directory.Build.props`; nur im WebUI-Host genügt es nicht. Leerraum um `true` ist erlaubt.
- Asset-Filter (`ExcludeAssets`/`IncludeAssets`, besonders `buildTransitive`) am
  Paketverweis und `SimpleAdminExtractSources=false` prüfen. Entscheidend ist, ob
  `obj/*.nuget.g.targets` die Paket-Targets importiert; `ExcludeAssets="build"`
  allein schaltet `buildTransitive` nicht zuverlässig ab.
- Nur ein echter Build entpackt: Restore, Design-Time-Build und `publish --no-build`
  genügen nicht; nach einer Versionsanhebung zuerst Restore, kein direktes `--no-restore`.
- Ohne gemeinsamen Ordner liegt das Projekt relativ zum **Worker-Host-Projekt** unter
  `SimpleAdmin.Sources/Minicon.SimpleAdmin.Tests/`. `SimpleAdminSourcesDirectory`
  ändert den Ort, `SimpleAdminSourcesNamespace` den Namen zu `<Namespace>.Tests`.
  Die Erfolgsmeldung nennt den vollständigen Projektpfad. Erst nach dem Build beider
  Hosts im gemeinsamen Ordner `dotnet test <Pfad>/<Prefix>.Tests.csproj`
  (`<Prefix>` ist standardmäßig `Minicon.SimpleAdmin`, sonst der gesetzte Namespace) ausführen.

To switch extraction off, set `<SimpleAdminExtractSources>false</SimpleAdminExtractSources>`.

Extraction runs again only when the package version or the namespace changes — local
edits in the folder are then overwritten. `LICENSE` (MIT copyright notice) stays
unchanged and must be kept. When the namespace changes, delete the old folders by hand.

## Building locally

```bash
# Build everything
dotnet build Minicon.SimpleAdmin.slnx

# Run the tests
dotnet test src/Minicon.SimpleAdmin.Tests/Minicon.SimpleAdmin.Tests.csproj

# Produce NuGet packages into ./local-feed
dotnet build Minicon.SimpleAdmin.slnx -c Release
dotnet pack  Minicon.SimpleAdmin.slnx -c Release -o local-feed --no-build
```

To consume the locally built packages from another solution, add `local-feed` as a
NuGet source (e.g. in a `NuGet.config`):

```xml
<add key="minicon-local" value="../Minicon.SimpleAdmin/local-feed" />
```

## Configuration & features

The `config.json` consumed by the worker and admin UI defines environments, servers,
service types, feature toggles and the PRTG/NetScaler endpoints. Supported features:

- System metrics (CPU/memory/disk) with status smoothing to prevent alert fatigue
- IIS application-pool, Windows-service, event-log and certificate checks
- Event log **ignore rules** — suppress known noise long-term, scoped to all or
  specific servers (wildcards), with reason and optional expiry
- SQL query checks (row-count and column assertions)
- Email monitoring (SMTP probes + IMAP delivery checks)
- **BizTalk instance control** at `/BizTalk/Instances`: live suspended instances,
  application/error filters, single Resume/Terminate with confirmation, and durable
  file audit plus best-effort Windows Application events. Enter through server details,
  application groups or “Instanzen prüfen” on suspended problems. See the operator guide below.
- BizTalk monitoring (management API) and file/log monitoring (stuck files on shares,
  error signatures in logs) — with a **setup assistant** in the admin UI that tests
  paths and regex signatures against the real files before saving, offers templates
  and suggests common error signatures with their hit counts
- **Run-level log monitoring**: beyond regex signatures a log scan can declare
  *open runs* (a run that starts but never finishes — e.g. a log ending in
  `waiting for Result…` without a result line) and *expected runs* (a run that
  should happen on certain weekdays at a fixed time but is missing from the log
  entirely) — see the operator guide below
- **Central e-mail notifier**: designated servers aggregate all statuses over HTTP
  and send summary mails with acknowledge deep-links (HTML + plaintext, reminders,
  all-clear notices, HA via two coordinated instances) — testable end-to-end via
  the "Test-Mail senden" button in the admin UI
- Problem acknowledgement with expiry — single or multi-select ("Auswahl quittieren")
- **PowerShell script runner** (on by default, opt-out via `ScriptRunner:Enabled=false`; automatically off
  while the WebUI runs without authentication): upload or
  register `.ps1` scripts, parameters are detected automatically (date/time pickers,
  dropdowns, server-side file/folder chooser, switches, lists), live output, cancel and
  an audit history of every run
- Time profiles (relaxed thresholds outside business hours)
- Central aggregation across multiple servers
- AES-256 connection-string encryption (`Encryption:ConnectionStringKey`)

The admin UI ships a complete German documentation page (**/Documentation**)
covering every check type, the notifier, authentication and the REST API.

## License

MIT — see [LICENSE](LICENSE).

## BizTalk instance control — operator guide (REST)

Management REST was introduced with BizTalk Server 2016 Feature Pack 1; Microsoft's installation guide currently lists Feature Pack 2 or newer as prerequisite. For BizTalk Server 2020+, ManagementService and OperationalDataService are separate services. Configure the management-service base for these instance actions, not the separate OData service. Exact version/feature pack, routes and response/status contracts still require customer verification; this is not a blanket compatibility claim. [Microsoft REST installation](https://learn.microsoft.com/en-us/biztalk/core/install-and-configure-the-management-rest-apis-in-biztalk-server), [2020+ service configuration](https://learn.microsoft.com/en-us/biztalk/install-and-config-guides/configure-biztalk-server).

Set these in the WebUI host's `appsettings.json`, not `config.json`:

```json
{
  "BizTalk": {
    "ControlEnabled": true,
    "AllowWithoutAuthentication": false,
    "QueryTimeoutSeconds": 30,
    "ActionTimeoutSeconds": 60,
    "AuditHistorySize": 2000,
    "MaxInstances": 1000,
    "MaxResponseBytes": 8388608,
    "AllowedApiHosts": { "bt-config-id": "explicit-api-host" }
  }
}
```

Omit `AllowedApiHosts` when the API runs on the configured server. Loopback URLs in
server BizTalk checks are rewritten to that server, preserving port/path. Only
HTTP(S) without credentials/query/fragment is accepted; redirects are disabled,
TLS validation stays active, and HTTP uses the WebUI process credentials without
user impersonation. The global authentication/RequiredRole policy stays in force.
Without authentication, live IO and mutations require explicit opt-in on an isolated host.
Targets also require loaded config, the BizTalk feature, an active server and its
enabled BizTalk check. Query timeout is the smaller of the host limit (5–120 s)
and positive server `timeoutSeconds` (otherwise 30 s).

Scan limits (1–5000 entries, default 1000; at most 8 MiB) apply before filtering,
including active entries. Partial results show a warning and counts only within
the scanned range. Error text is capped at 32 KiB UTF-8; search cannot cover the
removed tail. Paging reads live again and may shift; refresh resets the offset.
Unknown applications have a separate filter. URI or ServiceType is labelled as
provider information, not a verified port name. Worker status sums remain unchanged.

Single-instance **Fortsetzen** (Resume) and **Beenden** (Terminate) use the management
REST API under the WebUI service account. Every signed-in user may act within the
global RequiredRole policy. Resume requires a fresh resumable state. Terminate asks
for explicit confirmation with server/application/GUID and warns that the message
is permanently discarded. No bulk actions, WMI, automatic retries or fallback.
Accepted means accepted for processing; live state is refreshed. A timeout after
possible submission is **Unknown**: check live state/audit before acting again.

Host options above are exclusively appsettings settings. Action timeout is 5–600 s;
`AuditHistorySize` bounds new open operations and the independent active completed-history window (minimum 10 each). Optional `AuditLogDirectory`
defaults to ConfigPath. Grant the WebUI service account management read/PUT rights
and audit-directory write access. With explicit auth-off opt-in the audit user is
`anonymous`. The JSON-only action POST additionally requires the Razor antiforgery
token header `RequestVerificationToken`. GUID and config targets are validated
under a process-local lock shared by Resume and Terminate.

The required `biztalk-instance-actions.json` records user, UTC time, process account,
configured server, GUID, provider application/mapping, action, outcome and OperationId.
Durable Requested precedes mutation. If it cannot be written, HTTP 503 prevents the
mutation. Completed write failure returns `AuditPersisted=false`; Requested remains
visibly unresolved, pending completion is retained in memory and further mutations
are blocked until that write can drain. Open Requested operations are never trimmed, even after reducing the limit;
full open capacity blocks only new mutations, never completions or rejection audits.
Closed history overflow is durably moved to immutable
`biztalk-instance-actions.archive-<SHA256>.json` segments before replacing the active
file. Trimming never deletes audit phases; archives are retained without automatic
deletion. Back up active and archive files together. Reads combine/deduplicate them
and apply filters before the bounded result limit. Corrupt files remain in place and block
mutations. This is no distributed transaction with BizTalk. The bounded audit GET
`/api/biztalk/instances/audit?server=&instanceId=&max=200` stays available with control
off even for disabled or removed targets, or when config cannot be loaded.
The page has independent server-ID/name and GUID filters; a blank server filter
shows all history. Known disabled targets retain their canonical ID; unknown targets
are explicitly marked and the original bounded target reference is preserved as
RequestedServer.

On Windows an additional **Application** event is attempted asynchronously:
source **Minicon.SimpleAdmin.WebUI**, fallback existing **SimpleAdmin**, both checked
for Application mapping. Prepare the preferred source during installation using an
elevated PowerShell, for example:

```powershell
New-EventLog -LogName Application -Source 'Minicon.SimpleAdmin.WebUI'
```

Runtime never reassigns or creates sources, and does not require elevated logging
rights. IDs: **3100 Information** Accepted, **3101 Warning** Rejected/Unknown,
**3102 Error** Failed; payload correlates the same action/user/GUID/OperationId.
One result per request is queued, not one per file phase. The 128-entry background
buffer never waits for Windows IO in an HTTP request. Missing/misassigned sources,
denied rights, event exceptions/hangs or overflow fall back to file audit with one
host warning per process; restart after repair to check sources again. Events can
be lost during failure/shutdown. Their failure never changes the action or file
persistence result. **Non-Windows: file audit only**, no expected-platform warning.
ScheduledTasks keeps its existing file format and does not gain event auditing.

Before customer release, verify the new GET/PUT routes and responses, account rights,
network/TLS, authorized resumable/non-resumable test instances and an explicitly
approved disposable message for Terminate. Correlate file and Windows events, and
exercise missing-source/rights fallback. Productive REST monitoring already exists;
these new operation checks have not been performed against the customer environment.

Audit snapshots, archive segments and lock files inherit permissions from their
directory. Protect ConfigPath/AuditLogDirectory so only the service account and
authorized administrators/readers have access; keep the same protections for backups.
Upstream mutation 404/409 remains Rejected with HTTP 404/409; upstream 408/504 is
Unknown/504, never automatically retried. ObservedStatus is null rather than a stale
pre-action status; the UI performs the fresh observation. The event platform check
runs once on the background worker; overflow degrades the event channel until restart
with one asynchronously scheduled warning. The intentionally retained Wait/TryWrite
combination detects dropped events without waiting; DropWrite would hide overflow.

### Troubleshooting BizTalk instance control

| Symptom | Operator check |
|---|---|
| Live/actions return 403 | Check host `BizTalk:ControlEnabled`, authentication and global `Authentication:RequiredRole`. Keep authentication enabled for normal operation. Audit history remains readable. |
| Target returns 404 | Check config server ID, Active, global BizTalk feature and server check. A vanished instance or upstream action 404 is also rejected; refresh live. |
| API failure / 502 | From the WebUI host under its actual service account, check the configured management-service base, DNS, network/TLS, integrated authentication and read/PUT permissions. Worker access alone does not prove WebUI access. Use the displayed account/OperationId for correlation. |
| Limited/empty results | Filters cover only the bounded scan; active entries count too. Check the warning, reset filters/page and adjust host limits only within the documented bounds. |
| 409 | A state changed, another operation is pending, or the GUID lock conflicts. Refresh and inspect history before acting. |
| Unknown / 504 or connection loss | The command may have been accepted. Inspect live state and audit; never repeat automatically. |
| 503 / AuditPersisted=false | Check audit path, free space, inherited directory rights, active/archive files and lock access. Preserve corrupt/unresolved evidence; repair storage before another mutation. |
| No Windows Application event | Check the local WebUI-host source and Application mapping/write rights, plus the one-time host warning. Repair and restart; file audit stays authoritative. Events are best effort and can be lost on shutdown/overflow. |

WebUI REST routes are `GET /api/biztalk/instances/live`, JSON-only
`POST /api/biztalk/instances/action` and `GET /api/biztalk/instances/audit`.
The provider uses management-base-relative `GET OperationalData/Instances` and
`PUT OperationalData/Instances/Resume/{guid}` or `Terminate/{guid}`. Validate these
new routes and response/status contracts at the customer before release; the existing
productive REST application monitoring is already confirmed.

Before a new Requested phase, all existing archive segments are read and their filename
SHA256 and JSON checked under the main audit lock, one segment at a time. Corruption
blocks new mutations (503) and makes audit reads return 503, including valid JSON with
a mismatching hash; evidence stays unchanged. Existing completions and rejection audits
can still be recorded. There is no integrity cache: validation cost grows with retained
archive history. Diagnose/restore damaged evidence from a trusted backup before retrying.
All BizTalk display timestamps use the WebUI host's local timezone and the shared
German format with seconds and explicit UTC offset; raw API timestamps retain their offsets.

## Run-level log monitoring — operator guide

Regex signatures count lines. They cannot see a run that **started but never finished**
and they cannot see a run that **never happened at all**. A log scan can therefore declare
open-run rules (`pendingRules[]`) and expected-run rules (`expectedRuns[]`).

The motivating case — a Kassen/JUKOS interface log (~21 000 lines, CRLF, UTF-8):

```
2026-11-20 22:05:15.756 --- Starte Schnittstelle execute/schnittstelle/einlesen/KASSEN/JUKOS ---
2026-11-20 22:05:15.756 open: http://localhost:8080/...
2026-11-20 22:05:15.756 waiting for Result...
2026-11-20 22:15:09.456 Schnittstelle Kassen/Jukos für Nutzer rshw verarbeitet. Verarbeitungsergebnis: OK
2026-11-20 22:15:09.457
Laufzeit: 00:09:53.699

2026-11-25 13:40:41.040 --- Starte Schnittstelle ... ---
2026-11-25 13:40:41.041 open: ...
2026-11-25 13:40:41.041 waiting for Result...

2026-11-25 14:14:06.933 --- Starte Schnittstelle ... ---      <-- erneuter Start, der vorige endet nie
2026-11-25 14:44:05.679 waiting for Result...                 <-- Dateiende: Lauf offen
```

### Findings and file selection

| Finding | Config key | Problem ID |
|---|---|---|
| Open run | `pendingRules[]` | `log_<scan>_<rule>_pending` |
| Abandoned run | `pendingRules[]` | `log_<scan>_<rule>_abandoned` |
| Expected run | `expectedRuns[]` | `log_<scan>_<rule>_missing` |

**Every pending or expected-run rule evaluates exactly one file: the newest matching file by
`LastWriteTime`.** There is no cross-file continuation: an end line in another file never closes
an open run, and an older file is not used as additional expected-run evidence. Configure one scan
per authority when several independent logs match a glob.

Existing signature scans keep their previous behavior: every file admitted by `maxFileAgeMinutes`
is scanned separately with exactly `maxLines` from its tail (`0` means the whole file). When a file
is used by signatures and run rules, it is read only once; the signature evaluation takes its own
`maxLines` subset from the shared snapshot. Run rules use a separate bounded tail derived from
`maxLines` (at most 200 000 lines). The preview uses the same newest file for pending rules while
still reading the other admitted files for signature suggestions.

`FileSystemAccess` reads forward with a `StreamReader` into a bounded ring buffer. BOM detection,
UTF-8/UTF-16, empty lines and CR, LF or CRLF line endings match
`File.ReadAllLines(path).TakeLast(maxLines)`; memory usage does not grow with the file size.

### Open runs

A rule names the **start** and **end** of a run. A start without an end until the end of the file is
**hanging** once it is older than `maxMinutes` (default 30) — otherwise it is only shown as
„läuft seit X min“ (still running, no problem). A start followed by a newer start without an end in
between is an **abandoned run**, aggregated per rule with count and last occurrence, but only for
the last 24 h (`reportAbandoned: false` switches it off).

In the admin UI one rule per line, lossless named format (order-independent), fields separated by
`::`:

```
name=Lauf muss abgeschlossen sein :: start=waiting for Result :: end=Verarbeitungsergebnis: :: max=30 :: severity=Critical
```

`start` and `end` are mandatory. Optional: `abort` (ends the run), `tsRegex`/`tsFormat` (timestamp),
`reportAbandoned`, `wiki`, `steps`. Remediation steps are separated by an unescaped `|`. Backslash,
`::` and `|` inside values are escaped with a backslash, so regular expressions and steps containing
these characters survive the round trip. The positional format
`Anzeigename :: StartRegex :: EndRegex :: MaxMinuten :: Severity :: AbbruchRegex :: ZeitstempelRegex :: ZeitstempelFormat`
is still read for hand-maintained configs.

Timestamps default to the line start `yyyy-MM-dd HH:mm:ss.fff` (local time of the server writing the
log). `timestampRegex` takes the first capture group instead; if a line has no timestamp, the
selected file's `LastWriteTime` is used. CRLF, empty lines and continuation lines such as
`Laufzeit: 00:09:53.699` do not disturb the evaluation.

### Expected runs

`expectedRuns[]` expects a run on certain days at a fixed time and reports it when the selected log
contains no matching line. The due date is the **last** scheduled day whose
`expectedTime + graceMinutes` is ≤ now; every candidate is checked against this condition, including
with a grace period longer than one day. On Monday 08:00 the due day is Friday, on Saturday it is
also Friday. The finding is only raised after the grace period has elapsed (default 120 min).
`holidays` is a plain list of ISO dates — there is **no** automatic holiday calculation; a holiday
falls back to the previous scheduled working day.

```
name=Abendlauf Kassen/JUKOS :: match=Verarbeitungsergebnis: :: schedule=Mon-Fri :: time=22:00 :: grace=120 :: severity=Critical :: holidays=2026-12-24,2026-12-31
```

`schedule` uses the known day syntax (`Mon-Fri`, `Mon,Wed,Fri`, `Sat`, empty = every day), `time` is
`HH:mm`, `grace` the tolerance in minutes (default 120). Further fields: `severity`, `holidays`
(comma-separated exceptions), `wiki`, `steps`.

A matching line proves the due run only within **expectedTime − 12 h through expectedTime + grace**
(inclusive). At 22:00 with 120 minutes grace, 10:30 counts, 23:30 also counts, and a line after
midnight no longer counts. The informational `LastRun` is determined independently up to the
current time, so a late run is still shown even when it missed the grace window. A regex timeout
produces a visible `EvaluationError` and **no** missing-run finding; there is no multi-file evidence
limit because only the newest file is evaluated.

> **Wichtig: „Erwarteter Lauf“ wertet nur die neueste passende Datei aus.** Legt ein Job am Wochenende
> eine neue Datei an, kann am Montag ein Fehlalarm entstehen, weil ein gültiger Beleg in der
> Freitagsdatei nicht mehr gelesen wird. Für fortlaufend geschriebene Logs (eine Datei) ist dies unkritisch.

### Many identical log scans (one per authority)

When many authorities share the same log layout, the Servers/Edit card
**Datei-/Protokollüberwachung** offers **„Für weitere Behörde kopieren“** (log scans) and
**„Kopieren“** (watched directories). The button clones the selected row including all rules,
thresholds, wiki link and remediation steps, appends „ (Kopie)“ to the name and re-indexes the form
fields — only the name and the path need adjusting. Unknown future config fields are retained on
duplicates, reordered rules and pure renames; ambiguous matches never inherit another item's fields.

### Where the findings appear

- **Status → Details**, section *Datei-/Protokollüberwachung*: tables „Offene Läufe“ and
  „Erwartete Läufe“, plus access and evaluation errors.
- **Problems page** and the notifier mail: one entry per finding, with severity, wiki link and
  remediation steps.
- **Static HTML** detail page (`📁 Dateiüberwachung`) — the same run tables and errors, available
  even when the WebUI is down.
- **File-monitoring badge** on the server card counts findings, access errors and evaluation errors.
