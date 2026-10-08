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
<PackageReference Include="Minicon.SimpleAdmin.Worker" Version="2.28.0" />
```

### Admin UI

```csharp
using Minicon.SimpleAdmin.WebUI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSimpleAdminWebUI(builder.Configuration);

var app = builder.Build();
app.UseSimpleAdminWebUI();
app.Run();
```

```xml
<PackageReference Include="Minicon.SimpleAdmin.WebUI" Version="2.28.0" />
```

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
- BizTalk monitoring (management API) and file/log monitoring
- **Central e-mail notifier**: designated servers aggregate all statuses over HTTP
  and send summary mails with acknowledge deep-links (HTML + plaintext, reminders,
  all-clear notices, HA via two coordinated instances) — testable end-to-end via
  the "Test-Mail senden" button in the admin UI
- Problem acknowledgement with expiry — single or multi-select ("Auswahl quittieren")
- Time profiles (relaxed thresholds outside business hours)
- Central aggregation across multiple servers
- AES-256 connection-string encryption (`Encryption:ConnectionStringKey`)

The admin UI ships a complete German documentation page (**/Documentation**)
covering every check type, the notifier, authentication and the REST API.

## License

MIT — see [LICENSE](LICENSE).
