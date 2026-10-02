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

        var servicesConfig = new ServerServicesConfig { Enabled = ServicesEnabled, Checks = serviceConfigs };

        // Return null when everything is in the disabled/unconfigured state (avoids serializing empty checks object)
        if (!AppPools.Enabled && !EventLogEnabled && !ServicesEnabled && serviceConfigs.Count == 0
            && sqlConfig == null && emailProbesConfig == null && emailDeliveryConfig == null
            && certConfig == null && biztalkConfig == null && fileMonConfig == null
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
            FileMonitoring = FileMonitoringChecksViewModel.FromServerFileMonitoringConfig(config.FileMonitoring)
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

    public WatchedDirectory ToWatchedDirectory() => new()
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
        RemediationSteps = string.Join("\n", d.RemediationSteps)
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
    public string? WikiUrl { get; set; }
    public string? RemediationSteps { get; set; }  // newline-separated

    public LogScanConfig ToLogScanConfig() => new()
    {
        Name = (Name ?? string.Empty).Trim(),
        Path = (Path ?? string.Empty).Trim(),
        MaxFileAgeMinutes = MaxFileAgeMinutes,
        MaxLines = MaxLines <= 0 ? 5000 : MaxLines,
        WikiUrl = string.IsNullOrWhiteSpace(WikiUrl) ? null : WikiUrl.Trim(),
        RemediationSteps = FileMonitoringChecksViewModel.SplitLines(RemediationSteps),
        Patterns = ParsePatterns(Patterns)
    };

    public static LogScanRowViewModel FromLogScanConfig(LogScanConfig s) => new()
    {
        Name = s.Name,
        Path = s.Path,
        MaxFileAgeMinutes = s.MaxFileAgeMinutes,
        MaxLines = s.MaxLines,
        WikiUrl = s.WikiUrl,
        RemediationSteps = string.Join("\n", s.RemediationSteps),
        Patterns = string.Join("\n", s.Patterns.Select(p =>
            $"{p.Name} :: {p.Severity} :: {p.Regex}"))
    };

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
