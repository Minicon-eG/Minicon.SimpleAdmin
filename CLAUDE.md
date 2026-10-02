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
- All package `Version` properties are kept in sync across the `*.csproj` files (currently `2.20.1`).
- Bump every package's `<Version>` together — mixed package versions across the seven projects will break consumers because they all depend on each other.
- `GeneratePackageOnBuild=true` is set on each publishable project, so a Release build emits `.nupkg` files automatically.

### Debug Symbols (v2.20.1)
- `src/Directory.Build.props` sets `DebugType=embedded` + `EmbedAllSources=true` for every project, so each DLL carries an **embedded portable PDB including the full C# sources**. Consumers can debug and step into the original code without a symbol server. No separate `.snupkg` is produced (`IncludeSymbols=false`). Trade-off: larger DLLs and the source is visible inside every package.

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
| `Minicon.SimpleAdmin.WebUI` | Razor Class Library — full admin UI (MVC + REST) | `SimpleAdminWebUIExtensions.AddSimpleAdminWebUI/UseSimpleAdminWebUI`, controllers, views, view models, `ConfigurationService`, `StatusReaderService` |
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
WebUI          (depends on Shared, Services)
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

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSimpleAdminWebUI(builder.Configuration);

var app = builder.Build();
app.UseSimpleAdminWebUI();
app.Run();
```
Host `appsettings.json` keys: `ConfigPath` (directory containing `config.json`; empty = the app directory itself, e.g. the IIS app folder — v2.24.0), `StatusDirectory`, optional `BasePath` for IIS sub-applications, `Encryption:ConnectionStringKey`, optional `Authentication:Enabled` / `Authentication:RequiredRole` (see "WebUI Authentication").

`AddSimpleAdmin(IServiceCollection, IConfiguration, HostPaths)` is exposed publicly so a host can also customise the worker's DI registrations before `BuildHost`.

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
| `/Documentation` | `DocumentationController` | Built-in docs page |

### REST API (all under `/api/...`)
- `GET /api/status[/{id}][/metrics|/apppools|/services]` — `Api/StatusController`
- `GET /api/problems[/server/{id}]`, `GET /api/problems/{id}?serverId=` — `Api/ProblemsController`
- `GET|POST|PUT|DELETE /api/acknowledges[/{id}|/server/{id}|/{id}/extend]` — `Api/AcknowledgesController`
- `POST /api/sqlquerytest/{connection|query}` — `Api/SqlQueryTestController`
- `POST /api/emailtest/{smtp|imap}` — `Api/EmailTestController`
- `POST /api/notificationtest/send` — `Api/NotificationTestController` (resolves the notifier's `smtpRef` exactly like `NotificationService` and sends a test mail; accepts current form values so it works before saving)

### WebUI Services
- **`StatusReaderService`** (10 s cache) — reads from `output.centralOutputPath` (if set) or `StatusDirectory` and **merges** all sources (v2.23.1): single `RuntimeStatus` files in the folder and its `status/` subfolder plus combined `*.status.json` files; per server/service the newest `LastUpdate` wins (tie: single file, it has history). Before v2.23.1 the first non-empty source won, so a central host whose local worker writes into the push folder only showed itself. **HTTP pull (v2.24.0)**: with `output.pullStatusOverHttp` (default true) it additionally fetches `{baseUrl}/status/{name}.status.json` of every active server from config.json in parallel (named HttpClient `SimpleAdminStatus`, 5 s timeout) and merges it the same way; an active server with neither HTTP nor file data gets a placeholder entry (service `erreichbarkeit`, status Unknown). A central WebUI thus shows all servers with no shared folder. It merges active acknowledges into problem derivation, supplies `BaseUrl` from config for static HTML links. `CalculateServiceStatus` and `CalculateOverallStatus` consider all checker types (incl. certificates) for the no-metrics-fallback and the severity escalation.
- **`ConfigurationService`** — in-memory config management with `HasChanges`, full CRUD, atomic save (temp file + `File.Move`), `MutateAndSaveAsync` wraps every mutation in a `SemaphoreSlim` write-lock + reverts via `ReloadFromFileAsync` if the save fails. Encryption is applied to a clone so `_config` is never mutated by the save path. Edit POST stays on the same page (does NOT redirect).

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

### Status Smoothing (Alert Fatigue Prevention)
Status changes require N consecutive checks showing the same result (default `minConsecutiveChecks: 5`). `StatusEvaluator.GetConfirmedStatus()` re-evaluates each snapshot's value against current thresholds. If mixed → returns the most frequent; ties broken in favor of least severe. The same mechanism is applied to email probe/delivery via `ConsecutiveFailures` carry-forward.

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
