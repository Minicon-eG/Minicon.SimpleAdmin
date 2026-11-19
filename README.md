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
- **Read-only BizTalk live instances** at `/BizTalk/Instances`, accessible from server
  details: GUID, application, suspended time with timezone and bounded error details,
  filters and live paging. No Resume/Terminate or audit endpoints in this package.
  The existing productive REST monitoring is confirmed; verify the new instance route
  and rights/connectivity from the WebUI host under its service account before customer use.
- BizTalk monitoring (management API) and file/log monitoring (stuck files on shares,
  error signatures in logs) — with a **setup assistant** in the admin UI that tests
  paths and regex signatures against the real files before saving, offers templates
  and suggests common error signatures with their hit counts
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

### BizTalk live instances and single actions (P3a/P3b)

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
