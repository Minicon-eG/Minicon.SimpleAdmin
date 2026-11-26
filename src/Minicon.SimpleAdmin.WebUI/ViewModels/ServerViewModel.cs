using System.ComponentModel.DataAnnotations;
using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.WebUI.ViewModels;

public class ServerViewModel
{
    [Required(ErrorMessage = "Server-Name ist erforderlich")]
    [RegularExpression(@"^[a-zA-Z0-9\-_\.]+$", ErrorMessage = "Nur Buchstaben, Zahlen, -, _ und . erlaubt")]
    [Display(Name = "Server-Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Beschreibung")]
    public string? Description { get; set; }

    [Display(Name = "Aktiv")]
    public bool Active { get; set; } = true;

    [RegularExpression(@"^(https?://.+)?$", ErrorMessage = "Ungültige URL (z.B. http://servername)")]
    [Display(Name = "Base URL")]
    public string? BaseUrl { get; set; }

    [Display(Name = "Service-Typen")]
    public List<ServiceTypeViewModel> ServiceTypes { get; set; } = new();

    [Display(Name = "Server-Checks")]
    public ServerChecksViewModel Checks { get; set; } = new();

    public Server ToServer(ServerChecksConfig? originalChecks = null)
    {
        return new Server
        {
            Name = Name,
            Description = Description,
            Active = Active,
            BaseUrl = BaseUrl,
            ServiceTypes = ServiceTypes.Select(st => st.ToServiceType()).ToList(),
            Checks = Checks.ToServerChecksConfig(originalChecks)
        };
    }

    public static ServerViewModel FromServer(Server server)
    {
        return new ServerViewModel
        {
            Name = server.Name,
            Description = server.Description,
            Active = server.Active,
            BaseUrl = server.BaseUrl,
            ServiceTypes = server.ServiceTypes.Select((st, i) => ServiceTypeViewModel.FromServiceType(st, i)).ToList(),
            Checks = ServerChecksViewModel.FromServerChecksConfig(server.Checks)
        };
    }
}

public class ServerChecksViewModel
{
    [Display(Name = "AppPool-Checks")]
    public AppPoolChecksViewModel AppPools { get; set; } = new();

    [Display(Name = "EventLog-Checks aktiviert")]
    public bool EventLogEnabled { get; set; }

    [Display(Name = "Windows Service-Checks aktiviert")]
    public bool ServicesEnabled { get; set; }

    [Display(Name = "Windows Service-Checks")]
    public List<ServiceCheckViewModel> Services { get; set; } = new();

    [Display(Name = "SQL Query Checks")]
    public SqlQueryChecksViewModel SqlQueryChecks { get; set; } = new();

    [Display(Name = "E-Mail-Probes (Sender)")]
    public EmailProbesViewModel EmailProbes { get; set; } = new();

    [Display(Name = "E-Mail-Zustellung (Checker)")]
    public EmailDeliveryViewModel EmailDelivery { get; set; } = new();

    [Display(Name = "Zertifikat-Checks")]
    public CertificateChecksViewModel Certificates { get; set; } = new();

    [Display(Name = "BizTalk-Überwachung")]
    public BizTalkChecksViewModel BizTalk { get; set; } = new();

    [Display(Name = "Datei-/Protokollüberwachung")]
    public FileMonitoringChecksViewModel FileMonitoring { get; set; } = new();

    [Display(Name = "Geplante Tasks")]
    public ScheduledTaskChecksViewModel ScheduledTasks { get; set; } = new();

    public ServerChecksConfig? ToServerChecksConfig(ServerChecksConfig? original = null)
    {
        var serviceConfigs = Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s.ToServiceCheckConfig())
            .ToList();

        var sqlConfig = SqlQueryChecks.ToServerSqlQueriesConfig();
        var emailProbesConfig = EmailProbes.ToServerEmailProbesConfig();
        var emailDeliveryConfig = EmailDelivery.ToServerEmailDeliveryConfig();
        var certConfig = Certificates.ToCertificateChecksConfig();
        var biztalkConfig = BizTalk.ToServerBizTalkConfig(original?.BizTalk);
        var fileMonConfig = FileMonitoring.ToServerFileMonitoringConfig();
        var scheduledTasksConfig = ScheduledTasks.ToServerScheduledTasksConfig();

        var servicesConfig = new ServerServicesConfig { Enabled = ServicesEnabled, Checks = serviceConfigs };

        // Return null when everything is in the disabled/unconfigured state (avoids serializing empty checks object)
        if (!AppPools.Enabled && !EventLogEnabled && !ServicesEnabled && serviceConfigs.Count == 0
            && sqlConfig == null && emailProbesConfig == null && emailDeliveryConfig == null
            && certConfig == null && biztalkConfig == null && fileMonConfig == null
            && scheduledTasksConfig == null
            && original?.Cpu == null && original?.Memory == null
            && (original?.Disks == null || original.Disks.Count == 0))
        {
            return null;
        }

        return new ServerChecksConfig
        {
            AppPools = AppPools.Enabled
                ? AppPools.ToAppPoolChecksConfig(original?.AppPools)
                : null,
            EventLog = EventLogEnabled
                ? new EventLogChecksConfig
                  {
                      Enabled = true,
                      Logs = original?.EventLog?.Logs ?? new()
                  }
                : null,
            Services = servicesConfig,
            SqlQueries = sqlConfig,
            EmailProbes = emailProbesConfig,
            EmailDelivery = emailDeliveryConfig,
            Certificates = certConfig,
            BizTalk = biztalkConfig,
            FileMonitoring = fileMonConfig,
            ScheduledTasks = scheduledTasksConfig,
            // Pass through fields not managed by the UI
            Cpu = original?.Cpu,
            Memory = original?.Memory,
            Disks = original?.Disks,
        };
    }

    public static ServerChecksViewModel FromServerChecksConfig(ServerChecksConfig? config)
    {
        if (config == null)
        {
            return new ServerChecksViewModel();
        }

        return new ServerChecksViewModel
        {
            AppPools = AppPoolChecksViewModel.FromAppPoolChecksConfig(config.AppPools),
            EventLogEnabled = config.EventLog?.Enabled ?? false,
            ServicesEnabled = config.Services?.Enabled ?? false,
            Services = config.Services?.Checks
                .Select((s, i) => ServiceCheckViewModel.FromServiceCheckConfig(s, i))
                .ToList() ?? new(),
            SqlQueryChecks = SqlQueryChecksViewModel.FromServerSqlQueriesConfig(config.SqlQueries),
            EmailProbes = EmailProbesViewModel.FromServerEmailProbesConfig(config.EmailProbes),
            EmailDelivery = EmailDeliveryViewModel.FromServerEmailDeliveryConfig(config.EmailDelivery),
            Certificates = CertificateChecksViewModel.FromCertificateChecksConfig(config.Certificates),
            BizTalk = BizTalkChecksViewModel.FromServerBizTalkConfig(config.BizTalk),
            FileMonitoring = FileMonitoringChecksViewModel.FromServerFileMonitoringConfig(config.FileMonitoring),
            ScheduledTasks = ScheduledTaskChecksViewModel.FromServerScheduledTasksConfig(config.ScheduledTasks)
        };
    }
}

// ── BizTalk Checks ViewModel ─────────────────────────────────────────────────

public class BizTalkChecksViewModel
{
    [Display(Name = "BizTalk-Überwachung aktiviert")]
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "http://localhost/BizTalkManagementService";
    public int? TimeoutSeconds { get; set; }

    public string ApplicationsMonitor { get; set; } = "all";
    public string ApplicationsExclude { get; set; } = string.Empty; // newline-separated
    public string OrchestrationsMonitor { get; set; } = "all";
    public string SendPortsMonitor { get; set; } = "all";
    public string ReceiveLocationsMonitor { get; set; } = "all";
    public string ReceiveLocationsDisabledSeverity { get; set; } = "Warning";

    public bool SuspendedEnabled { get; set; } = true;
    public int? SuspendedWarning { get; set; }
    public int? SuspendedCritical { get; set; }

    public ServerBizTalkConfig? ToServerBizTalkConfig(ServerBizTalkConfig? original = null)
    {
        if (!Enabled)
            return null;

        return new ServerBizTalkConfig
        {
            Enabled = true,
            BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "http://localhost/BizTalkManagementService" : BaseUrl.Trim(),
            TimeoutSeconds = TimeoutSeconds,
            Applications = new BizTalkArtifactMonitor
            {
                Monitor = ApplicationsMonitor,
                Include = original?.Applications.Include ?? new(),
                Exclude = SplitLines(ApplicationsExclude)
            },
            Orchestrations = new BizTalkArtifactMonitor
            {
                Monitor = OrchestrationsMonitor,
                Include = original?.Orchestrations.Include ?? new(),
                Exclude = original?.Orchestrations.Exclude ?? new()
            },
            SendPorts = new BizTalkArtifactMonitor
            {
                Monitor = SendPortsMonitor,
                Include = original?.SendPorts.Include ?? new(),
                Exclude = original?.SendPorts.Exclude ?? new()
            },
            ReceiveLocations = new BizTalkReceiveLocationMonitor
            {
                Monitor = ReceiveLocationsMonitor,
                DisabledSeverity = ReceiveLocationsDisabledSeverity,
                Include = original?.ReceiveLocations.Include ?? new(),
                Exclude = original?.ReceiveLocations.Exclude ?? new(),
                CriticalList = original?.ReceiveLocations.CriticalList ?? new()
            },
            SuspendedInstances = new BizTalkSuspendedConfig
            {
                Enabled = SuspendedEnabled,
                Warning = SuspendedWarning,
                Critical = SuspendedCritical,
                ByApplication = original?.SuspendedInstances.ByApplication ?? true
            }
        };
    }

    public static BizTalkChecksViewModel FromServerBizTalkConfig(ServerBizTalkConfig? config)
    {
        if (config == null) return new BizTalkChecksViewModel();
        return new BizTalkChecksViewModel
        {
            Enabled = config.Enabled,
            BaseUrl = config.BaseUrl,
            TimeoutSeconds = config.TimeoutSeconds,
            ApplicationsMonitor = config.Applications.Monitor,
            ApplicationsExclude = string.Join("\n", config.Applications.Exclude),
            OrchestrationsMonitor = config.Orchestrations.Monitor,
            SendPortsMonitor = config.SendPorts.Monitor,
            ReceiveLocationsMonitor = config.ReceiveLocations.Monitor,
            ReceiveLocationsDisabledSeverity = config.ReceiveLocations.DisabledSeverity,
            SuspendedEnabled = config.SuspendedInstances.Enabled,
            SuspendedWarning = config.SuspendedInstances.Warning,
            SuspendedCritical = config.SuspendedInstances.Critical
        };
    }

    private static List<string> SplitLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

// ── File Monitoring Checks ViewModels ────────────────────────────────────────

public class FileMonitoringChecksViewModel
{
    [Display(Name = "Datei-/Protokollüberwachung aktiviert")]
    public bool Enabled { get; set; }

    [Display(Name = "Betriebszeiten (optional)")]
    public string? Schedule { get; set; }

    public List<WatchedDirectoryRowViewModel> Directories { get; set; } = new();
    public List<LogScanRowViewModel> LogScans { get; set; } = new();

    public ServerFileMonitoringConfig? ToServerFileMonitoringConfig()
    {
        if (!Enabled)
            return null;

        return new ServerFileMonitoringConfig
        {
            Enabled = true,
            Schedule = string.IsNullOrWhiteSpace(Schedule) ? null : Schedule.Trim(),
            Directories = Directories
                .Where(d => !string.IsNullOrWhiteSpace(d.Path))
                .Select(d => d.ToWatchedDirectory())
                .ToList(),
            LogScans = LogScans
                .Where(s => !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => s.ToLogScanConfig())
                .ToList()
        };
    }

    public static FileMonitoringChecksViewModel FromServerFileMonitoringConfig(ServerFileMonitoringConfig? config)
    {
        if (config == null)
            return new FileMonitoringChecksViewModel();

        return new FileMonitoringChecksViewModel
        {
            Enabled = config.Enabled,
            Schedule = config.Schedule,
            Directories = config.Directories.Select(WatchedDirectoryRowViewModel.FromWatchedDirectory).ToList(),
            LogScans = config.LogScans.Select(LogScanRowViewModel.FromLogScanConfig).ToList()
        };
    }

    internal static List<string> SplitLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static List<string> SplitCsv(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public class WatchedDirectoryRowViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? IncludePatterns { get; set; }   // comma-separated
    public string? ExcludePatterns { get; set; }   // comma-separated
    public bool Recursive { get; set; }
    public int? AgeMinutesWarning { get; set; }
    public int? AgeMinutesCritical { get; set; }
    public string? CutoffTime { get; set; }
    public string CutoffSeverity { get; set; } = "Critical";
    public string? WikiUrl { get; set; }
    public string? RemediationSteps { get; set; }  // newline-separated

    /// <summary>
    /// The unknown fields of the original directory (JSON). They are carried through the form so
    /// that they are not lost when the list of directories is edited (e.g. after a duplicate).
    /// </summary>
    public string? UnknownFieldsJson { get; set; }

    public WatchedDirectory ToWatchedDirectory()
    {
        var directory = new WatchedDirectory
        {
            Name = (Name ?? string.Empty).Trim(),
            Path = (Path ?? string.Empty).Trim(),
            IncludePatterns = FileMonitoringChecksViewModel.SplitCsv(IncludePatterns),
            ExcludePatterns = FileMonitoringChecksViewModel.SplitCsv(ExcludePatterns),
            Recursive = Recursive,
            AgeMinutesWarning = AgeMinutesWarning,
            AgeMinutesCritical = AgeMinutesCritical,
            CutoffTime = string.IsNullOrWhiteSpace(CutoffTime) ? null : CutoffTime.Trim(),
            CutoffSeverity = string.IsNullOrWhiteSpace(CutoffSeverity) ? "Critical" : CutoffSeverity,
            WikiUrl = string.IsNullOrWhiteSpace(WikiUrl) ? null : WikiUrl.Trim(),
            RemediationSteps = FileMonitoringChecksViewModel.SplitLines(RemediationSteps)
        };

        // The unknown fields of the original directory.
        var unknown = LogScanRowViewModel.ParseUnknownFields(UnknownFieldsJson);
        if (unknown.Count > 0)
        {
            directory.ExtensionData = unknown;
        }

        return directory;
    }

    public static WatchedDirectoryRowViewModel FromWatchedDirectory(WatchedDirectory d) => new()
    {
        Name = d.Name,
        Path = d.Path,
        IncludePatterns = string.Join(", ", d.IncludePatterns),
        ExcludePatterns = string.Join(", ", d.ExcludePatterns),
        Recursive = d.Recursive,
        AgeMinutesWarning = d.AgeMinutesWarning,
        AgeMinutesCritical = d.AgeMinutesCritical,
        CutoffTime = d.CutoffTime,
        CutoffSeverity = d.CutoffSeverity,
        WikiUrl = d.WikiUrl,
        RemediationSteps = string.Join("\n", d.RemediationSteps),
        UnknownFieldsJson = LogScanRowViewModel.SerializeExtensionData(d.ExtensionData)
    };
}

public class LogScanRowViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? MaxFileAgeMinutes { get; set; }
    public int MaxLines { get; set; } = 5000;

    /// <summary>One signature per line: <c>Anzeigename :: Severity :: Regex</c> (Severity = Critical|Warning).</summary>
    public string? Patterns { get; set; }

    /// <summary>
    /// One "offener Lauf" rule per line, written as <c>key=value</c> pairs separated by "::":
    /// <c>name=… :: start=… :: end=… :: max=… :: severity=… :: abort=… :: tsRegex=… :: tsFormat=… ::
    /// reportAbandoned=… :: wiki=… :: steps=…</c>.
    /// Only <c>start</c> and <c>end</c> are mandatory; the rest have defaults (30 min, Critical, no
    /// abort regex, timestamp at the line start with "yyyy-MM-dd HH:mm:ss.fff", abandoned runs
    /// reported, no wiki link, no remediation steps).
    /// The older positional form (<c>Anzeigename :: StartRegex :: EndRegex :: …</c>) is still read
    /// for hand-edited configs, but it cannot carry every field and is therefore not written back.
    /// </summary>
    public string? PendingRules { get; set; }

    /// <summary>
    /// One "erwarteter Lauf" rule per line, written as <c>key=value</c> pairs separated by "::":
    /// <c>name=… :: match=… :: schedule=… :: time=… :: grace=… :: severity=… :: holidays=… :: wiki=… :: steps=…</c>.
    /// Only <c>match</c> (the line that proves the run happened) is mandatory; the rest have
    /// defaults (every day, 22:00, 120 min grace, Critical, no holidays).
    /// </summary>
    public string? ExpectedRuns { get; set; }

    public string? WikiUrl { get; set; }
    public string? RemediationSteps { get; set; }  // newline-separated

    /// <summary>
    /// The unknown fields of the original scan and of its rules (JSON), carried through the form.
    /// Rules are matched by name or by a unique fingerprint of their known fields, so duplicates,
    /// reordered rules and pure renames do not depend on list positions.
    /// <para>
    /// The JSON has the shape
    /// <c>{ "scan": { … }, "patterns": [ { … } ], "pendingRules": [ { … } ], "expectedRuns": [ { … } ] }</c>.
    /// </para>
    /// </summary>
    public string? UnknownFieldsJson { get; set; }

    public LogScanConfig ToLogScanConfig()
    {
        var unknown = ParseUnknownFields(UnknownFieldsJson);
        var scan = new LogScanConfig
        {
            Name = (Name ?? string.Empty).Trim(),
            Path = (Path ?? string.Empty).Trim(),
            MaxFileAgeMinutes = MaxFileAgeMinutes,
            MaxLines = MaxLines <= 0 ? 5000 : MaxLines,
            WikiUrl = string.IsNullOrWhiteSpace(WikiUrl) ? null : WikiUrl.Trim(),
            RemediationSteps = FileMonitoringChecksViewModel.SplitLines(RemediationSteps),
            Patterns = ParsePatterns(Patterns),
            PendingRules = ParsePendingRules(PendingRules),
            ExpectedRuns = ParseExpectedRuns(ExpectedRuns)
        };

        // The unknown fields of the scan itself.
        if (unknown.TryGetValue("scan", out var scanUnknown) && scanUnknown.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            scan.ExtensionData = ToDictionary(scanUnknown);
        }

        // The unknown fields of the rules — matched by their stable identity (the rule name).
        ApplyRuleUnknownFields(scan.Patterns, unknown, "patterns", p => p.Name);
        ApplyRuleUnknownFields(scan.PendingRules, unknown, "pendingRules", r => r.Name);
        ApplyRuleUnknownFields(scan.ExpectedRuns, unknown, "expectedRuns", r => r.Name);

        return scan;
    }

    /// <summary>
    /// Copies the unknown fields of the original rules onto the new ones. The rules are matched by
    /// their stable identity (the rule name); only when no name is available the position is used
    /// as a fallback. A reordered, inserted or deleted rule therefore never inherits the unknown
    /// fields of another rule.
    /// </summary>
    private static void ApplyRuleUnknownFields<T>(List<T> rules, Dictionary<string, System.Text.Json.JsonElement> unknown, string key, Func<T, string> nameSelector)
        where T : Minicon.SimpleAdmin.Models.Config.ConfigModel
    {
        if (!unknown.TryGetValue(key, out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        var original = list.EnumerateArray()
            .Where(element => element.ValueKind == System.Text.Json.JsonValueKind.Object)
            .ToList();
        var byName = original
            .Where(element => !string.IsNullOrWhiteSpace(ReadIdentity(element)))
            .GroupBy(ReadIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var name = nameSelector(rule);
            if (!string.IsNullOrWhiteSpace(name)
                && byName.TryGetValue(name, out var named)
                && named.Count == 1)
            {
                rule.ExtensionData = ToDictionaryWithoutIdentity(named[0]);
                continue;
            }

            var fingerprint = RuleFingerprint(rule);
            var matching = original
                .Where(element => string.Equals(ReadMetadata(element, RuleFingerprintKey), fingerprint, StringComparison.Ordinal))
                .ToList();
            if (matching.Count == 1)
            {
                rule.ExtensionData = ToDictionaryWithoutIdentity(matching[0]);
            }
        }
    }

    private static string ReadIdentity(System.Text.Json.JsonElement element)
        => ReadMetadata(element, RuleIdentityKey);

    private static string ReadMetadata(System.Text.Json.JsonElement element, string key)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(key, StringComparison.Ordinal)
                && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, System.Text.Json.JsonElement> ToDictionaryWithoutIdentity(System.Text.Json.JsonElement element)
    {
        var result = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(RuleIdentityKey, StringComparison.Ordinal)
                || property.Name.Equals(RuleFingerprintKey, StringComparison.Ordinal))
            {
                continue;
            }

            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>Converts a JSON object into a dictionary of extension data.</summary>
    internal static Dictionary<string, System.Text.Json.JsonElement> ToDictionary(System.Text.Json.JsonElement element)
    {
        var result = new Dictionary<string, System.Text.Json.JsonElement>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>
    /// Reads the unknown fields of the original scan and its rules from their JSON representation.
    /// Invalid or missing content yields an empty dictionary (no exception).
    /// </summary>
    internal static Dictionary<string, System.Text.Json.JsonElement> ParseUnknownFields(string? json)
    {
        var result = new Dictionary<string, System.Text.Json.JsonElement>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed field must not break the save.
        }

        return result;
    }

    /// <summary>
    /// Serializes the unknown fields of a scan and its rules (for the hidden form field).
    /// <para>
    /// The rules are stored with their name and a fingerprint of their known fields so that the
    /// unknown fields can be matched after reordering or a pure rename — ambiguous rules never
    /// inherit another rule's fields.
    /// </para>
    /// </summary>
    internal static string? SerializeUnknownFields(LogScanConfig scan)
    {
        if (scan == null)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>();
        if (scan.ExtensionData.Count > 0)
        {
            payload["scan"] = scan.ExtensionData;
        }

        if (scan.Patterns.Any(p => p.ExtensionData.Count > 0))
        {
            payload["patterns"] = scan.Patterns.Select(p => WithIdentity(p.Name, RuleFingerprint(p), p.ExtensionData)).ToList();
        }

        if (scan.PendingRules.Any(r => r.ExtensionData.Count > 0))
        {
            payload["pendingRules"] = scan.PendingRules.Select(r => WithIdentity(r.Name, RuleFingerprint(r), r.ExtensionData)).ToList();
        }

        if (scan.ExpectedRuns.Any(r => r.ExtensionData.Count > 0))
        {
            payload["expectedRuns"] = scan.ExpectedRuns.Select(r => WithIdentity(r.Name, RuleFingerprint(r), r.ExtensionData)).ToList();
        }

        if (payload.Count == 0)
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string RuleFingerprint<T>(T rule)
    {
        object payload = rule switch
        {
            Minicon.SimpleAdmin.Models.Config.LogPatternConfig pattern => new
            {
                pattern.Severity,
                pattern.Regex,
                pattern.WikiUrl,
                pattern.RemediationSteps
            },
            Minicon.SimpleAdmin.Models.Config.LogPendingRule pending => new
            {
                pending.StartRegex,
                pending.EndRegex,
                pending.AbortRegex,
                pending.MaxMinutes,
                pending.Severity,
                pending.ReportAbandoned,
                pending.TimestampRegex,
                pending.TimestampFormat,
                pending.WikiUrl,
                pending.RemediationSteps
            },
            Minicon.SimpleAdmin.Models.Config.ExpectedRunRule expected => new
            {
                expected.MatchRegex,
                expected.Schedule,
                expected.ExpectedTime,
                expected.GraceMinutes,
                expected.Severity,
                expected.Holidays,
                expected.WikiUrl,
                expected.RemediationSteps
            },
            _ => new { }
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static Dictionary<string, System.Text.Json.JsonElement> WithIdentity(
        string? name,
        string fingerprint,
        Dictionary<string, System.Text.Json.JsonElement> extensionData)
    {
        var result = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(name))
        {
            result[RuleIdentityKey] = System.Text.Json.JsonDocument
                .Parse(System.Text.Json.JsonSerializer.Serialize(name))
                .RootElement.Clone();
        }

        result[RuleFingerprintKey] = System.Text.Json.JsonDocument
            .Parse(System.Text.Json.JsonSerializer.Serialize(fingerprint))
            .RootElement.Clone();
        foreach (var field in extensionData)
        {
            result[field.Key] = field.Value;
        }

        return result;
    }

    internal const string RuleIdentityKey = "$identity";
    internal const string RuleFingerprintKey = "$fingerprint";

    /// <summary>
    /// Serializes the unknown fields of a single object (e.g. a watched directory) for the hidden
    /// form field. Returns null when there are no unknown fields.
    /// </summary>
    internal static string? SerializeExtensionData(Dictionary<string, System.Text.Json.JsonElement>? extensionData)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(extensionData);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static LogScanRowViewModel FromLogScanConfig(LogScanConfig s) => new()
    {
        Name = s.Name,
        Path = s.Path,
        MaxFileAgeMinutes = s.MaxFileAgeMinutes,
        MaxLines = s.MaxLines,
        WikiUrl = s.WikiUrl,
        RemediationSteps = string.Join("\n", s.RemediationSteps),
        Patterns = string.Join("\n", s.Patterns.Select(p =>
            $"{p.Name} :: {p.Severity} :: {p.Regex}")),
        PendingRules = string.Join("\n", s.PendingRules.Select(SerializePendingRule)),
        ExpectedRuns = string.Join("\n", s.ExpectedRuns.Select(SerializeExpectedRun)),
        UnknownFieldsJson = SerializeUnknownFields(s)
    };

    /// <summary>
    /// Writes a pending rule in the keyed format (<c>key=value</c> pairs separated by "::").
    /// Every field is written explicitly so that the rule survives a save/load round trip
    /// unchanged — in particular <c>reportAbandoned=false</c>, the wiki link and the
    /// remediation steps, which the older positional format could not carry.
    /// <para>
    /// The values are escaped (see <see cref="EscapeValue"/>), so a value that contains the
    /// separator "::" — e.g. the regular expression <c>waiting\s+::\s+Result</c> — survives the
    /// round trip unchanged.
    /// </para>
    /// </summary>
    private static string SerializePendingRule(LogPendingRule r)
    {
        var parts = new List<string>
        {
            $"name={EscapeValue(r.Name)}",
            $"start={EscapeValue(r.StartRegex)}",
            $"end={EscapeValue(r.EndRegex)}",
            $"max={r.MaxMinutes}",
            $"severity={r.Severity}",
            $"reportAbandoned={(r.ReportAbandoned ? "true" : "false")}",
            $"tsFormat={EscapeValue(r.TimestampFormat)}"
        };
        if (!string.IsNullOrWhiteSpace(r.AbortRegex))
            parts.Add($"abort={EscapeValue(r.AbortRegex)}");
        if (!string.IsNullOrWhiteSpace(r.TimestampRegex))
            parts.Add($"tsRegex={EscapeValue(r.TimestampRegex)}");
        if (!string.IsNullOrWhiteSpace(r.WikiUrl))
            parts.Add($"wiki={EscapeValue(r.WikiUrl)}");
        if (r.RemediationSteps.Count > 0)
            parts.Add($"steps={SerializeSteps(r.RemediationSteps)}");
        return string.Join(" :: ", parts);
    }

    /// <summary>
    /// Writes an expected-run rule in the keyed format (<c>key=value</c> pairs separated by "::").
    /// </summary>
    private static string SerializeExpectedRun(ExpectedRunRule r)
    {
        var parts = new List<string>
        {
            $"name={EscapeValue(r.Name)}",
            $"match={EscapeValue(r.MatchRegex)}",
            $"schedule={EscapeValue(r.Schedule)}",
            $"time={EscapeValue(r.ExpectedTime)}",
            $"grace={r.GraceMinutes}",
            $"severity={r.Severity}"
        };
        if (r.Holidays.Count > 0)
            parts.Add($"holidays={EscapeValue(string.Join(",", r.Holidays))}");
        if (!string.IsNullOrWhiteSpace(r.WikiUrl))
            parts.Add($"wiki={EscapeValue(r.WikiUrl)}");
        if (r.RemediationSteps.Count > 0)
            parts.Add($"steps={SerializeSteps(r.RemediationSteps)}");
        return string.Join(" :: ", parts);
    }

    /// <summary>
    /// Escapes a value for the keyed rule format: the separator "::", the list separator "|" and
    /// the escape character "\" are escaped with a backslash, so that any value (regular
    /// expression, wiki link, remediation steps) survives the round trip unchanged.
    /// </summary>
    internal static string EscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("::", "\\::", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>
    /// Serializes the remediation steps: every step is escaped on its own, the steps are joined with
    /// an <em>unescaped</em> "|". A step that itself contains a "|" therefore survives the round
    /// trip unchanged.
    /// </summary>
    internal static string SerializeSteps(IEnumerable<string> steps)
        => string.Join("|", steps.Select(EscapeValue));

    /// <summary>
    /// Reads the remediation steps of a rule: the value is split at the unescaped "|" and every
    /// step is unescaped afterwards. A step that itself contains a "|" (escaped as "\|") stays one
    /// step.
    /// </summary>
    internal static List<string> ParseSteps(string rawValue)
    {
        var result = new List<string>();
        foreach (var step in SplitOnUnescapedPipe(rawValue))
        {
            var unescaped = UnescapeValue(step.Trim());
            if (unescaped.Length > 0)
            {
                result.Add(unescaped);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a value at the "|" characters that are not escaped with a backslash.
    /// </summary>
    internal static List<string> SplitOnUnescapedPipe(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;

        foreach (var c in value)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                current.Append(c);
                escaped = true;
                continue;
            }

            if (c == '|')
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Reverses <see cref="EscapeValue"/>.
    /// </summary>
    internal static string UnescapeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == '\\' || next == ':' || next == '|')
                {
                    result.Append(next);
                    i++;
                    continue;
                }
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Splits a rule line into its segments at the unescaped separator "::". An escaped
    /// "\::" does not split — the value stays in one piece and is unescaped afterwards.
    /// </summary>
    internal static List<string> SplitSegments(string line)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                // Keep the escape character — the value is unescaped after the split.
                current.Append(c);
                escaped = true;
                continue;
            }

            if (c == ':' && i + 1 < line.Length && line[i + 1] == ':')
            {
                segments.Add(current.ToString().Trim());
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
        }

        segments.Add(current.ToString().Trim());
        return segments;
    }

    /// <summary>
    /// Parses the "erwartete Läufe" rules from the one-per-line text format
    /// (<c>key=value</c> pairs separated by "::").
    /// </summary>
    private static List<ExpectedRunRule> ParseExpectedRuns(string? raw)
    {
        var result = new List<ExpectedRunRule>();
        foreach (var line in FileMonitoringChecksViewModel.SplitLines(raw))
        {
            var rule = new ExpectedRunRule();
            foreach (var segment in SplitSegments(line))
            {
                var separator = segment.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = segment[..separator].Trim().ToLowerInvariant();
                var rawValue = segment[(separator + 1)..].Trim();
                var value = UnescapeValue(rawValue);
                switch (key)
                {
                    case "name":
                        rule.Name = value;
                        break;
                    case "match":
                        rule.MatchRegex = value;
                        break;
                    case "schedule":
                        rule.Schedule = value;
                        break;
                    case "time":
                        rule.ExpectedTime = value;
                        break;
                    case "grace":
                        if (int.TryParse(value, out var grace) && grace > 0)
                            rule.GraceMinutes = grace;
                        break;
                    case "severity":
                        if (!string.IsNullOrWhiteSpace(value))
                            rule.Severity = NormalizeSeverity(value);
                        break;
                    case "holidays":
                        rule.Holidays = value
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();
                        break;
                    case "wiki":
                        rule.WikiUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                        break;
                    case "steps":
                        // The list separator "|" is only unescaped per step — the raw value is
                        // needed to tell a separator from an escaped "|" inside a step.
                        rule.RemediationSteps = ParseSteps(rawValue);
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.MatchRegex))
            {
                result.Add(rule);
            }
        }

        return result;
    }

    private static List<LogPatternConfig> ParsePatterns(string? raw)
    {
        var result = new List<LogPatternConfig>();
        foreach (var line in FileMonitoringChecksViewModel.SplitLines(raw))
        {
            var parts = line.Split("::", 3, StringSplitOptions.TrimEntries);
            var pattern = parts.Length switch
            {
                >= 3 => new LogPatternConfig { Name = parts[0], Severity = NormalizeSeverity(parts[1]), Regex = parts[2] },
                2 => new LogPatternConfig { Name = parts[0], Severity = "Critical", Regex = parts[1] },
                _ => new LogPatternConfig { Name = parts[0], Severity = "Critical", Regex = parts[0] }
            };
            if (!string.IsNullOrWhiteSpace(pattern.Regex))
                result.Add(pattern);
        }
        return result;
    }

    /// <summary>
    /// Parses the "offene Läufe" rules from the one-per-line text format.
    /// The current format is a list of <c>key=value</c> pairs separated by "::" (order
    /// independent). The older positional format (<c>Anzeigename :: StartRegex :: EndRegex ::
    /// MaxMinutes :: Severity :: …</c>) is still accepted so that hand-edited configs keep
    /// working; it simply cannot express every field.
    /// </summary>
    private static List<LogPendingRule> ParsePendingRules(string? raw)
    {
        var result = new List<LogPendingRule>();
        foreach (var line in FileMonitoringChecksViewModel.SplitLines(raw))
        {
            var rule = ParsePendingRule(line);
            if (rule is not null && !string.IsNullOrWhiteSpace(rule.StartRegex))
                result.Add(rule);
        }

        return result;
    }

    private static LogPendingRule? ParsePendingRule(string line)
    {
        // Keyed format: every segment carries "key=value". A segment without "=" means the line
        // uses the older positional format.
        var segments = SplitSegments(line);
        if (segments.Any(s => !s.Contains('=')))
            return ParsePendingRulePositional(segments.ToArray());

        var rule = new LogPendingRule();
        foreach (var segment in segments)
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = segment[..separator].Trim();
            var rawValue = segment[(separator + 1)..].Trim();
            var value = UnescapeValue(rawValue);
            switch (key.ToLowerInvariant())
            {
                case "name":
                    rule.Name = value;
                    break;
                case "start":
                    rule.StartRegex = value;
                    break;
                case "end":
                    rule.EndRegex = value;
                    break;
                case "max":
                    if (int.TryParse(value, out var maxMinutes) && maxMinutes > 0)
                        rule.MaxMinutes = maxMinutes;
                    break;
                case "severity":
                    if (!string.IsNullOrWhiteSpace(value))
                        rule.Severity = NormalizeSeverity(value);
                    break;
                case "abort":
                    rule.AbortRegex = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "tsregex":
                    rule.TimestampRegex = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "tsformat":
                    if (!string.IsNullOrWhiteSpace(value))
                        rule.TimestampFormat = value;
                    break;
                case "reportabandoned":
                    rule.ReportAbandoned = IsTrue(value);
                    break;
                case "wiki":
                    rule.WikiUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "steps":
                    // The list separator "|" is only unescaped per step — the raw value is needed
                    // to tell a separator from an escaped "|" inside a step.
                    rule.RemediationSteps = ParseSteps(rawValue);
                    break;
            }
        }

        return rule;
    }

    /// <summary>
    /// Reads the older positional format. The field order is
    /// <c>Name :: StartRegex :: EndRegex :: MaxMinutes :: Severity :: AbortRegex :: TimestampRegex ::
    /// TimestampFormat</c>. Fields that are not written are not shifted — a missing optional field
    /// simply ends the line.
    /// </summary>
    private static LogPendingRule? ParsePendingRulePositional(string[] parts)
    {
        if (parts.Length < 3)
            return null;

        var rule = new LogPendingRule
        {
            Name = parts[0],
            StartRegex = parts[1],
            EndRegex = parts[2],
            MaxMinutes = 30,
            Severity = "Critical"
        };

        if (parts.Length > 3 && int.TryParse(parts[3], out var maxMinutes) && maxMinutes > 0)
            rule.MaxMinutes = maxMinutes;

        if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]))
            rule.Severity = NormalizeSeverity(parts[4]);

        if (parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]))
            rule.AbortRegex = parts[5];

        if (parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6]))
            rule.TimestampRegex = parts[6];

        if (parts.Length > 7 && !string.IsNullOrWhiteSpace(parts[7]))
            rule.TimestampFormat = parts[7];

        return rule;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.Ordinal) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("ja", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSeverity(string value)
        => string.Equals(value, "Warning", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Critical";
}

// ── Certificate Checks ViewModels ────────────────────────────────────────────

public class CertificateChecksViewModel
{
    [Display(Name = "Zertifikat-Überwachung aktiviert")]
    public bool Enabled { get; set; }

    public List<CertificateCheckEditorViewModel> Checks { get; set; } = new();

    public CertificateChecksConfig? ToCertificateChecksConfig()
    {
        var checks = Checks
            .Where(c => !string.IsNullOrWhiteSpace(c.Source))
            .Select(c => c.ToCertificateCheckConfig())
            .ToList();

        if (!Enabled && checks.Count == 0)
            return null;

        return new CertificateChecksConfig { Enabled = Enabled, Checks = checks };
    }

    public static CertificateChecksViewModel FromCertificateChecksConfig(CertificateChecksConfig? config)
    {
        if (config == null) return new CertificateChecksViewModel();
        return new CertificateChecksViewModel
        {
            Enabled = config.Enabled,
            Checks = config.Checks.Select((c, i) => CertificateCheckEditorViewModel.FromCertificateCheckConfig(c, i)).ToList()
        };
    }
}

public class CertificateCheckEditorViewModel
{
    public int Index { get; set; }

    [Display(Name = "Quelle")]
    public string Source { get; set; } = "store://LocalMachine/My";

    [Display(Name = "Subject enthält")]
    public string? SubjectContains { get; set; }

    [Display(Name = "Issuer enthält")]
    public string? IssuerContains { get; set; }

    [Display(Name = "Thumbprint")]
    public string? Thumbprint { get; set; }

    [Display(Name = "Friendly Name enthält")]
    public string? FriendlyNameContains { get; set; }

    [Display(Name = "Warning (Tage)")]
    public int? WarningDays { get; set; }

    [Display(Name = "Critical (Tage)")]
    public int? CriticalDays { get; set; }

    public CertificateCheckConfig ToCertificateCheckConfig()
    {
        CertificateFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(SubjectContains)
            || !string.IsNullOrWhiteSpace(IssuerContains)
            || !string.IsNullOrWhiteSpace(Thumbprint)
            || !string.IsNullOrWhiteSpace(FriendlyNameContains))
        {
            filter = new CertificateFilter
            {
                SubjectContains = string.IsNullOrWhiteSpace(SubjectContains) ? null : SubjectContains,
                IssuerContains = string.IsNullOrWhiteSpace(IssuerContains) ? null : IssuerContains,
                Thumbprint = string.IsNullOrWhiteSpace(Thumbprint) ? null : Thumbprint,
                FriendlyNameContains = string.IsNullOrWhiteSpace(FriendlyNameContains) ? null : FriendlyNameContains
            };
        }

        return new CertificateCheckConfig
        {
            Source = Source,
            Filter = filter,
            WarningDays = WarningDays,
            CriticalDays = CriticalDays
        };
    }

    public static CertificateCheckEditorViewModel FromCertificateCheckConfig(CertificateCheckConfig config, int index)
    {
        return new CertificateCheckEditorViewModel
        {
            Index = index,
            Source = string.IsNullOrWhiteSpace(config.Source) ? "store://LocalMachine/My" : config.Source,
            SubjectContains = config.Filter?.SubjectContains,
            IssuerContains = config.Filter?.IssuerContains,
            Thumbprint = config.Filter?.Thumbprint,
            FriendlyNameContains = config.Filter?.FriendlyNameContains,
            WarningDays = config.WarningDays,
            CriticalDays = config.CriticalDays
        };
    }
}

// ── SQL Query Checks ViewModels ──────────────────────────────────────────────

public class SqlQueryChecksViewModel
{
    public bool Enabled { get; set; }
    public List<SqlQueryCheckViewModel> Checks { get; set; } = new();

    public ServerSqlQueriesConfig? ToServerSqlQueriesConfig()
    {
        var checks = Checks
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.ConnectionString))
            .Select(c => c.ToSqlQueryCheckConfig())
            .ToList();

        if (!Enabled && checks.Count == 0)
            return null;

        return new ServerSqlQueriesConfig
        {
            Enabled = Enabled,
            Checks = checks
        };
    }

    public static SqlQueryChecksViewModel FromServerSqlQueriesConfig(ServerSqlQueriesConfig? config)
    {
        if (config == null)
            return new SqlQueryChecksViewModel();

        return new SqlQueryChecksViewModel
        {
            Enabled = config.Enabled,
            Checks = config.Checks
                .Select((c, i) => SqlQueryCheckViewModel.FromSqlQueryCheckConfig(c, i))
                .ToList()
        };
    }
}

public class SqlQueryCheckViewModel
{
    public int Index { get; set; }

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Beschreibung")]
    public string Description { get; set; } = string.Empty;

    [Display(Name = "Connection String")]
    public string ConnectionString { get; set; } = string.Empty;

    [Display(Name = "Timeout (Sekunden)")]
    public int? TimeoutSeconds { get; set; }

    public List<SqlQueryViewModel> Queries { get; set; } = new();

    public SqlQueryCheckConfig ToSqlQueryCheckConfig()
    {
        return new SqlQueryCheckConfig
        {
            Name = Name,
            Description = Description,
            ConnectionString = ConnectionString,
            TimeoutSeconds = TimeoutSeconds,
            Queries = Queries
                .Where(q => !string.IsNullOrWhiteSpace(q.Sql))
                .Select(q => q.ToSqlQueryConfig())
                .ToList()
        };
    }

    public static SqlQueryCheckViewModel FromSqlQueryCheckConfig(SqlQueryCheckConfig config, int index)
    {
        return new SqlQueryCheckViewModel
        {
            Index = index,
            Name = config.Name,
            Description = config.Description,
            ConnectionString = config.ConnectionString,
            TimeoutSeconds = config.TimeoutSeconds,
            Queries = config.Queries
                .Select((q, i) => SqlQueryViewModel.FromSqlQueryConfig(q, i))
                .ToList()
        };
    }
}

public class SqlQueryViewModel
{
    public int Index { get; set; }
    public int CheckIndex { get; set; }

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "SQL")]
    public string Sql { get; set; } = string.Empty;

    // Row count assertion (optional — bound via nullable fields)
    public bool HasRowCount { get; set; }
    public int RowCountExpected { get; set; }
    public string RowCountOperator { get; set; } = "==";

    public List<SqlColumnAssertionViewModel> ColumnAssertions { get; set; } = new();

    public SqlQueryConfig ToSqlQueryConfig()
    {
        return new SqlQueryConfig
        {
            Name = Name,
            Sql = Sql,
            RowCount = HasRowCount
                ? new SqlRowCountAssertion { ExpectedCount = RowCountExpected, Operator = RowCountOperator }
                : null,
            ColumnAssertions = ColumnAssertions
                .Where(a => !string.IsNullOrWhiteSpace(a.Column))
                .Select(a => a.ToSqlColumnAssertion())
                .ToList()
        };
    }

    public static SqlQueryViewModel FromSqlQueryConfig(SqlQueryConfig config, int index)
    {
        return new SqlQueryViewModel
        {
            Index = index,
            Name = config.Name,
            Sql = config.Sql,
            HasRowCount = config.RowCount != null,
            RowCountExpected = config.RowCount?.ExpectedCount ?? 0,
            RowCountOperator = config.RowCount?.Operator ?? "==",
            ColumnAssertions = config.ColumnAssertions
                .Select((a, i) => SqlColumnAssertionViewModel.FromSqlColumnAssertion(a, i))
                .ToList()
        };
    }
}

public class SqlColumnAssertionViewModel
{
    public int Index { get; set; }

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Spalte")]
    public string Column { get; set; } = string.Empty;

    [Display(Name = "Erwarteter Wert")]
    public string ExpectedValue { get; set; } = string.Empty;

    [Display(Name = "Operator")]
    public string Operator { get; set; } = "==";

    [Display(Name = "Typ")]
    public string ValueType { get; set; } = "String";

    public SqlColumnAssertion ToSqlColumnAssertion()
    {
        return new SqlColumnAssertion
        {
            Name = Name,
            Column = Column,
            ExpectedValue = ExpectedValue,
            Operator = Operator,
            ValueType = Enum.TryParse<SqlValueType>(ValueType, out var vt) ? vt : SqlValueType.String
        };
    }

    public static SqlColumnAssertionViewModel FromSqlColumnAssertion(SqlColumnAssertion config, int index)
    {
        return new SqlColumnAssertionViewModel
        {
            Index = index,
            Name = config.Name,
            Column = config.Column,
            ExpectedValue = config.ExpectedValue,
            Operator = config.Operator,
            ValueType = config.ValueType.ToString()
        };
    }
}

public class ServiceCheckViewModel
{
    public int Index { get; set; }

    [Display(Name = "Service-Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Anzeigename")]
    public string? DisplayName { get; set; }

    [Display(Name = "Automatisch")]
    public bool MonitorAutomatic { get; set; } = true;

    [Display(Name = "Manuell")]
    public bool MonitorManual { get; set; } = false;

    public ServiceCheckConfig ToServiceCheckConfig()
    {
        return new ServiceCheckConfig
        {
            Name = Name,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName,
            MonitorAutomatic = MonitorAutomatic,
            MonitorManual = MonitorManual
        };
    }

    public static ServiceCheckViewModel FromServiceCheckConfig(ServiceCheckConfig config, int index)
    {
        return new ServiceCheckViewModel
        {
            Index = index,
            Name = config.Name,
            DisplayName = config.DisplayName,
            MonitorAutomatic = config.MonitorAutomatic,
            MonitorManual = config.MonitorManual
        };
    }
}

/// <summary>
/// ViewModel for AppPool checks configuration
/// Supports two modes: Auto-Discovery (with ignore list) or Manual (with explicit pool list)
/// </summary>
public class AppPoolChecksViewModel
{
    [Display(Name = "AppPool-Überwachung aktiviert")]
    public bool Enabled { get; set; }

    [Display(Name = "Alle Pools automatisch prüfen")]
    public bool DiscoverAll { get; set; } = false;

    [Display(Name = "Ignorierte Pools")]
    public List<string> IgnoredPools { get; set; } = new();

    [Display(Name = "Manuell konfigurierte Pools")]
    public List<string> ManualPools { get; set; } = new();

    public AppPoolChecksConfig ToAppPoolChecksConfig(AppPoolChecksConfig? original = null)
    {
        var originalByName = original?.Pools?
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase)
            ?? new();

        return new AppPoolChecksConfig
        {
            Enabled = Enabled,
            DiscoverAll = DiscoverAll,
            IgnoredPools = IgnoredPools,
            Pools = DiscoverAll
                ? new List<AppPoolConfig>()
                : ManualPools.Select(name =>
                    originalByName.TryGetValue(name, out var orig)
                        ? orig
                        : new AppPoolConfig { Name = name, Enabled = true }
                  ).ToList()
        };
    }

    public static AppPoolChecksViewModel FromAppPoolChecksConfig(AppPoolChecksConfig? config)
    {
        if (config == null)
        {
            return new AppPoolChecksViewModel();
        }

        return new AppPoolChecksViewModel
        {
            Enabled = config.Enabled,
            DiscoverAll = config.DiscoverAll,
            IgnoredPools = config.IgnoredPools ?? new List<string>(),
            ManualPools = config.Pools?.Select(p => p.Name).ToList() ?? new List<string>()
        };
    }
}

public class AppPoolConfigViewModel
{
    public int Index { get; set; }

    [Required(ErrorMessage = "AppPool-Name ist erforderlich")]
    [Display(Name = "AppPool-Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Anzeigename")]
    public string? DisplayName { get; set; }

    [Display(Name = "Aktiviert")]
    public bool Enabled { get; set; } = true;

    [Display(Name = "Memory Warning (MB)")]
    public int? MemoryWarningMB { get; set; }

    [Display(Name = "Memory Critical (MB)")]
    public int? MemoryCriticalMB { get; set; }

    [Display(Name = "Uptime Warning (Stunden)")]
    public int? UptimeWarningHours { get; set; }

    public AppPoolConfig ToAppPoolConfig()
    {
        return new AppPoolConfig
        {
            Name = Name,
            DisplayName = DisplayName,
            Enabled = Enabled,
            MemoryWarningMB = MemoryWarningMB,
            MemoryCriticalMB = MemoryCriticalMB,
            UptimeWarningHours = UptimeWarningHours,
        };
    }

    public static AppPoolConfigViewModel FromAppPoolConfig(AppPoolConfig config, int index)
    {
        return new AppPoolConfigViewModel
        {
            Index = index,
            Name = config.Name,
            DisplayName = config.DisplayName,
            Enabled = config.Enabled,
            MemoryWarningMB = config.MemoryWarningMB,
            MemoryCriticalMB = config.MemoryCriticalMB,
            UptimeWarningHours = config.UptimeWarningHours,
        };
    }
}

// ── Email Probes ViewModels (Sender role) ────────────────────────────────────

public class EmailProbesViewModel
{
    public bool Enabled { get; set; }
    public List<EmailProbeEditorViewModel> Probes { get; set; } = new();

    public ServerEmailProbesConfig? ToServerEmailProbesConfig()
    {
        var probes = Probes
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.ToEmailProbeConfig())
            .ToList();

        if (!Enabled && probes.Count == 0)
            return null;

        return new ServerEmailProbesConfig { Enabled = Enabled, Probes = probes };
    }

    public static EmailProbesViewModel FromServerEmailProbesConfig(ServerEmailProbesConfig? config)
    {
        if (config == null) return new EmailProbesViewModel();
        return new EmailProbesViewModel
        {
            Enabled = config.Enabled,
            Probes = config.Probes.Select((p, i) => EmailProbeEditorViewModel.FromEmailProbeConfig(p, i)).ToList()
        };
    }
}

public class EmailProbeEditorViewModel
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 25;
    public string SmtpSslMode { get; set; } = "false";
    public bool SmtpUseSsl { get; set; } = false;
    public string SmtpFrom { get; set; } = string.Empty;
    public string SmtpTo { get; set; } = string.Empty;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int SendIntervalMinutes { get; set; } = 15;
    public int? SmtpTimeoutSeconds { get; set; }

    private static string ResolveSslMode(string? sslMode, bool useSsl)
        => NormalizeSslMode(sslMode ?? (useSsl ? "true" : "false"));

    private static string NormalizeSslMode(string? sslMode)
    {
        var normalized = (sslMode ?? "false").Trim().ToLowerInvariant();
        return normalized is "true" or "false" or "auto" ? normalized : "false";
    }

    private static bool SslModeToUseSsl(string? sslMode)
        => string.Equals(NormalizeSslMode(sslMode), "true", StringComparison.OrdinalIgnoreCase);

    public EmailProbeConfig ToEmailProbeConfig()
    {
        return new EmailProbeConfig
        {
            Name = Name,
            Description = Description,
            Smtp = new SmtpConfig
            {
                Host = SmtpHost,
                Port = SmtpPort,
                SslMode = NormalizeSslMode(SmtpSslMode),
                UseSsl = SslModeToUseSsl(SmtpSslMode),
                From = SmtpFrom,
                To = SmtpTo,
                Username = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername,
                Password = string.IsNullOrWhiteSpace(SmtpPassword) ? null : SmtpPassword,
                TimeoutSeconds = SmtpTimeoutSeconds
            },
            Subject = Subject,
            SendIntervalMinutes = SendIntervalMinutes
        };
    }

    public static EmailProbeEditorViewModel FromEmailProbeConfig(EmailProbeConfig config, int index)
    {
        return new EmailProbeEditorViewModel
        {
            Index = index,
            Name = config.Name,
            Description = config.Description,
            SmtpHost = config.Smtp.Host,
            SmtpPort = config.Smtp.Port,
            SmtpSslMode = ResolveSslMode(config.Smtp.SslMode, config.Smtp.UseSsl),
            SmtpUseSsl = config.Smtp.UseSsl,
            SmtpFrom = config.Smtp.From,
            SmtpTo = config.Smtp.To,
            SmtpUsername = config.Smtp.Username,
            SmtpPassword = config.Smtp.Password,
            Subject = config.Subject,
            SendIntervalMinutes = config.SendIntervalMinutes,
            SmtpTimeoutSeconds = config.Smtp.TimeoutSeconds
        };
    }
}

// ── Email Delivery ViewModels (Checker role) ─────────────────────────────────

public class EmailDeliveryViewModel
{
    public bool Enabled { get; set; }
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public string ImapSslMode { get; set; } = "true";
    public bool ImapUseSsl { get; set; } = true;
    public string ImapUsername { get; set; } = string.Empty;
    public string ImapPassword { get; set; } = string.Empty;
    public string ImapFolder { get; set; } = "INBOX";
    public int? ImapTimeoutSeconds { get; set; }
    public List<EmailDeliveryCheckEditorViewModel> Checks { get; set; } = new();

    private static string ResolveSslMode(string? sslMode, bool useSsl)
        => NormalizeSslMode(sslMode ?? (useSsl ? "true" : "false"));

    private static string NormalizeSslMode(string? sslMode)
    {
        var normalized = (sslMode ?? "true").Trim().ToLowerInvariant();
        return normalized is "true" or "false" or "auto" ? normalized : "true";
    }

    private static bool SslModeToUseSsl(string? sslMode)
        => string.Equals(NormalizeSslMode(sslMode), "true", StringComparison.OrdinalIgnoreCase);

    public ServerEmailDeliveryConfig? ToServerEmailDeliveryConfig()
    {
        var checks = Checks
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.SubjectFilter))
            .Select(c => c.ToEmailDeliveryCheckConfig())
            .ToList();

        if (!Enabled && string.IsNullOrWhiteSpace(ImapHost) && checks.Count == 0)
            return null;

        return new ServerEmailDeliveryConfig
        {
            Enabled = Enabled,
            Imap = new ImapConfig
            {
                Host = ImapHost,
                Port = ImapPort,
                SslMode = NormalizeSslMode(ImapSslMode),
                UseSsl = SslModeToUseSsl(ImapSslMode),
                Username = ImapUsername,
                Password = ImapPassword,
                Folder = string.IsNullOrWhiteSpace(ImapFolder) ? "INBOX" : ImapFolder,
                TimeoutSeconds = ImapTimeoutSeconds
            },
            Checks = checks
        };
    }

    public static EmailDeliveryViewModel FromServerEmailDeliveryConfig(ServerEmailDeliveryConfig? config)
    {
        if (config == null) return new EmailDeliveryViewModel();
        return new EmailDeliveryViewModel
        {
            Enabled = config.Enabled,
            ImapHost = config.Imap.Host,
            ImapPort = config.Imap.Port,
            ImapSslMode = ResolveSslMode(config.Imap.SslMode, config.Imap.UseSsl),
            ImapUseSsl = config.Imap.UseSsl,
            ImapUsername = config.Imap.Username,
            ImapPassword = config.Imap.Password,
            ImapFolder = config.Imap.Folder,
            ImapTimeoutSeconds = config.Imap.TimeoutSeconds,
            Checks = config.Checks.Select((c, i) => EmailDeliveryCheckEditorViewModel.FromEmailDeliveryCheckConfig(c, i)).ToList()
        };
    }
}

public class EmailDeliveryCheckEditorViewModel
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubjectFilter { get; set; } = string.Empty;
    public int MaxAgeWarningMinutes { get; set; } = 20;
    public int MaxAgeCriticalMinutes { get; set; } = 60;
    public bool DeleteAfterCheck { get; set; } = true;

    public EmailDeliveryCheckConfig ToEmailDeliveryCheckConfig()
    {
        return new EmailDeliveryCheckConfig
        {
            Name = Name,
            Description = Description,
            SubjectFilter = SubjectFilter,
            MaxAgeWarningMinutes = MaxAgeWarningMinutes,
            MaxAgeCriticalMinutes = MaxAgeCriticalMinutes,
            DeleteAfterCheck = DeleteAfterCheck
        };
    }

    public static EmailDeliveryCheckEditorViewModel FromEmailDeliveryCheckConfig(EmailDeliveryCheckConfig config, int index)
    {
        return new EmailDeliveryCheckEditorViewModel
        {
            Index = index,
            Name = config.Name,
            Description = config.Description,
            SubjectFilter = config.SubjectFilter,
            MaxAgeWarningMinutes = config.MaxAgeWarningMinutes,
            MaxAgeCriticalMinutes = config.MaxAgeCriticalMinutes,
            DeleteAfterCheck = config.DeleteAfterCheck
        };
    }
}

public class ServiceTypeViewModel
{
    public int Index { get; set; }

    [Required(ErrorMessage = "Service-Typ ist erforderlich")]
    [Display(Name = "Service-Typ")]
    public string Type { get; set; } = string.Empty;

    public PrtgConfigViewModel Prtg { get; set; } = new();
    public NetScalerConfigViewModel NetScaler { get; set; } = new();

    public ServiceType ToServiceType()
    {
        return new ServiceType
        {
            Type = Type,
            Prtg = Prtg.ToPrtgConfig(),
            NetScaler = NetScaler.ToNetScalerConfig()
        };
    }

    public static ServiceTypeViewModel FromServiceType(ServiceType st, int index)
    {
        return new ServiceTypeViewModel
        {
            Index = index,
            Type = st.Type,
            Prtg = PrtgConfigViewModel.FromPrtgConfig(st.Prtg, index),
            NetScaler = NetScalerConfigViewModel.FromNetScalerConfig(st.NetScaler, index)
        };
    }
}

public class PrtgConfigViewModel
{
    public int ServiceIndex { get; set; }

    [Display(Name = "PRTG aktiviert")]
    public bool Enabled { get; set; }

    [Display(Name = "Admin-Pfad")]
    public string AdminPath { get; set; } = string.Empty;

    public List<PrtgCheckViewModel> Checks { get; set; } = new();

    public PrtgConfig ToPrtgConfig()
    {
        return new PrtgConfig
        {
            Enabled = Enabled,
            AdminPath = AdminPath,
            Checks = Checks.Select(c => c.ToPrtgCheck()).ToList()
        };
    }

    public static PrtgConfigViewModel FromPrtgConfig(PrtgConfig config, int serviceIndex)
    {
        return new PrtgConfigViewModel
        {
            ServiceIndex = serviceIndex,
            Enabled = config.Enabled,
            AdminPath = config.AdminPath,
            Checks = config.Checks.Select((c, i) => PrtgCheckViewModel.FromPrtgCheck(c, serviceIndex, i)).ToList()
        };
    }
}

public class PrtgCheckViewModel
{
    public int ServiceIndex { get; set; }
    public int CheckIndex { get; set; }

    [Required(ErrorMessage = "Metrik ist erforderlich")]
    [Display(Name = "Metrik")]
    public string Metric { get; set; } = "cpu";

    [Display(Name = "Anzeigename")]
    public string? DisplayName { get; set; }

    [Display(Name = "Ziel (für disk)")]
    public string? Target { get; set; }

    public ThresholdViewModel Threshold { get; set; } = new();

    public PrtgCheck ToPrtgCheck()
    {
        return new PrtgCheck
        {
            Metric = Metric,
            DisplayName = DisplayName,
            Target = Target,
            Threshold = Threshold.ToThreshold()
        };
    }

    public static PrtgCheckViewModel FromPrtgCheck(PrtgCheck check, int serviceIndex, int checkIndex)
    {
        return new PrtgCheckViewModel
        {
            ServiceIndex = serviceIndex,
            CheckIndex = checkIndex,
            Metric = check.Metric,
            DisplayName = check.DisplayName,
            Target = check.Target,
            Threshold = ThresholdViewModel.FromThreshold(check.Threshold)
        };
    }
}

public class ThresholdViewModel
{
    [Range(0, 100, ErrorMessage = "Warning muss zwischen 0 und 100 liegen")]
    [Display(Name = "Warning")]
    public double? Warning { get; set; } = 70;

    [Range(0, 100, ErrorMessage = "Critical muss zwischen 0 und 100 liegen")]
    [Display(Name = "Critical")]
    public double? Critical { get; set; } = 85;

    [Required(ErrorMessage = "Operator ist erforderlich")]
    [Display(Name = "Operator")]
    public string Operator { get; set; } = ">";

    public Threshold ToThreshold()
    {
        return new Threshold
        {
            Warning = Warning,
            Critical = Critical,
            Operator = Operator
        };
    }

    public static ThresholdViewModel FromThreshold(Threshold? threshold)
    {
        if (threshold == null) return new ThresholdViewModel();

        return new ThresholdViewModel
        {
            Warning = threshold.Warning,
            Critical = threshold.Critical,
            Operator = threshold.Operator
        };
    }
}

public class NetScalerConfigViewModel
{
    public int ServiceIndex { get; set; }

    [Display(Name = "NetScaler aktiviert")]
    public bool Enabled { get; set; }

    [Display(Name = "Admin-Pfad")]
    public string AdminPath { get; set; } = string.Empty;

    public List<HealthCheckViewModel> HealthChecks { get; set; } = new();

    public NetScalerConfig ToNetScalerConfig()
    {
        return new NetScalerConfig
        {
            Enabled = Enabled,
            AdminPath = AdminPath,
            HealthChecks = HealthChecks.Select(hc => hc.ToHealthCheck()).ToList()
        };
    }

    public static NetScalerConfigViewModel FromNetScalerConfig(NetScalerConfig config, int serviceIndex)
    {
        return new NetScalerConfigViewModel
        {
            ServiceIndex = serviceIndex,
            Enabled = config.Enabled,
            AdminPath = config.AdminPath,
            HealthChecks = config.HealthChecks.Select((hc, i) => HealthCheckViewModel.FromHealthCheck(hc, serviceIndex, i)).ToList()
        };
    }
}

public class HealthCheckViewModel
{
    public int ServiceIndex { get; set; }
    public int CheckIndex { get; set; }

    [Required(ErrorMessage = "Name ist erforderlich")]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "HTTP-Methode ist erforderlich")]
    [Display(Name = "HTTP-Methode")]
    public string Method { get; set; } = "GET";

    [Required(ErrorMessage = "Pfad ist erforderlich")]
    [Display(Name = "Pfad")]
    public string Path { get; set; } = string.Empty;

    [Display(Name = "Payload")]
    public string? Payload { get; set; }

    [Display(Name = "Erwartete Status-Codes")]
    public string ExpectedStatusCodes { get; set; } = "200";

    [Display(Name = "Body enthält")]
    public string? BodyContains { get; set; }

    [Display(Name = "Erwartete Header")]
    public string? ExpectedHeaders { get; set; }

    [Display(Name = "JsonPath-Assertions")]
    public string? JsonPathAssertions { get; set; }

    [Range(5, 3600, ErrorMessage = "Intervall muss zwischen 5 und 3600 Sekunden liegen")]
    [Display(Name = "Intervall (Sekunden)")]
    public int? IntervalSeconds { get; set; } = 30;

    [Range(1000, 60000, ErrorMessage = "Timeout muss zwischen 1000 und 60000 ms liegen")]
    [Display(Name = "Timeout (ms)")]
    public int? TimeoutMs { get; set; } = 5000;

    public HealthCheck ToHealthCheck()
    {
        var statusCodes = ExpectedStatusCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var code) ? code : 200)
            .ToList();

        var bodyContains = string.IsNullOrWhiteSpace(BodyContains)
            ? null
            : BodyContains.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

        var headers = ParseHeaders(ExpectedHeaders);
        var jsonPath = ParseJsonPath(JsonPathAssertions);

        return new HealthCheck
        {
            Name = Name,
            Method = Method,
            Path = Path,
            Payload = Payload,
            Expected = new ExpectedResponse
            {
                StatusCodes = statusCodes,
                BodyContains = bodyContains,
                Headers = headers,
                JsonPath = jsonPath
            },
            IntervalSeconds = IntervalSeconds,
            TimeoutMs = TimeoutMs
        };
    }

    private static Dictionary<string, string>? ParseHeaders(string? headersText)
    {
        if (string.IsNullOrWhiteSpace(headersText)) return null;

        var result = new Dictionary<string, string>();
        foreach (var line in headersText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                result[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static List<JsonPathAssertion>? ParseJsonPath(string? jsonPathText)
    {
        if (string.IsNullOrWhiteSpace(jsonPathText)) return null;

        var result = new List<JsonPathAssertion>();
        foreach (var line in jsonPathText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: $.path == value oder $.path != value
            var match = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\$[^\s]+)\s*(==|!=|>|<|>=|<=)\s*(.+)$");
            if (match.Success)
            {
                result.Add(new JsonPathAssertion
                {
                    Path = match.Groups[1].Value,
                    Operator = match.Groups[2].Value,
                    Value = match.Groups[3].Value.Trim()
                });
            }
        }
        return result.Count > 0 ? result : null;
    }

    public static HealthCheckViewModel FromHealthCheck(HealthCheck hc, int serviceIndex, int checkIndex)
    {
        return new HealthCheckViewModel
        {
            ServiceIndex = serviceIndex,
            CheckIndex = checkIndex,
            Name = hc.Name,
            Method = hc.Method,
            Path = hc.Path,
            Payload = hc.Payload,
            ExpectedStatusCodes = hc.Expected?.StatusCodes != null
                ? string.Join(", ", hc.Expected.StatusCodes)
                : "200",
            BodyContains = hc.Expected?.BodyContains != null
                ? string.Join("\n", hc.Expected.BodyContains)
                : null,
            ExpectedHeaders = hc.Expected?.Headers != null
                ? string.Join("\n", hc.Expected.Headers.Select(kv => $"{kv.Key}: {kv.Value}"))
                : null,
            JsonPathAssertions = hc.Expected?.JsonPath != null
                ? string.Join("\n", hc.Expected.JsonPath.Select(jp => $"{jp.Path} {jp.Operator} {jp.Value}"))
                : null,
            IntervalSeconds = hc.IntervalSeconds,
            TimeoutMs = hc.TimeoutMs
        };
    }
}
