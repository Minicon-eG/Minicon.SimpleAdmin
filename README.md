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
<PackageReference Include="Minicon.SimpleAdmin.Worker" Version="2.34.1" />
```

### Admin UI

```csharp
using Minicon.SimpleAdmin.WebUI;

return await SimpleAdminWebUIHost.RunAsync(args);
```

```xml
<PackageReference Include="Minicon.SimpleAdmin.WebUI" Version="2.34.1" />
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
</PropertyGroup>
```

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
