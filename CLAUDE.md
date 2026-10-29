# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Minicon.SimpleAdmin** is a monitoring platform for PRTG and NetScaler environments, distributed as a set of reusable **NuGet packages**. It consists of a console worker that collects system metrics and generates static HTML status pages, plus an ASP.NET Core admin UI for configuration management, real-time status viewing and problem acknowledgement.

**Key Design Philosophy**: Maximum availability through static file generation. The HTML pages have no dynamic dependencies and remain accessible even if workers, databases, or external systems are down (as long as IIS/nginx is operational).

**Repository type**: this repo publishes NuGet packages. Host applications (thin deployment wrappers kept in separate, private repositories) consume them. Code changes here usually need a version bump and a release to take effect downstream.

**Repository URL**: https://github.com/Minicon-eG/Minicon.SimpleAdmin (public)

## Commands

### Build and Test
```bash
# Build the whole solution
dotnet build Minicon.SimpleAdmin.slnx

# Run all tests
dotnet test src/Minicon.SimpleAdmin.Tests/Minicon.SimpleAdmin.Tests.csproj

# Build NuGet packages into ./local-feed
dotnet build Minicon.SimpleAdmin.slnx -c Release
dotnet pack  Minicon.SimpleAdmin.slnx -c Release -o local-feed --no-build
```

### Versioning
- All package `Version` properties are kept in sync across the `*.csproj` files (currently `2.36.1`).
- Bump every package's `<Version>` together — mixed package versions across the seven projects will break consumers because they all depend on each other.
- `GeneratePackageOnBuild=true` is set on each publishable project, so a Release build emits `.nupkg` files automatically.

### Debug Symbols (v2.20.1)
- `src/Directory.Build.props` sets `DebugType=embedded` + `EmbedAllSources=true` for every project, so each DLL carries an **embedded portable PDB including the full C# sources**. Consumers can debug and step into the original code without a symbol server. No separate `.snupkg` is produced (`IncludeSymbols=false`). Trade-off: larger DLLs and the source is visible inside every package.

### Source Extraction in Consumers (v2.32.0)
- Every package additionally ships its project files under `sources/` (copied via `obj/simpleadmin-sources/` because NuGet drops `Content` items with `Pack=false`, which Razor sets on all `.cshtml`) and the shared files (`Directory.Build.props`, `build/`, README, LICENSE) under `sources-root/`.
- `src/build/Minicon.SimpleAdmin.Sources.targets` is packed as `buildTransitive/<PackageId>.targets`: on build, the host extracts every referenced SimpleAdmin package to `SimpleAdmin.Sources/<PackageId>/` (re-extracted only when the package path/version changes, marker `.simpleadmin-package`; local edits are overwritten on upgrade). The folder is added to `DefaultItemExcludes`, so the host keeps compiling against the package DLLs. Opt-out: `<SimpleAdminExtractSources>false</SimpleAdminExtractSources>`, target folder: `SimpleAdminSourcesDirectory`.
- Customer namespace: `<SimpleAdminSourcesNamespace>Kunde.Monitoring</SimpleAdminSourcesNamespace>` replaces the token `Minicon.SimpleAdmin` (whole identifier, not after `Minicon-eG/`) in text files and in file/folder names during extraction (inline `RoslynCodeTaskFactory` task — netstandard2.0, so no `Path.GetRelativePath`). Namespaces, project names, `_content/<PackageId>` paths and logger categories stay consistent; LICENSE, `Authors` and repository URLs are kept. Keep `Minicon.SimpleAdmin` as the only spelling of the prefix in code so the rename stays complete.
- Test project (v2.36.0): the **Worker** package additionally packs `src/Minicon.SimpleAdmin.Tests` (without bin/obj/TestResults) under `sources-tests/` (target `_SimpleAdminPackTestSources` in `Directory.Build.props`). With `<SimpleAdminExtractTests>true</SimpleAdminExtractTests>` (default false) the host extracts it to `<SimpleAdminSourcesDirectory>/<Prefix>.Tests/` (own marker, renamed like the rest incl. `InternalsVisibleTo`). It references all six projects, so Worker and WebUI host must share one `SimpleAdminSourcesDirectory` (e.g. `../SimpleAdmin.Sources`); the build warns when the WebUI sources are missing there. Verified end-to-end: packed → two hosts with `Kunde.Monitoring` → `dotnet test` on the extracted project passes. Keep tests free of repo-relative paths so they keep working when extracted.

### Release Notes
- `CHANGELOG.md` (repo root, German, newest first, Keep-a-Changelog style: `## [X.Y.Z] – date – headline` with `### Neu / Geändert / Behoben / Sicherheit / Performance / Hinweise / Entwickler / Intern`) is the changelog of record and **must** get an entry in every release commit. The GitHub Release notes are extracted from that section (see `.claude/skills/release/SKILL.md`).
- **Two audiences (v2.36.0)**: the WebUI page „Neuigkeiten“ shows headline, bold summary and every section except `Entwickler`/`Intern`. Those user sections (and headline/summary) must describe only what a WebUI user notices, in plain German — no package/host/REST/config-key/dependency/test/build details. Technical content goes to `### Entwickler` (operators/integrators: config keys, appsettings, hosts, REST, dependencies) or `### Intern` (refactorings, tests). A release with only technical changes gets a neutral headline (e.g. „Technische Korrekturen“).
- **In the WebUI (v2.36.0)**: `CHANGELOG.md` is embedded into the WebUI DLL (`EmbeddedResource` via `$(_SimpleAdminRepoRoot)`, LogicalName `Minicon.SimpleAdmin.WebUI.CHANGELOG.md`; every package also ships it in `sources-root/` so hosts built from extracted sources have it). `ChangelogParser` (Markdown subset, HTML-encoded, only http(s)/relative links) + `ChangelogProvider` (singleton, cached) → `DocumentationController.Changelog` / `Views/Documentation/Changelog.cshtml`. Footer version links there; `site.js` shows a „Neu“ pill when the installed version is newer than `localStorage['sa-seen-version']` and marks newer releases on the page. The test `Provider_EmbeddedChangelog_ContainsTheInstalledVersion` fails when the newest CHANGELOG entry ≠ package version — write the entry before running the tests of a release.

### Publishing
- Trusted Publishing to nuget.org is configured via GitHub Actions (`Add NuGet publish workflow (Trusted Publishing)` commit). The workflow publishes all seven packages on release.

## Package Structure

The solution contains **seven projects**. All publishable libraries set `GeneratePackageOnBuild=true` and emit `Minicon.SimpleAdmin.<Name>.<version>.nupkg`.

| Package | Purpose | Key types |
|---|---|---|
| `Minicon.SimpleAdmin.Shared` | Pure models + a few cross-cutting services | `Config`, `Server`, `RuntimeStatus`, `ActiveProblem`, `Acknowledge`, `AcknowledgeService`, `ProblemDerivationService`, `ConnectionStringEncryption`, `StateFilesLock` |
| `Minicon.SimpleAdmin.Services` | Platform metrics + status persistence | `IPlatformMetricsProvider` (Windows/Linux/MacOS/Fallback), `MetricsCollector`, `StatusEvaluator`, `RuntimeStatusStore`, `IDateTimeProvider`, `IEnvironmentService` |
| `Minicon.SimpleAdmin.Checkers` | Optional per-server checkers + central notifier | `AppPoolChecker`, `WindowsServiceChecker`, `EventLogChecker`, `CertificateChecker`, `BizTalkChecker`/`BizTalkApiClient`, `FileShareChecker`/`FileSystemAccess`, `SqlQueryChecker`, `EmailProbeSender`, `EmailDeliveryChecker`, `NotificationService`, `SmtpMailSender`, `HttpStatusReader`, `NotificationStateStore` |
| `Minicon.SimpleAdmin.HtmlGenerator` | Static HTML rendering | `StaticHtmlGenerator` |
| `Minicon.SimpleAdmin.Worker` | Console worker host + DI wiring | `SimpleAdminWorkerHost.RunAsync(args)`, `SimpleAdminWorker`, `ServiceConfigReader` |
| `Minicon.SimpleAdmin.WebUI` | Razor Class Library — full admin UI (MVC + REST); references Checkers | `SimpleAdminWebUIExtensions.AddSimpleAdminWebUI/UseSimpleAdminWebUI`, controllers, views, view models, `ConfigurationService`, `StatusReaderService` |
| `Minicon.SimpleAdmin.Tests` | xUnit suite over Shared/Services/Checkers/HtmlGenerator/WebUI | not published |

### Dependency Graph
```
Shared (no deps)
  ↑
Services       (depends on Shared)
  ↑
Checkers       (depends on Shared)
  ↑
HtmlGenerator  (depends on Shared)
  ↑
Worker         (depends on Shared, Services, Checkers, HtmlGenerator)
WebUI          (depends on Shared, Services, Checkers — the file monitoring assistant reuses FileShareChecker)
Tests          (depends on everything)
```

`Directory.Build.props` adds `InternalsVisibleTo("Minicon.SimpleAdmin.Tests")` to every project except the test project itself, so internals are reachable from tests without polluting the public API.

## Host Integration (How Consumers Use This)

A consumer is a separate repository containing two thin host projects. Both host apps are deliberately tiny — all logic lives in the packages.

### Console worker host
```csharp
using Minicon.SimpleAdmin.Worker;

// [configPath] [statusDirectory] [wwwrootDirectory] [--force-probe]
return await SimpleAdminWorkerHost.RunAsync(args);
```
The host's `appsettings.json` supplies `Encryption:ConnectionStringKey` plus optional `Logging:LogLevel` overrides.

### WebUI host
```csharp
using Minicon.SimpleAdmin.WebUI;

return await SimpleAdminWebUIHost.RunAsync(args);
```
`SimpleAdminWebUIHost.RunAsync` (v2.34.0) handles the normal WebApplication build and, when invoked with `install` / `uninstall` / `start` / `stop` as the first argument, dispatches to `ServiceInstaller` for the Windows-service installer (see "Installation ohne IIS"). The older three-liner (`CreateBuilder` + `AddSimpleAdminWebUI` + `UseSimpleAdminWebUI` + `Run`) still works for custom hosts.

Host `appsettings.json` keys: `ConfigPath` (directory containing `config.json`; empty = the app directory itself, e.g. the IIS app folder — v2.24.0), `StatusDirectory`, optional `BasePath` for IIS sub-applications, `Encryption:ConnectionStringKey`, optional `Authentication:Enabled` / `Authentication:RequiredRole` (see "WebUI Authentication"), optional `DataProtection:KeysDirectory` / `DataProtection:Enabled` (see "DataProtection" below).

`AddSimpleAdmin(IServiceCollection, IConfiguration, HostPaths)` is exposed publicly so a host can also customise the worker's DI registrations before `BuildHost`.

### Installation ohne IIS (Windows-Dienst, v2.34.0)
`SimpleAdminWebUIHost.RunAsync(args)` erkennt die Subkommandos `install` / `uninstall` / `start` / `stop` am ersten Argument und delegiert an `ServiceInstaller.Dispatch`. Der Installer legt den Dienst (`sc.exe create binPath=<EigeneEXE>`, Autostart), die URL-ACL (`netsh http add urlacl`), eine Firewall-Regel (`netsh advfirewall firewall add rule`) und den DataProtection-Keys-Ordner (`icacls` mit `(OI)(CI)M` für das Dienstkonto) in einem Schritt an und startet den Dienst. Standard-Dienstkonto: `LocalSystem`; optional `--account=DOMAIN\u` + `--password=…` (interaktiver Prompt, wenn `--password` fehlt und `--non-interactive` nicht gesetzt ist). Flags: `--port=5080`, `--service-name=SimpleAdminWebUI`, `--display-name=…`, `--description=…`, `--keys-dir=C:\ProgramData\SimpleAdmin\dp-keys`, `--non-interactive`, `--purge` (nur `uninstall`, löscht den Keys-Ordner mit). Admin-Rechte werden am Anfang geprüft. Alle externen Prozesse laufen über `IProcessRunner`, die Tests mocken diese Schicht. Der `uninstall`-Pfad läßt den Keys-Ordner stehen (laufende Sessions/Cookies), `--purge` entfernt ihn.

Im Windows-Dienst-Modus (`WindowsServiceHelpers.IsWindowsService() == true`) nutzt der Host **HttpSys** statt Kestrel, damit Negotiate ohne IIS funktioniert — Listening-Prefix aus `ASPNETCORE_URLS` bzw. `Urls` (Default `http://*:5080/`). Interaktive Starts (Konsole) und IIS-Deployments bleiben auf Kestrel bzw. In-Process.

### DataProtection (v2.34.0)
`AddSimpleAdminWebUI` konfiguriert `IDataProtectionProvider` auf einen stabilen Dateisystem-Pfad (`DataProtection:KeysDirectory`, Default `C:\ProgramData\SimpleAdmin\dp-keys`) und verschlüsselt die Keys unter Windows mit **DPAPI im LocalMachine-Scope** (`ProtectKeysWithDpapi(protectToLocalMachine: true)`). Damit hängen die Keys nicht mehr am Benutzerprofil des AppPools / Dienstkontos — der frühere Startabsturz mit `CryptographicException 0xe0434352` beim Entschlüsseln verwaister User-DPAPI-Keys ist damit behoben. Hostseitig abschaltbar mit `DataProtection:Enabled = false`. Lässt sich das Verzeichnis nicht anlegen (fehlende Rechte), fällt der Host auf das Default-Verhalten (keine persistierten Keys) zurück und loggt einen Hinweis.

## Architecture — Worker (`Minicon.SimpleAdmin.Worker`)

`SimpleAdminWorker.RunAsync()` orchestrates one cycle:

1. Load and validate `config.json` (`ServiceConfigReader`)
2. Find current server by hostname
3. Process expired acknowledges
4. **Before** `Task.WhenAll`: if AppPool monitoring is enabled, load the previous `RuntimeStatus` from disk to extract the prior `AppPools` dictionary (enables `StoppedSince` carry-forward). Same pattern for email probe/delivery `ConsecutiveFailures`.
5. Run optional checkers **in parallel** via `Task.WhenAll`:
   - If AppPool feature enabled: `AppPoolChecker` (receives `previousStates` for `StoppedSince` carry-forward)
   - If Services feature enabled: `WindowsServiceChecker`
   - If EventLog feature enabled: `EventLogChecker`
   - If SqlQueries feature enabled: `SqlQueryChecker`
   - If emailProbes enabled per-server: `EmailProbeSender`
   - If emailDelivery enabled per-server: `EmailDeliveryChecker`
   - If Certificates feature enabled AND per-server enabled: `CertificateChecker`
   - Always (in parallel): `MetricsCollector` collects CPU/Memory/Disk for every ServiceType
6. Apply email status smoothing: if `ConsecutiveFailures < minConsecutiveChecks`, suppress non-Healthy status (mirrors metric smoothing).
7. For each service type on the current server:
   - Evaluate confirmed status from history (alert fatigue prevention)
   - `ProblemDerivationService.DeriveProblems(metrics, appPools, services, eventLogs, sqlChecks, acknowledges, serverId, emailProbes, emailDelivery, certificates)` → `List<ActiveProblem>`
   - Save `RuntimeStatus` with updated history to `status/<server>.<service>.json`
8. `StaticHtmlGenerator.GenerateAllAsync` renders all HTML pages
9. Optionally copy output to `CentralOutputPath`

**Notifier role** (config-driven, no flag): after the normal cycle, `RunAsync` calls `MaybeRunNotifierCycleAsync` — if `features.notifications.enabled` and the hostname is in `notifierServers`, it runs `INotificationService.RunCycleAsync(config)`. A notifier host that is not itself a monitored server still sends. See "Central Notifications" below.

### Important Worker Internals
- `RuntimeOptions.ForceProbe` (`--force-probe` CLI flag) bypasses the `EmailProbeSender` rate-limit gate for one cycle.
- `TrackAsync(name, factory)` wraps every parallel checker for per-checker debug timing logs (`[Checker] starting / completed in Xms / failed after Xms`).
- All checkers tolerate non-Windows platforms gracefully (return empty / Unknown).

## Architecture — WebUI (`Minicon.SimpleAdmin.WebUI`)

Razor Class Library. The host application provides only the entry point + appsettings.

### Controllers
| Route | Controller | Purpose |
|---|---|---|
| `/` | `HomeController` | Dashboard |
| `/Status` | `StatusController` | Server status list + per-server detail |
| `/Problems` | `ProblemsController` | All active problems sorted by severity |
| `/Environments` | `EnvironmentsController` | Environment matrix |
| `/Servers` | `ServersController` | CRUD for server config + service types |
| `/Settings` | `SettingsController` | History, Output, Features, Time Profile settings |
| `/Documentation` | `DocumentationController` | Built-in docs page; `/Documentation/Changelog` = „Neuigkeiten“ (release notes, v2.36.0) |
| `/Scripts` | `ScriptsController` | PowerShell script runner (v2.31.0, see below) |

### REST API (all under `/api/...`)
- `GET /api/status[/{id}][/metrics|/apppools|/services]` — `Api/StatusController`
- `GET /api/problems[/server/{id}]`, `GET /api/problems/{id}?serverId=` — `Api/ProblemsController`
- `GET|POST|PUT|DELETE /api/acknowledges[/{id}|/server/{id}|/{id}/extend]` — `Api/AcknowledgesController`; `POST /api/acknowledges/bulk` (v2.28.0, `BulkAcknowledgeRequest`: `items[{serverId, problemId}]` + shared duration/comment/expiresAt, duplicates ignored, publishes once) backs the multi-select "Auswahl quittieren" on `Views/Problems/Index.cshtml`. Problems page (v2.30.0): default filter = open (unacknowledged); client-side filters status/severity/server/text kept in the query string (`?status=all&severity=Critical&server=…&q=…`), KPI tiles set filters, select-all/bulk act on visible rows only; styles under "Problems page" in `wwwroot/css/site.css`
- `POST /api/sqlquerytest/{connection|query}` — `Api/SqlQueryTestController`
- `POST /api/emailtest/{smtp|imap}` — `Api/EmailTestController`
- `POST /api/scripts/{id}/runs`, `GET /api/scripts/runs/{runId}?from=n`, `POST /api/scripts/runs/{runId}/cancel`, `GET /api/scripts/browse?path=&mode=file|folder&extension=` — `Api/ScriptsApiController` (script runner, JSON-only POSTs)
- `POST /api/filemonitoringtest/{directory|logscan}` — `Api/FileMonitoringTestController` (dry run for the file monitoring assistant, see below)
- `POST /api/notificationtest/send` — `Api/NotificationTestController` (resolves the notifier's `smtpRef` exactly like `NotificationService` and sends a test mail; accepts current form values so it works before saving)

### WebUI Services
- **`StatusReaderService`** (10 s cache) — reads from `output.centralOutputPath` (if set) or `StatusDirectory` and **merges** all sources (v2.23.1): single `RuntimeStatus` files in the folder and its `status/` subfolder plus combined `*.status.json` files; per server/service the newest `LastUpdate` wins (tie: single file, it has history). Before v2.23.1 the first non-empty source won, so a central host whose local worker writes into the push folder only showed itself. **HTTP pull (v2.24.0)**: with `output.pullStatusOverHttp` (default true) it additionally fetches `{baseUrl}/status/{name}.status.json` of every active server from config.json in parallel (named HttpClient `SimpleAdminStatus`, 5 s timeout) and merges it the same way; an active server with neither HTTP nor file data gets a placeholder entry (service `erreichbarkeit`, status Unknown). A central WebUI thus shows all servers with no shared folder. **Empty-state diagnostics (v2.29.0)**: `GetDiagnostics()` → `StatusSourceDiagnostics` (config load state/path, `StatusDirectory` vs `centralOutputPath`, scanned dirs with file counts, pull setting, server counts + German reasons); `StatusController.Index` passes it via `ViewData["Diagnostics"]` only when no server is found. It merges active acknowledges into problem derivation, supplies `BaseUrl` from config for static HTML links. `CalculateServiceStatus` and `CalculateOverallStatus` consider all checker types (incl. certificates) for the no-metrics-fallback and the severity escalation.
- **`ConfigurationService`** — in-memory config management with `HasChanges`, full CRUD, atomic save (temp file + `File.Move`), `MutateAndSaveAsync` wraps every mutation in a `SemaphoreSlim` write-lock + reverts via `ReloadFromFileAsync` if the save fails. Encryption is applied to a clone so `_config` is never mutated by the save path. Edit POST stays on the same page (does NOT redirect).

### Design System (Glossy theme, light/dark, v2.33.0)
- `wwwroot/css/theme.css` (loaded after `site.css`, before `pages.css`) styles every Bootstrap 5.3 component as frosted glass (light) / neon glass (dark) via `<html data-bs-theme>`. The theme is set inline in `<head>` from `localStorage['sa-theme']` or the OS preference (no flash); the navbar button toggles it (`site.js`). Never hard-code light colours (`#fff`, `bg-light`, `table-light`) — use `--bs-*` / `--sa-*` variables so dark mode keeps working.
- Application title: `general.title` in config.json (Settings → „Darstellung“, max. 60 chars), shown in the navbar and browser tab via `ConfigurationService.GetApplicationTitle()` (fallback "SimpleAdmin"). The static HTML pages keep their own fixed titles.
- Building blocks: `.pill pill-{ok|warn|crit|ack|info|muted}` (+ `pill-soft`, `pill-lg`), `.kpi-tile kpi-{tone}` (or `tone-{tone}`), `.btn-glass`, `.card-alert`, `.status-dot status-dot-{tone}`, `.page-head`.
- `Helpers/Display` (internal): all view times go through `Display.LocalTime/LocalTimeSeconds/LocalShort` (status timestamps are UTC, unspecified kind = UTC); German labels/tones/icons for `ServiceStatus`, metric/check status and `ProblemSeverity`; `Duration` includes days. `<time class="relative-time" data-utc="@Display.Iso(t)">` is rendered relative by `site.js`.
- `Helpers/ChartGeometry` renders sparklines and history charts as server-side SVG (no chart library, invariant-culture coordinates). Status/Details: metric tiles, service badges and cpu/memory/disk problems link to `#chart-<metric>`; per metric the history of the service with the most samples is used (services are never mixed).
- **No horizontal scrolling (v2.36.1)**: problem lists (`Views/Problems/Index.cshtml`, Status/Details „Aktive Probleme“ with `.problem-table-compact`) use `.problem-table` = `table-layout: fixed` + `<colgroup>` widths, the problem text wraps (`overflow-wrap: anywhere`), below 1200px the rows become CSS-grid cards (cells `cell-select/-severity/-server/-since/-status/-action`, `problem-main`). Long identifiers elsewhere get `.text-wrap-anywhere` (only on long-text cells — on every cell it would split short words). Navbar collapses below xl; a long `general.title` is truncated. Don't wrap such tables in `.table-responsive`.
- Shared partials: `_AcknowledgeModal` (acknowledge dialog for Problems + Status/Details), `_AutoRefresh` (60 s reload toggle, pauses while a dialog is open, a field has focus or the tab is hidden). The nav "Probleme" count is loaded asynchronously from `/api/problems` so the layout never blocks on status reads.

### PowerShell Script Runner (v2.31.0)
Start admin-registered PowerShell scripts from the WebUI (e.g. an archive copy script). Everything lives in the WebUI package (`Models/Scripts`, `Services/Scripts`, `Controllers/ScriptsController`, `Api/ScriptsApiController`, `Views/Scripts`); the worker is not involved.
- **Host gate**: `appsettings.json` section `ScriptRunner` (`ScriptRunnerOptions.Resolve`): `Enabled` (default **true**, opt-out per host; **forced off when `Authentication:Enabled=false`** unless `AllowWithoutAuthentication`, reason in `DisabledReason` → Disabled view + startup log), `RequiredRole`, `AllowedDirectories` (upload dir always allowed), `BrowseRoots`, `PowerShellPath` (default `powershell.exe` / `pwsh`), `MaxConcurrentRuns` (4), `MaxOutputLines`, `HistorySize`, `UploadDirectory` (`<ConfigPath>/scripts`), `RunLogDirectory` (`<ConfigPath>/script-runs`). Deliberately not in config.json — UI config editors must not be able to widen the limits or re-enable it on a host that opted out. `[ScriptRunnerGate]` enforces Enabled + role on MVC and API; the nav item only shows when enabled.
- **Definitions** in `scripts.json` next to config.json (`ScriptDefinitionStore`, atomic write). Created by upload or server path; `PowerShellScriptParser` (pure C#, no PowerShell needed, never executes the script) reads the script-level `param()` block + comment-based help and infers the control (`ScriptParameterType`): switch/bool → Switch, ValidateSet → Choice (array → multi), numbers (+ValidateRange) → Number, arrays → List, datetime / `…Date` / help "YYYY-MM-DD" → Date (format detected, e.g. `dd.MM.yyyy`), `Time…`/"HH:mm" → Time, FileInfo/`…Path`/`…File` → File, DirectoryInfo/`…Folder`/`…Directory` → Folder; Mandatory, literal defaults, ValidatePattern. `MergeParameters` → `MergeResult` (parameters + German change notes): keeps admin overrides (label/type/format/fixed/default) only while the script did not change underneath — changed `PsType` re-detects the control (drops Fixed), changed script default (tracked in `ScriptDefaultValue`) wins; unknown old type/default (hand-added or v2.31.0 data) keeps the override. Bare words are expressions, not literals. Per parameter `Fixed` (admin value, not editable) and `Hidden` (never passed; rejected for mandatory parameters). Min/Max bind via `MinText`/`MaxText` (culture-invariant). Shared tables: `PowerShellTypes`, quote chars in `Shared/Services/PowerShellLiteral` (also used by `EventLogChecker`).
- **Execution** (`ScriptRunService`, singleton): `ScriptArgumentBuilder` validates the form values (required, choices, pattern, ranges, date/time formats) and encodes them Base64 into env `SIMPLEADMIN_SCRIPT_ARGS`; a fixed bootstrap (`-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand`) decodes and splats them (`& $script @args`) → no command-line injection; arrays/int/switch keep their types. A switch defaulting to `$true` is passed as `-X:$false` when turned off. Exit code ≠ 0 → Failed. Output: lines (ANSI stripped, cut at 8 KB) go into a fixed ring buffer (`MaxOutputLines`, O(1)) and a buffered, human-readable log file (`yyyy-MM-dd HH:mm:ss.fff | text` / `! text` for stderr, flushed every second; legacy v2.31.0 format still parsed). Finished runs stay in memory 2 min, then `GetOutput` serves the tail from the file (`ReadLogTail` reads backwards, cost independent of file size) and live pollers get `resync`. `Log` download = `PhysicalFile` of the log. The run ends on process exit (Exited event, not `WaitForExitAsync`), waiting at most `StreamDrainGrace` (5 s) for pipes held by background children; timeout/kill waits are bounded. Cancel before the process exists is honoured. One run per script unless `AllowParallelRuns`; `MaxConcurrentRuns` counts only this process's runs. Audit history `script-runs/history.json`: every write re-reads + merges (newest `ScriptRun.UpdatedAt` wins) under a `history.json.lock` file lock, so IIS overlapped recycle loses nothing; runs still "Running" at startup become `Interrupted` (a still-alive old process's later update wins); `Dispose` records own running runs as `Interrupted`. `scripts.json` (`ScriptDefinitionStore`): reloaded when its write time changes; unreadable → `LoadError` banner, saving blocked (never overwritten).
- **UI**: Index (cards + recent runs), Create (upload / server path with file chooser), Edit (parameter editor, re-read, source preview), Run (generated form with date/time pickers, dropdowns, server file/folder chooser `_PathBrowserModal` — listing filters while enumerating and stops at 2 000 entries, off the request thread; command preview mirrors `ScriptArgumentBuilder.FormatCommandLine`), Output (loads the tail, then polls; keeps at most 4 000 lines in the DOM, backs off on errors, stops on 404, slows down in background tabs; cancel, re-run with same values, log download).
- **Tests**: parser/builder/run-service unit tests (`/bin/sh` via the `StartInfoFactory` seam, `[UnixFact]`; seams `TimeoutUnit`, `StreamDrainGrace`); `[PowerShellFact]` tests run end-to-end when `pwsh` is on PATH or `SIMPLEADMIN_TEST_PWSH` points to it and are reported as skipped otherwise.

## Important Models (Shared)

All canonical models are in `src/Minicon.SimpleAdmin.Shared/Models/`.

- **Config/** — `Config` (root), `EnvironmentConfig`, `Server` (+ `ServerChecksConfig`, `ServerServicesConfig`, `ServerSqlQueriesConfig`, `ServerEmailProbesConfig`, `ServerEmailDeliveryConfig`), `ServiceType`, `PrtgConfig`/`PrtgCheck`, `NetScalerConfig`/`HealthCheck`/`ExpectedResponse`/`JsonPathAssertion`, `Threshold`, `HistorySettings`, `OutputSettings`/`CentralGeneratorSettings`, `GeneralSettings`, `FeaturesConfig` (+ `AppPoolFeatureConfig`, `EventLogFeatureConfig`, `CertificateFeatureConfig`, `AcknowledgeFeatureConfig`, `SqlQueryFeatureConfig`, `BizTalkFeatureConfig`, `FileMonitoringFeatureConfig`, `NotificationsFeatureConfig`), `ServerBizTalkConfig`, `ServerFileMonitoringConfig` (+ `WatchedDirectory`, `LogScanConfig`, `LogPatternConfig`), `TimeProfileConfig`, `SmtpConfig`/`ImapConfig`, `EmailProbeConfig`/`EmailDeliveryCheckConfig`, `SqlQueryCheckConfig`/`SqlQueryConfig`/`SqlRowCountAssertion`/`SqlColumnAssertion`/`SqlValueType`.
- **State/** — `Acknowledge`, `AcknowledgeState` (`ProcessExpired`, `CleanupHistory`), `ActiveProblem` (factories: `AppPoolStopped`, `AppPoolHighMemory`, `AppPoolLongUptime`, `CpuHigh`, `MemoryHigh`, `DiskHigh`, `ServiceDown`, `ServicePatternNoMatch`, `EventLogErrors`, `SqlQueryConnectionError`, `SqlQueryExecutionError`, `SqlQueryRowCountMismatch`, `SqlQueryColumnMismatch`, `CertificateExpiring`, `EmailProbeSmtpError`, `EmailDeliveryImapError`, `EmailDeliveryStale`, `ServerStale`, `BizTalkApplicationStopped`, `BizTalkOrchestrationStopped`, `BizTalkSendPortStopped`, `BizTalkReceiveLocationDisabled`, `BizTalkSuspendedInstances`, `BizTalkApiError`, `FileStuck`, `LogError`, `FileShareUnreachable`), `AppPoolState`, `CertificateState`, `BizTalkState`/`BizTalkArtifact`/`BizTalkSuspendedGroup`, `FileMonitoringState`/`StuckFile`/`LogMatch`, `ServiceState`, `EventLogState`/`EventLogEntry`, `SqlQueryCheckState`/`SqlQueryResult`/`SqlRowCountResult`/`SqlColumnResult`, `EmailProbeState`, `EmailDeliveryCheckState`, `NotifyState`/`NotifyEntry` (central notifier bookkeeping), enums (`MetricStatus`, `OverallStatus`, `ProblemSeverity`, `AppPoolStatus`).
- **Status/** — `RuntimeStatus` (Server, Service, LastUpdate, Status, Metrics, Endpoints, History, AppPools, Services, EventLogs, SqlQueryChecks, EmailProbes, EmailDelivery, **Certificates**, **BizTalk**, **FileMonitoring**), `Metric`, `MetricSnapshot`, `MetricThreshold`, `ServiceStatus` (Healthy/Degraded/Unhealthy/Unknown/Unreachable/Acknowledged), `EndpointStatus`/`EndpointType`, `CentralServerStatus`.

## Important Patterns

### Status Determination Order
Problems are evaluated first, before checking whether metrics exist:
- All active problems acknowledged → `Acknowledged`
- Any unacknowledged `Critical` problem → `Unhealthy`
- Any unacknowledged `Warning` problem → `Degraded`
- No metrics AND no monitoring check data → `Unknown`
- DNS/connection failure → `Unreachable`
- All metrics `Ok` → `Healthy`

A server with no PRTG metrics but a failing email delivery check correctly returns `Unhealthy`, not `Unknown`.

### CPU-Metrik gegen Selbstkontamination (v2.34.2)
`WindowsMetricsProvider.GetCpuUsageAsync` las vorher `% Processor Time` einmal mit nur 100 ms Fenster — bei einem kurzlebigen Worker, der `MetricsCollector` parallel zu 10+ Checkern in `Task.WhenAll` laufen lässt, landete das Sample fast immer in einem Burst (JIT/DI/Config-Load, PowerShell für EventLog, SQL-Probes, AppPool-PerfCounter) und meldete 100 % obwohl der Server idle war. Jetzt:
- **Mehrfachabtastung mit Median**: default 3 Samples über 900 ms Fenster (`CpuSamplingOptions`, `Metrics:Cpu:WindowMs` / `Metrics:Cpu:Samples`). Eine einzelne Burst-Spitze gewinnt nicht mehr.
- **Eigene CPU-Last abziehen**: `Process.TotalProcessorTime` vor/nach dem Fenster → `100 × self_ms / wall_ms / coreCount` wird vom System-Median subtrahiert. Default an, Opt-out `Metrics:Cpu:SubtractSelf=false`.
- **Prozess-Priorität `BelowNormal`**: `SimpleAdminWorkerHost.RunAsync` setzt den Worker-Prozess beim Start auf `ProcessPriorityClass.BelowNormal`, damit er den eigentlichen Workloads keine Rechenzeit stiehlt.
- Debug-Log `[CPU] (system median X %, self Y % over Z ms, samples […])` zeigt, wie der Endwert zustande kommt.
Linux (`/proc/stat`) und macOS waren nicht betroffen (sauberer Delta-Rechenpfad).

**Nachtrag (v2.36.1):** Der Median über 900 ms reichte nicht, weil die Messung parallel zu den Checkern lief und deren **Kindprozesse** (`powershell.exe`, `sc.exe` je Dienst, `appcmd.exe`, `netsh`, plus Defender-Scans) nicht vom Eigenanteil erfasst werden. Jetzt:
- Der Worker sammelt die Metriken **erst nach** `Task.WhenAll` der Checker.
- CPU = **Mittelwert** (nicht Median) über ein langes Fenster: Default 60 s / 12 Samples (`Metrics:Cpu:WindowMs` 200 ms…180 s, `Metrics:Cpu:Samples` 2…120).
- `Metrics:Cpu:RuntimeBudgetSeconds` (Default 240, 30…3600): Das Fenster wird verkürzt, sodass Prozessstart + Checker + Fenster + 30 s Reserve (`CpuSamplingOptions.RuntimeReserve`) im Budget bleiben, mindestens 1 s (`EffectiveWindow`, Basis `Process.StartTime`, weil der Worker one-shot ist).
- Der CPU-Wert wird pro Lauf einmal gemessen und für weitere ServiceTypes 2 min wiederverwendet (sonst 60 s je ServiceType).

### Domains und Namenskonvention (v2.35.0)
`Config.Domains` (`List<Domain>`, fehlender Block = leer): `Name` (DNS), `Environment` (Key aus `Config.Environments`, **immer explizit**), `Servers`, optional `NetScalerVServer` (darf von mehreren Domains geteilt werden), `HealthCheck` (Name eines NetScaler-HealthChecks, nur Information), `Description`, `AllowNameMismatch`. Beide PROD-Varianten (`app`, `app-prod`) sind eigenständige Einträge. `Shared/Services/DomainNamingConvention`: `SuggestEnvironment`/`IsConsistent`/`Evaluate` – erstes DNS-Label, `-dev`→development, `-ref`→ref, `-prod`/kein bekanntes Suffix→production (nur wenn der Key existiert), längstes Suffix gewinnt; erweiterbar/überschreibbar per `Config.DomainSuffixMap` (leerer Wert entfernt ein Suffix). Nur Vorschlag/Warnung, nie Quelle der Wahrheit. WebUI: `DomainsController` (Liste nach Umgebung, Create/Edit/Delete, `GET /Domains/Suggest`), Serverauswahl nur aus `GetServersInEnvironment(env)`, Mismatch wird erst mit „Ausnahme zulassen“ gespeichert; Serverliste hat Umgebungs-Spalte + Filter; Server-Rename/-Delete pflegt `Domain.Servers` mit. HtmlGenerator: `RenderDomainList` zeigt Domains je Umgebung auf `all.html` und der zentralen Übersicht (die jetzt auch weitere Umgebungen außer production/ref/development rendert). Checker sind unverändert.

### Status Smoothing (Alert Fatigue Prevention)
Status changes require N consecutive checks showing the same result (default `minConsecutiveChecks: 5`). `StatusEvaluator.GetConfirmedStatus()` re-evaluates each snapshot's value against current thresholds. If mixed → returns the most frequent; ties broken in favor of least severe. The same mechanism is applied to email probe/delivery via `ConsecutiveFailures` carry-forward.

### Problem First/Last Occurrence (v2.32.1)
Problems are re-derived on every read, so their start time is persisted separately: the worker carries `RuntimeStatus.ProblemsSince` (problem ID → first seen UTC) forward each cycle via `ProblemDerivationService.TrackFirstSeen` (resolved problems drop out; a severity change = new ID = new start). Every reader (HTML generator, `StatusReaderService` — earliest start across the server's service files —, `NotificationService`) calls `ApplyOccurrences(problems, status.ProblemsSince, status.LastUpdate)`; without an entry (older files) First = Last = `LastUpdate`. `StaticHtmlGenerator.WithoutHistory` is the single copy used for the HTTP/central status JSON — add new `RuntimeStatus` fields there.

### Feature Toggles
- `SystemMetrics`: always on
- `Services`: on by default; per-server `server.checks.services.enabled` defaults true
- `AppPools`: **off** by default — opt-in globally AND per-server
- `EventLog`: **off** by default; central long-term ignore rules via `features.eventLog.ignoreRules` (see below)
- `Certificates`: **off** by default — opt-in globally AND per-server (expiry mails come from the central notifier, not a certificate-specific toggle)
- `Acknowledge`: on by default
- `SqlQueries`: **off** by default — opt-in globally AND per-server
- `BizTalk`: **off** by default — opt-in globally AND per-server (`server.checks.biztalk`); polls the BizTalk Management API
- `FileMonitoring`: **off** by default — opt-in globally AND per-server (`server.checks.fileMonitoring`); watches file shares for stuck files + scans logs for error signatures
- `EmailMonitoring`: no global toggle — enabled purely per-server (`emailProbes.enabled` / `emailDelivery.enabled`)
- `Notifications`: **off** by default — opt-in (`features.notifications.enabled`); a worker becomes a notifier when its hostname is in `features.notifications.notifierServers` (no `--notify` flag)

### Event Log Ignore Rules (v2.22.0)
Central long-term suppression of known event log noise, configured in `features.eventLog.ignoreRules` (UI: Settings → Features → "Event-Log: Ignorier-Regeln").
- **Rule fields**: `source` (wildcard, case-insensitive), `eventIds[]`, `messageContains` (substring or wildcard), `logName` (empty = all logs), `servers[]` (**empty = all servers**, wildcards like `srv07*`), `reason` (audit trail), `expiresAt` (optional UTC expiry → forced review).
- A rule matches only when **all** set criteria match. Rules without any criteria (no source/eventIds/message) are treated as inactive — they would otherwise suppress everything. Expired rules stop suppressing.
- Ignored events do **not** count against `warningCount`/`criticalCount` but are counted separately (`EventLogState.IgnoredCount`, badge "N Events (M ignoriert)").
- The worker pre-filters rules by hostname (`EventLogChecker.RuleAppliesToServer`) and passes them to `CheckEventLogsAsync`. Matching helpers (`MatchesWildcard`, `IsEventIgnored`, `MatchesSourceFilters`) are `internal static` and unit-tested.
- Per-log `includeSources`/`excludeSources` (wildcards, exclude wins) are applied before the ignore rules. PowerShell fetch limit is 500 events per log.
- **Query hardening (v2.31.0)**: `EventLogChecker.BuildEventQueryScript` embeds the log name from config.json only via `PowerShellLiteral.Quote` (single-quoted, all quote variants doubled, control chars dropped) and runs PowerShell with `-EncodedCommand` via `ArgumentList` — a crafted `logName` can no longer inject code into the worker's PowerShell call. Since v2.31.1 stdout and stderr are drained concurrently and the query is killed after `QueryTimeout` (2 min) — a full stderr pipe can no longer hang the worker cycle.
- Empty form rows are dropped in the Features POST handler; `EventIdsText`/`ServersText` are `[JsonIgnore]` form-binding helpers on `EventLogIgnoreRule`.

### Connection String Encryption
- AES-256-CBC with PBKDF2-HMACSHA256 (100 000 iterations). Format: `enc:v1:{base64(salt)}.{base64(iv)}.{base64(ciphertext)}`.
- Master key in host `appsettings.json` under `Encryption:ConnectionStringKey` (or env `Encryption__ConnectionStringKey`). MUST be identical in worker and WebUI hosts.
- WebUI auto-encrypts plain-text values on save; worker decrypts before opening `SqlConnection`. Plain-text passes through when no key is configured.

### WebUI Authentication (v2.20.0)
- `AddSimpleAdminWebUI` registers **Negotiate (Windows/Kerberos) auth** + a global **authorization fallback policy** (`RequireAuthenticatedUser`), so every controller/REST endpoint requires an authenticated user without any per-controller `[Authorize]`. `UseSimpleAdminWebUI` adds `UseAuthentication()` between `UseRouting()` and `UseAuthorization()`.
- **Why**: `/api/sqlquerytest` and `/api/emailtest` open outbound SQL/SMTP/IMAP connections to caller-supplied hosts (SSRF + credential relay); the config CRUD mutates `config.json`. None of these may be anonymous.
- **Secure by default**: enabled unless `Authentication:Enabled` = `false` (opt-out for dev/non-AD hosts, logged as a warning). Optional `Authentication:RequiredRole` (e.g. `DOMAIN\SimpleAdmin-Admins`) restricts access to one AD group.
- **Does not touch cross-process traffic**: `UseStaticFiles()` runs *before* the auth middleware, so `wwwroot/status/*.status.json` + `acknowledges.json` (pulled over HTTP by the notifier/peers) stay anonymous. The worker never calls WebUI endpoints — all its checks run in-process.
- Requires the `Microsoft.AspNetCore.Authentication.Negotiate` package (added to `Minicon.SimpleAdmin.WebUI.csproj`). Negotiate handshake works under Kestrel and behind IIS/reverse proxies; on non-Windows it needs a Kerberos setup (or opt out).

### Certificate Monitoring (v2.14.0)
- **Checker**: `CertificateChecker` reads from local Windows Certificate Store (`store://LocalMachine/My`, etc.) or file (`file://`). Returns one `CertificateState` per matching X509 certificate (keyed by thumbprint). Sentinel entries surface filter-no-match and load errors.
- **Status**: `DaysUntilExpiry ≤ criticalDays` → Critical (incl. expired ≤ 0); ≤ `warningDays` → Warning; else Healthy.
- **Problems**: `ActiveProblem.CertificateExpiring(subject, thumbprint, days, isCritical)` — ID `cert_<thumbprint8>_expiring`. Message: `"Zertifikat '<subject>' läuft in N Tagen ab"` or `"… ist abgelaufen (seit N Tagen)"`.
- **Notification mails**: certificate-expiry mails are sent by the **central notifier** (see "Central Notifications") — it derives the same `cert_*_expiring` problems from the aggregated status and mails them with an acknowledge deep-link. There is no certificate-specific mail config (removed in v2.16.1).
- **WebUI**: Servers/Edit has a "Zertifikat-Überwachung" card (`_CertificateChecksPartial.cshtml`) — source dropdown, Subject/Issuer/Thumbprint/FriendlyName filters, per-check WarningDays/CriticalDays overrides. Settings/Features has global default thresholds (Warning/Critical days). Status/Details renders a Zertifikate table.
- **HTML**: Detail page shows a `🔒 Zertifikate` section and a `🔒 K/N Zertifikate` monitoring badge.

### Email Monitoring
- Sender state files (per probe): `emailprobe.{safeName}.lastattempt` (every attempt, for rate-limit) and `emailprobe.{safeName}.lastsent` (success only, for UI).
- Rate-limit gate writes `.lastattempt` BEFORE connecting — so a hung/killed probe doesn't bypass the interval.
- Checker reuses ONE IMAP connection per cycle; UIDs to delete are deduplicated across overlapping subject filters and expunged in one bulk operation.
- Detailed debug logging available per category (see Logging section below).

### BizTalk Monitoring (v2.17.0)
- **Checker**: `BizTalkChecker` polls the BizTalk Management Service REST API via `IBizTalkApiClient` (HTTP with integrated Windows auth — `HttpClientHandler { UseDefaultCredentials = true }`). Reads `/Applications`, `/Orchestrations`, `/SendPorts`, `/ReceiveLocations`, `/OperationalData/Instances`. Pure static evaluators (`EvaluateArtifacts`/`EvaluateReceiveLocations`/`EvaluateSuspended`, all `internal` + unit-tested) build a compact `BizTalkState` (only problematic artefacts + per-category totals + suspended summary). API unreachable → `BizTalkState.ApiReachable = false`.
- **Signals → Problems**: Application/Orchestration/SendPort `Status != "Started"` → Critical; Receive Location `Enable == false` → Warning/Critical (per `disabledSeverity`/`criticalList`); suspended-instance count ≥ thresholds → Warning/Critical; API down → `biztalk_api_error`. Problem IDs: `biztalk_app_<name>_stopped`, `biztalk_orch_<name>_stopped`, `biztalk_sp_<name>_stopped`, `biztalk_rl_<name>_disabled`, `biztalk_suspended_instances`, `biztalk_api_error`.
- **Config**: global toggle `features.biztalk.enabled` + `Defaults` (SuspendedWarning/Critical). Per-server `server.checks.biztalk`: `baseUrl` (default `http://localhost/BizTalkManagementService`), `timeoutSeconds`, per-artefact `{ monitor: all|list|none, include[], exclude[] }` for Applications/Orchestrations/SendPorts, ReceiveLocations adds `disabledSeverity`/`criticalList`, and `suspendedInstances { enabled, warning, critical, byApplication }`.
- **Topology**: runs on the BizTalk server (worker polls localhost). Host-instance Windows services (`BTSSvc$*`) are covered by the existing `WindowsServiceChecker`, not this API.
- **WebUI/HTML**: Servers/Edit „BizTalk-Überwachung" card (`_BizTalkChecksPartial.cshtml`); Settings/Features global toggle + suspended defaults; Status/Details renders a BizTalk section; HTML detail page shows a `⚙️ BizTalk` section + a `⚙️ K/N BizTalk` monitoring badge.
- Problems flow through the normal pipeline (notifier mails with acknowledge deep-link, central overview, status escalation) — no BizTalk-specific notification path. `RuntimeStatus.BizTalk` is included in the per-server status JSON published over HTTP so the notifier sees it.

### File / Log Monitoring (v2.18.0)
Detects **stuck files** ("hängengebliebene Dateien") on file shares and scans log files for configurable **error signatures** — built for interface/transfer folders but kept fully generic (all site-specific paths/regex/wiki links live in the consumer's config).
- **Checker**: `FileShareChecker` watches directories and scans logs. The only IO is behind `IFileSystemAccess` (`FileSystemAccess` = real `System.IO`); the evaluation is pure and unit-tested (`EvaluateDirectory`/`EvaluateLogContent`, both `internal`). The synchronous `Check(config, defaults, now, ct)` core takes an injected `now` for deterministic tests; `CheckAsync` wraps it in `Task.Run`. A central worker reaches the site shares over UNC (the service account needs read access to the shares).
- **Stuck files**: per watched directory a file is flagged when it is older than `ageMinutesWarning`/`Critical` **or** still present after a daily `cutoffTime` (e.g. `21:45`, the folder is expected empty by then). Per-directory `includePatterns`/`excludePatterns` (glob), `recursive`. Severity = age tier, or `cutoffSeverity` for a cutoff violation.
- **Log scans**: per log path/glob, each configured `LogPatternConfig { name, regex (IgnoreCase), severity }` is matched against the lines (tail-limited by `maxLines`); matches are **aggregated per pattern** (count + sample line) so the problem ID is stable. `maxFileAgeMinutes` skips stale files in a glob.
- **Business-hours gate**: optional per-server `schedule` in the `TimeProfile` syntax (`"Mon-Fri 06:00-22:00"`) via `new TimeProfile { Schedule }.IsActiveAt(now)`; outside the window the checker returns `OutsideBusinessHours = true` and runs nothing.
- **Signals → Problems**: stuck file → `ActiveProblem.FileStuck` (ID `file_<dir>_<file>_stuck`); aggregated log match → `LogError` (ID `log_<scan>_<pattern>_match`); unreachable share/path → `FileShareUnreachable` (ID `file_<label>_unreachable`, Critical). Each problem carries the optional `WikiUrl` + `RemediationSteps` (new `ActiveProblem` fields), rendered in the Problems/Status dashboard, the HTML detail page and the **notifier mail**.
- **Config**: global toggle `features.fileMonitoring.enabled` + `Defaults` (StuckFileAgeMinutesWarning/Critical, MaxReportedItems — caps the reported findings to keep the HTTP payload small). Per-server `server.checks.fileMonitoring`: `schedule`, `directories[] { name, path, includePatterns[], excludePatterns[], recursive, ageMinutesWarning/Critical, cutoffTime, cutoffSeverity, wikiUrl, remediationSteps[] }`, `logScans[] { name, path, maxFileAgeMinutes, maxLines, wikiUrl, remediationSteps[], patterns[] { name, regex, severity, wikiUrl, remediationSteps[] } }`.
- **WebUI/HTML**: Servers/Edit „Datei-/Protokollüberwachung" card (`_FileMonitoringChecksPartial.cshtml`, client-side repeatable rows; log patterns entered as `Name :: Severity :: Regex` lines); Settings/Features global toggle + age defaults; Status/Details renders stuck-file + log-match tables with wiki links; HTML detail page shows a `📁 Dateiüberwachung` section + a `📁 N Fund(e)` badge.
- Problems flow through the normal pipeline; `RuntimeStatus.FileMonitoring` is included in the per-server status JSON published over HTTP so the notifier sees it.
- **Setup assistant (v2.32.0)**: Servers/Edit „Assistent“ (`_FileMonitoringAssistantPartial.cshtml`, modal inside the server form — inputs have no `name`, Enter is swallowed) guides through a directory (templates Übergabe-/Eingangs-/Fehlerordner, extension badges set the include filter) or a log scan (signature suggestions with hit counts, „Übernehmen“ appends `Name :: Severity :: Regex`) and fills a new form row. Dry run: `FileMonitoringPreviewService` (Checkers, uses `IFileSystemAccess` + `FileShareChecker.EvaluateDirectory`; size-limited output, 500 ms regex timeout, 20 s IO timeout, `SignatureTemplates`) behind `POST /api/filemonitoringtest/{directory|logscan}`. Request bodies are the form row view models, converted like a save. Files are read by the **WebUI process** (response names machine + account); reading is refused while `Authentication:Enabled=false`; pasted sample lines (`sampleText`) always work without file access.

### Central Notifications (config-driven, v2.16.0)
A worker whose hostname is listed in `features.notifications.notifierServers` becomes a **central notifier** (no `--notify` flag): it aggregates the status of all servers over HTTP and sends one summary e-mail per cycle for unacknowledged problems, each with an acknowledge deep-link. Designed to run on **two servers in parallel** (HA) without double-sends — and **without a shared lock or UNC share**.
- **HTTP-only coordination, all URLs derived**: each monitored server already publishes `wwwroot/status/{hostname}.status.json` (a `CentralServerStatus`, written by `StaticHtmlGenerator.SaveStatusJsonForHttpAsync`). All URLs are derived from the configured servers' `baseUrl`s: status (one per active server), peers (the other `notifierServers`, this host excluded), acknowledges + deep-link base (from `webUiServer`). The explicit URL fields are optional overrides only.
- **Claim-first dedup** (mirrors `EmailProbeSender`'s `.lastattempt`/`.lastsent`): per problem ID, `LastAttemptAt` is written **before** sending; `LastNotifiedAt` only after success. A problem is due when no instance has a `LastAttemptAt` (new), when `LastNotifiedAt` is older than `reNotifyIntervalHours` (reminder), or when a failed attempt's `RetryAfterMinutes` window elapsed. Staggered cron times + the cross-instance `max(LastAttemptAt)` check make duplicates rare; a missed send is impossible (a dead peer's stale/absent state just lets the other send).
- **Problems**: per server `ProblemDerivationService.DeriveProblems(..., acknowledges, serverId)` (sets `Acknowledged`). Scope filter = unacknowledged AND severity ≥ `minSeverity`. `notifyOnStaleServer` turns an unreachable HTTP GET or a `GeneratedAt` older than `staleThresholdMinutes` into `ActiveProblem.ServerStale` (ID `server_<name>_stale`, Critical, acknowledgeable).
- **Acknowledges via HTTP**: the central WebUI's `AcknowledgesController` publishes a read-only copy of active acknowledges to `wwwroot/status/acknowledges.json` after every mutation; the notifier reads it via `acknowledgesUrl` and additionally drops entries whose `ExpiresAt` has lapsed.
- **Deep-link**: each problem line links to `{webUiBaseUrl}/Problems?ackServer=<id>&ackProblem=<id>`. `Views/Problems/Index.cshtml` auto-opens its existing acknowledge modal pre-filled from those query params → `POST /api/acknowledges`.
- **Mail format (multipart/alternative, no Markdown)**: `NotificationMailRenderer` builds BOTH a plain-text body and a rich inline-styled HTML body (the SimpleAdmin template: dark header, severity stat band, per-server cards with KRITISCH/WARNUNG badges, remediation steps, wiki link + "Quittieren" button, green "Behoben / Quittiert" section). `SmtpMailRequest.HtmlBody` triggers a `multipart/alternative` send via MimeKit `BodyBuilder` so HTML clients render the template and everything else falls back to the plain text. All dynamic content is `WebUtility.HtmlEncode`d.
- **Resolved detail**: the all-clear notice states the exact problem that was cleared. `NotifyEntry` persists the problem `Message`/`Type`/`FirstSeenAt` at notify time so the "Behoben / Quittiert" section shows the original message (e.g. the failed SQL check), not just "a problem behoben". Older state files without `Message` fall back to the severity label.
- **Config** (`features.notifications`): `enabled`, **`notifierServers[]`** (which servers send — replaces `--notify`), **`webUiServer`** (deep-link/acknowledges host; default = first notifier), `minSeverity`, `notifyOnStaleServer`, `staleThresholdMinutes`, `reNotifyIntervalHours`, `retryAfterMinutes`, `sendResolvedNotice`, `smtpRef` (reuses a probe's SMTP), `recipients`, `subjectPrefix`. The URL fields (`statusUrls`, `peerNotifyStateUrls`, `acknowledgesUrl`, `webUiBaseUrl`, `localNotifyStatePath`) remain as optional overrides.
- **Shared SMTP**: `SmtpMailSender` (`ISmtpMailSender`) is the single MailKit send routine the notifier uses; `EmailProbeSender` still carries its own copy (optional future consolidation).
- **Test mail (v2.21.0)**: the "Test-Mail senden" button on Settings → Features (`POST /api/notificationtest/send`) verifies the full send path — resolves `smtpRef` with the same fallback logic, decrypts probe credentials, uses the current (unsaved) form values and reports the resolved source/host/recipients or a specific German error. All WebUI test fetches use `Url.Content("~/api/...")` so they work under IIS sub-application base paths (v2.21.1).
- **plainTextOnly (v2.22.2, default flipped in v2.22.3)**: `features.notifications.plainTextOnly` — **default `true`**: notifier mails are plain text; HTML (multipart) is opt-in via `plainTextOnly=false` (UI checkbox "HTML-Mails versenden", inversely bound). Rationale: mail filters were observed accepting multipart/HTML mails at SMTP level but silently quarantining them — the plain test mail arrives while HTML notifier mails vanish (HTML mails exist since v2.19.0).
- **Acknowledge until / indefinite (v2.25.0)**: the Problems acknowledge dialog offers 30 min … 30 days, "Bis Datum" (`CreateAcknowledgeRequest.ExpiresAt`, must be in the future → 400 otherwise; passed through `IAcknowledgeService.CreateAcknowledgeAsync(..., expiresAt)` / `Acknowledge.Create(..., expiresAt)`) and "Unbegrenzt" (`durationMinutes = 0`, no end date). `AcknowledgedBy` is taken from the authenticated Windows user. `ProblemDerivationService.ApplyAcknowledges` skips expired entries (`Acknowledge.IsEffective`) — expired acks stay `Active` until a worker processes its own file, so the WebUI previously kept showing them — and copies `AcknowledgeExpiresAt` / `AcknowledgedBy` onto `ActiveProblem` (shown in the Problems list with an "Aufheben" button → `DELETE /api/acknowledges/{id}`, and in the status report). The dead "Automatisch zurücksetzen" checkbox was removed from the dialog (`AutoResetOnHealthy` is stored but never evaluated).
- **Event log ignore from the UI + central evaluation (v2.25.0)**: every event in Status/Details has an "Ignorieren" button → `POST /api/eventlogignore` (`EventLogIgnoreController`) adds a server-scoped rule (source + event ID, 1/7/30/90 days, date or permanent, reason + user) to `features.eventLog.ignoreRules`. Matching moved to `Shared/Services/EventLogIgnoreMatcher` (EventLogChecker delegates). `EventLogIgnoreMatcher.ApplyCentral` filters a reported `EventLogState` (removes `RecentEvents`, lowers `EventCount`, re-evaluates status against `features.eventLog.defaults`, never escalates) and is applied by `StatusReaderService` and `NotificationService` — rules created in the central WebUI work for all servers without distributing config.json (approximate, since servers only report recent events).
- **Scheduled status report (v2.23.0)**: `features.notifications.statusReport` — `enabled` (default false), `times[]` (local `HH:mm`, default `07:00`/`13:00`/`17:00`; an entry may hold several times separated by `,`/`;`/space, which is how the WebUI posts them), `catchUpMinutes` (default 60: a missed slot is sent late within this window, later it is skipped), `plainTextOnly` (nullable, null = inherit the notifier's `plainTextOnly`). At each slot the notifier sends one overview mail in addition to the problem mails: every server with open warnings/errors (independent of `minSeverity`; unreachable/stale servers are always included even when `notifyOnStaleServer=false`), acknowledged problems as a short list and the servers without findings — or an "Alles OK" mail. HA dedup via `NotifyState.LastReportAttemptAt` / `LastReportSentAt` (claim-first, retry after `retryAfterMinutes`). Logic: `NotificationService.IsStatusReportDue` / `SendStatusReportAsync`, rendering: `NotificationMailRenderer.RenderStatusReport`. The WebUI form has fields for it, so a settings save keeps the values.
- **State publishing (v2.22.2)**: after every state save the notifier additionally publishes a copy of `notify-state.json` to `{wwwroot}/status/` (best-effort) so the peer's HTTP read works without configuring `localNotifyStatePath`. The canonical state location is unchanged (no migration/re-notification). A renderer failure now counts as a failed attempt (retry) instead of crashing the cycle, and the worker wraps the whole notifier cycle in try/catch — a notifier error can no longer kill the process. Inactivity reasons (disabled / hostname not in notifierServers) are logged at Debug.

## Technology Stack

- **.NET 10.0** target across all packages
- **ASP.NET Core MVC** (WebUI) as a **Razor Class Library**
- **System.Text.Json** with camelCase + null-omitting
- **Microsoft.Extensions.Hosting** for DI + logging
- **MailKit / MimeKit** for SMTP and IMAP
- **Cronos** for cron schedules
- **System.Diagnostics.PerformanceCounter** for Windows metrics
- **System.IO.Abstractions** for filesystem testability
- **xUnit + FluentAssertions + Moq** for tests

## Logging Configuration

Provider: Console only. Levels are fully controlled by the host's `appsettings.json`. Per-checker debug categories:

| Category | Emits at Debug |
|---|---|
| `Minicon.SimpleAdmin.Worker.SimpleAdminWorker` | Overall timing summary |
| `Minicon.SimpleAdmin.Checkers.AppPools` (and `.Services`, `.EventLog`, `.SqlQueries`, `.EmailProbes`, `.EmailDelivery`, `.Metrics`, `.Certificates`, `.BizTalk`, `.FileMonitoring`) | `[Checker] starting / completed in Xms / failed after Xms` |
| `Minicon.SimpleAdmin.Checkers.Services.EmailProbeSender` | Full SMTP step trace (connect → auth → send → disconnect) |
| `Minicon.SimpleAdmin.Checkers.Services.EmailDeliveryChecker` | Full IMAP step trace (connect → auth → open → search → fetch → expunge → close) |

Set only the category you care about to `"Debug"`. Disabled checkers produce no log line — absence from the output is the signal they are off.

## Development Notes

- **Bump versions in lock-step** across all seven `*.csproj` files; mixed versions break consumer resolution.
- **InternalsVisibleTo** is auto-added via `Directory.Build.props` — use `internal` freely for testable seams.
- **Cross-platform**: Windows-only checkers (AppPool, Certificate, EventLog, Services) return empty results gracefully on non-Windows; tests assume Linux/macOS in CI.
- **No emojis in commit messages** — German-language Conventional-style messages (see existing log).
- **Nullable reference types** enabled everywhere; `ImplicitUsings` on.
- **`sealed`** on classes not designed for inheritance.

## Repository Conventions

- Branch: `master`
- Commit style (from history): `<Topic v<version>>: <German summary>` (e.g. `Zertifikat-Überwachung in Pakete hochgezogen (v2.14.0)`).
- Co-author trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` is the established pattern when Claude helped author the change.
- `local-feed/` directory contains locally-built `.nupkg` artefacts for consumption by sibling repos via `NuGet.config`.
