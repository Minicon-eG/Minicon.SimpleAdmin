namespace Minicon.SimpleAdmin.Models.State;

/// <summary>
/// Represents an active problem on a server
/// </summary>
public class ActiveProblem
{
    /// <summary>
    /// Unique problem ID (e.g., "apppool_ConfigServiceAppPool_stopped")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Problem type (cpu, memory, disk, service, apppool, eventlog, certificate)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Problem source (e.g., "AppPool", "Service", "Metric")
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Problem severity
    /// </summary>
    public ProblemSeverity Severity { get; set; } = ProblemSeverity.Warning;

    /// <summary>
    /// Human-readable problem message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When the problem was first detected
    /// </summary>
    public DateTime Since { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Alias for Since - when the problem first occurred
    /// </summary>
    public DateTime FirstOccurrence
    {
        get => Since;
        set => Since = value;
    }

    /// <summary>
    /// When the problem was last observed
    /// </summary>
    public DateTime LastOccurrence { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this problem is acknowledged
    /// </summary>
    public bool Acknowledged { get; set; }

    /// <summary>
    /// ID of the acknowledge if acknowledged
    /// </summary>
    public string? AcknowledgeId { get; set; }

    /// <summary>End of the acknowledge (UTC); null while acknowledged = indefinite. Only set when <see cref="Acknowledged"/>.</summary>
    public DateTime? AcknowledgeExpiresAt { get; set; }

    /// <summary>Who acknowledged the problem. Only set when <see cref="Acknowledged"/>.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// Additional context data for the problem
    /// </summary>
    public Dictionary<string, object> Context { get; set; } = new();

    /// <summary>
    /// Optional link to a wiki / runbook article describing how to resolve this problem.
    /// </summary>
    public string? WikiUrl { get; set; }

    /// <summary>
    /// Optional step-by-step remediation hints shown alongside the problem (dashboard, mail, HTML).
    /// </summary>
    public List<string> RemediationSteps { get; set; } = new();

    /// <summary>
    /// Creates a problem ID from components
    /// </summary>
    public static string CreateId(string type, string component, string issue)
    {
        return $"{type}_{component}_{issue}".ToLowerInvariant();
    }

    /// <summary>
    /// Creates an AppPool stopped problem
    /// </summary>
    public static ActiveProblem AppPoolStopped(string poolName, string? reason = null)
    {
        return new ActiveProblem
        {
            Id = CreateId("apppool", poolName, "stopped"),
            Type = "apppool",
            Severity = ProblemSeverity.Critical,
            Message = $"AppPool '{poolName}' ist gestoppt" + (reason != null ? $": {reason}" : ""),
            Context = new Dictionary<string, object>
            {
                ["poolName"] = poolName,
                ["stopReason"] = reason ?? "Unknown"
            }
        };
    }

    /// <summary>
    /// Creates an AppPool high memory problem
    /// </summary>
    public static ActiveProblem AppPoolHighMemory(string poolName, double memoryMB, double thresholdMB, bool isCritical)
    {
        return new ActiveProblem
        {
            Id = CreateId("apppool", poolName, "memory"),
            Type = "apppool",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"AppPool '{poolName}': Memory {memoryMB:N0} MB ({(isCritical ? "Kritisch" : "Warnung")} > {thresholdMB:N0} MB)",
            Context = new Dictionary<string, object>
            {
                ["poolName"] = poolName,
                ["memoryMB"] = memoryMB,
                ["thresholdMB"] = thresholdMB
            }
        };
    }

    /// <summary>
    /// Creates an AppPool long uptime problem (advisory warning to consider recycling)
    /// </summary>
    public static ActiveProblem AppPoolLongUptime(string poolName, double uptimeHours, int thresholdHours)
    {
        return new ActiveProblem
        {
            Id = CreateId("apppool", poolName, "uptime"),
            Type = "apppool",
            Severity = ProblemSeverity.Warning,
            Message = $"AppPool '{poolName}': Uptime {uptimeHours:N0} h (Warning > {thresholdHours} h) – Neustart empfohlen",
            Context = new Dictionary<string, object>
            {
                ["poolName"] = poolName,
                ["uptimeHours"] = uptimeHours,
                ["thresholdHours"] = thresholdHours
            }
        };
    }

    /// <summary>
    /// Creates a CPU high usage problem
    /// </summary>
    public static ActiveProblem CpuHigh(double value, double threshold, bool isCritical)
    {
        return new ActiveProblem
        {
            Id = CreateId("cpu", "usage", isCritical ? "critical" : "warning"),
            Type = "cpu",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"CPU: {value:N1}% ({(isCritical ? "Critical" : "Warning")} > {threshold:N0}%)",
            Context = new Dictionary<string, object>
            {
                ["value"] = value,
                ["threshold"] = threshold
            }
        };
    }

    /// <summary>
    /// Creates a memory high usage problem
    /// </summary>
    public static ActiveProblem MemoryHigh(double value, double threshold, bool isCritical)
    {
        return new ActiveProblem
        {
            Id = CreateId("memory", "usage", isCritical ? "critical" : "warning"),
            Type = "memory",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"Memory: {value:N1}% ({(isCritical ? "Critical" : "Warning")} > {threshold:N0}%)",
            Context = new Dictionary<string, object>
            {
                ["value"] = value,
                ["threshold"] = threshold
            }
        };
    }

    /// <summary>
    /// Creates a disk high usage problem
    /// </summary>
    public static ActiveProblem DiskHigh(string drive, double value, double threshold, bool isCritical)
    {
        return new ActiveProblem
        {
            Id = CreateId("disk", drive.TrimEnd(':'), isCritical ? "critical" : "warning"),
            Type = "disk",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"Disk {drive}: {value:N1}% ({(isCritical ? "Critical" : "Warning")} > {threshold:N0}%)",
            Context = new Dictionary<string, object>
            {
                ["drive"] = drive,
                ["value"] = value,
                ["threshold"] = threshold
            }
        };
    }

    /// <summary>
    /// Creates a service down problem
    /// </summary>
    public static ActiveProblem ServiceDown(string serviceName, string? displayName = null, string? startupType = null)
    {
        var startartLabel = startupType switch
        {
            "Automatic" => " (Startart: Automatisch)",
            "Manual"    => " (Startart: Manuell)",
            _           => string.Empty
        };

        var context = new Dictionary<string, object>
        {
            ["serviceName"] = serviceName,
            ["displayName"] = displayName ?? serviceName
        };
        if (startupType != null)
            context["startupType"] = startupType;

        return new ActiveProblem
        {
            Id = CreateId("service", serviceName, "stopped"),
            Type = "service",
            Severity = ProblemSeverity.Critical,
            Message = $"Service '{displayName ?? serviceName}' ist nicht gestartet{startartLabel}",
            Context = context
        };
    }

    /// <summary>
    /// Creates a problem when a wildcard service pattern matches no services
    /// </summary>
    public static ActiveProblem ServicePatternNoMatch(string pattern, string? displayName = null)
    {
        return new ActiveProblem
        {
            Id = CreateId("service", pattern.Replace("*", "_").Replace("?", "_"), "nomatch"),
            Type = "service",
            Severity = ProblemSeverity.Critical,
            Message = $"Kein Windows-Dienst gefunden für Muster '{displayName ?? pattern}' ({pattern})",
            Context = new Dictionary<string, object>
            {
                ["pattern"] = pattern,
                ["displayName"] = displayName ?? pattern
            }
        };
    }

    /// <summary>
    /// Creates an Event Log errors problem
    /// </summary>
    public static ActiveProblem EventLogErrors(string logSummary, int eventCount, bool isCritical)
    {
        return new ActiveProblem
        {
            Id = CreateId("eventlog", "errors", isCritical ? "critical" : "warning"),
            Type = "eventlog",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"Event Log: {eventCount} Fehler-Ereignisse" +
                      (string.IsNullOrEmpty(logSummary) ? "" : $" ({logSummary})"),
            Context = new Dictionary<string, object> { ["eventCount"] = eventCount }
        };
    }

    /// <summary>
    /// Creates a SQL query check connection-error problem
    /// </summary>
    public static ActiveProblem SqlQueryConnectionError(string checkId, string checkName, string error)
    {
        return new ActiveProblem
        {
            Id = CreateId("sqlquery", checkId, "connection_error"),
            Type = "sqlquery",
            Severity = ProblemSeverity.Critical,
            Message = $"SQL-Prüfung '{checkName}': Verbindung fehlgeschlagen — {error}",
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["checkName"] = checkName,
                ["error"] = error
            }
        };
    }

    /// <summary>
    /// Creates a SQL query execution-error problem
    /// </summary>
    public static ActiveProblem SqlQueryExecutionError(string checkId, string checkName, string queryName, string error)
    {
        return new ActiveProblem
        {
            Id = CreateId("sqlquery", $"{checkId}_{queryName}", "error"),
            Type = "sqlquery",
            Severity = ProblemSeverity.Critical,
            Message = $"SQL-Prüfung '{checkName}' / '{queryName}': Ausführungsfehler — {error}",
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["queryName"] = queryName,
                ["error"] = error
            }
        };
    }

    /// <summary>
    /// Creates a SQL query row-count mismatch problem
    /// </summary>
    public static ActiveProblem SqlQueryRowCountMismatch(
        string checkId,
        string checkName,
        string queryName,
        int actual,
        string op,
        int expected)
    {
        return new ActiveProblem
        {
            Id = CreateId("sqlquery", $"{checkId}_{queryName}", "rowcount_mismatch"),
            Type = "sqlquery",
            Severity = ProblemSeverity.Critical,
            Message = $"SQL-Prüfung '{checkName}' / '{queryName}': Erwartete Zeilenanzahl {op} {expected}, erhalten {actual}",
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["queryName"] = queryName,
                ["actualCount"] = actual,
                ["expectedCount"] = expected,
                ["operator"] = op
            }
        };
    }

    /// <summary>
    /// Creates a SQL query column-assertion mismatch problem
    /// </summary>
    public static ActiveProblem SqlQueryColumnMismatch(
        string checkId,
        string checkName,
        string queryName,
        string assertionName,
        string? actual,
        string op,
        string expected,
        string? failureReason)
    {
        return new ActiveProblem
        {
            Id = CreateId("sqlquery", $"{checkId}_{queryName}_{assertionName}", "column_mismatch"),
            Type = "sqlquery",
            Severity = ProblemSeverity.Critical,
            Message = $"SQL-Prüfung '{checkName}' / '{queryName}' / '{assertionName}': {failureReason ?? $"Erwartet {op} '{expected}', erhalten '{actual}'"}",
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["queryName"] = queryName,
                ["assertionName"] = assertionName,
                ["actualValue"] = actual ?? "(null)",
                ["expectedValue"] = expected,
                ["operator"] = op
            }
        };
    }

    /// <summary>
    /// Creates an email probe SMTP send failure problem (sender role)
    /// </summary>
    public static ActiveProblem EmailProbeSmtpError(string probeId, string probeName, string error)
    {
        return new ActiveProblem
        {
            Id = CreateId("emailprobe", probeId, "smtp_error"),
            Type = "emailprobe",
            Severity = ProblemSeverity.Critical,
            Message = $"E-Mail-Probe '{probeName}': SMTP-Fehler — {error}",
            Context = new Dictionary<string, object>
            {
                ["probeId"] = probeId,
                ["probeName"] = probeName,
                ["error"] = error
            }
        };
    }

    /// <summary>
    /// Creates an email delivery IMAP connection failure problem (checker role)
    /// </summary>
    public static ActiveProblem EmailDeliveryImapError(string checkId, string checkName, string error)
    {
        return new ActiveProblem
        {
            Id = CreateId("emaildelivery", checkId, "imap_error"),
            Type = "emaildelivery",
            Severity = ProblemSeverity.Critical,
            Message = $"E-Mail-Zustellung '{checkName}': IMAP-Fehler — {error}",
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["checkName"] = checkName,
                ["error"] = error
            }
        };
    }

    /// <summary>
    /// Creates an email delivery staleness problem when probe emails stop arriving (checker role).
    /// Pass null for ageMinutes when no matching messages were found at all (distinct from "found but too old").
    /// </summary>
    public static ActiveProblem EmailDeliveryStale(string checkId, string checkName, double? ageMinutes, bool isCritical)
    {
        var message = ageMinutes.HasValue
            ? $"E-Mail-Zustellung '{checkName}': Kein Probe-E-Mail seit {ageMinutes.Value:F0} Minuten empfangen"
            : $"E-Mail-Zustellung '{checkName}': Kein Probe-E-Mail im Postfach gefunden";

        return new ActiveProblem
        {
            Id = CreateId("emaildelivery", checkId, "stale"),
            Type = "emaildelivery",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = message,
            Context = new Dictionary<string, object>
            {
                ["checkId"] = checkId,
                ["checkName"] = checkName,
                ["ageMinutes"] = ageMinutes ?? -1.0
            }
        };
    }

    /// <summary>
    /// Creates a certificate expiring problem
    /// </summary>
    public static ActiveProblem CertificateExpiring(string subject, string thumbprint, int daysUntilExpiry, bool isCritical)
    {
        var idKey = string.IsNullOrEmpty(thumbprint)
            ? "unknown"
            : (thumbprint.Length >= 8 ? thumbprint[..8] : thumbprint);

        var message = daysUntilExpiry <= 0
            ? $"Zertifikat '{subject}' ist abgelaufen (seit {Math.Abs(daysUntilExpiry)} Tagen)"
            : $"Zertifikat '{subject}' läuft in {daysUntilExpiry} Tagen ab";

        return new ActiveProblem
        {
            Id = CreateId("certificate", idKey, "expiring"),
            Type = "certificate",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = message,
            Context = new Dictionary<string, object>
            {
                ["subject"] = subject,
                ["thumbprint"] = thumbprint,
                ["daysUntilExpiry"] = daysUntilExpiry
            }
        };
    }

    /// <summary>
    /// Creates a problem for a server whose status can no longer be retrieved or is stale.
    /// Used by the central notifier (HTTP pull): a failed HTTP GET sets <paramref name="unreachable"/>,
    /// an outdated status file passes the elapsed minutes via <paramref name="ageMinutes"/>.
    /// The server name doubles as the ServerId for the acknowledge deep-link.
    /// </summary>
    public static ActiveProblem ServerStale(string serverName, double? ageMinutes, bool unreachable)
    {
        var message = unreachable
            ? $"Server '{serverName}' nicht erreichbar — Status konnte nicht über HTTP abgerufen werden"
            : ageMinutes.HasValue
                ? $"Server '{serverName}' meldet sich nicht — letzte Statusmeldung vor {ageMinutes.Value:F0} Minuten"
                : $"Server '{serverName}' meldet sich nicht — Status veraltet";

        return new ActiveProblem
        {
            Id = CreateId("server", serverName, "stale"),
            Type = "server",
            Source = "Notifier",
            Severity = ProblemSeverity.Critical,
            Message = message,
            Context = new Dictionary<string, object>
            {
                ["serverName"] = serverName,
                ["ageMinutes"] = ageMinutes ?? -1.0,
                ["unreachable"] = unreachable
            }
        };
    }

    /// <summary>Creates a problem for a BizTalk application that is not in its expected (Started) state.</summary>
    public static ActiveProblem BizTalkApplicationStopped(string name, string status)
        => new()
        {
            Id = CreateId("biztalk", $"app_{name}", "stopped"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = ProblemSeverity.Critical,
            Message = $"BizTalk-Anwendung '{name}' ist nicht gestartet (Status: {status})",
            Context = new Dictionary<string, object> { ["artifact"] = "application", ["name"] = name, ["status"] = status }
        };

    /// <summary>Creates a problem for a BizTalk orchestration that is not in its expected (Started) state.</summary>
    public static ActiveProblem BizTalkOrchestrationStopped(string name, string status)
        => new()
        {
            Id = CreateId("biztalk", $"orch_{name}", "stopped"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = ProblemSeverity.Critical,
            Message = $"BizTalk-Orchestrierung '{name}' ist nicht gestartet (Status: {status})",
            Context = new Dictionary<string, object> { ["artifact"] = "orchestration", ["name"] = name, ["status"] = status }
        };

    /// <summary>Creates a problem for a BizTalk send port that is not in its expected (Started) state.</summary>
    public static ActiveProblem BizTalkSendPortStopped(string name, string status)
        => new()
        {
            Id = CreateId("biztalk", $"sp_{name}", "stopped"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = ProblemSeverity.Critical,
            Message = $"BizTalk-Sendeport '{name}' ist nicht gestartet (Status: {status})",
            Context = new Dictionary<string, object> { ["artifact"] = "sendport", ["name"] = name, ["status"] = status }
        };

    /// <summary>Creates a problem for a disabled BizTalk receive location.</summary>
    public static ActiveProblem BizTalkReceiveLocationDisabled(string name, bool isCritical)
        => new()
        {
            Id = CreateId("biztalk", $"rl_{name}", "disabled"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"BizTalk-Empfangsort '{name}' ist deaktiviert",
            Context = new Dictionary<string, object> { ["artifact"] = "receivelocation", ["name"] = name }
        };

    /// <summary>Creates a problem for suspended BizTalk service instances.</summary>
    public static ActiveProblem BizTalkSuspendedInstances(int count, bool isCritical)
        => new()
        {
            Id = CreateId("biztalk", "suspended", "instances"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"BizTalk: {count} suspendierte Instanz(en)",
            Context = new Dictionary<string, object> { ["artifact"] = "suspended", ["count"] = count }
        };

    /// <summary>Creates a problem when the BizTalk Management API cannot be reached.</summary>
    public static ActiveProblem BizTalkApiError(string error)
        => new()
        {
            Id = CreateId("biztalk", "api", "error"),
            Type = "biztalk",
            Source = "BizTalk",
            Severity = ProblemSeverity.Critical,
            Message = $"BizTalk-Management-API nicht erreichbar — {error}",
            Context = new Dictionary<string, object> { ["artifact"] = "api", ["error"] = error }
        };

    /// <summary>
    /// Creates a problem for a file that has stayed in a watched directory beyond its allowed
    /// age / daily cutoff ("hängengebliebene Datei"). The ID is stable per (directory, file name).
    /// </summary>
    public static ActiveProblem FileStuck(
        string directory,
        string filePath,
        double ageMinutes,
        string reason,
        bool isCritical,
        string? wikiUrl = null,
        List<string>? remediationSteps = null)
    {
        var fileName = Path.GetFileName(filePath);
        var ageLabel = ageMinutes >= 120 ? $"{ageMinutes / 60.0:N1} h" : $"{ageMinutes:N0} min";
        var message = string.Equals(reason, "cutoff", StringComparison.OrdinalIgnoreCase)
            ? $"Datei '{fileName}' liegt nach Cutoff noch in '{directory}' (seit {ageLabel})"
            : $"Datei '{fileName}' hängt in '{directory}' (seit {ageLabel})";

        return new ActiveProblem
        {
            Id = CreateId("file", $"{Slug(directory)}_{Slug(fileName)}", "stuck"),
            Type = "file",
            Source = "FileMonitoring",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = message,
            WikiUrl = wikiUrl,
            RemediationSteps = remediationSteps ?? new List<string>(),
            Context = new Dictionary<string, object>
            {
                ["directory"] = directory,
                ["filePath"] = filePath,
                ["fileName"] = fileName,
                ["ageMinutes"] = ageMinutes,
                ["reason"] = reason
            }
        };
    }

    /// <summary>
    /// Creates a problem for log lines matching a configured error signature.
    /// The ID is stable per (log scan, pattern) so the count can change without re-acknowledging.
    /// </summary>
    public static ActiveProblem LogError(
        string logScan,
        string logPath,
        string pattern,
        int count,
        string sampleLine,
        bool isCritical,
        string? wikiUrl = null,
        List<string>? remediationSteps = null)
    {
        var sample = string.IsNullOrWhiteSpace(sampleLine)
            ? string.Empty
            : $" — {(sampleLine.Length > 200 ? sampleLine[..200] + "…" : sampleLine)}";

        return new ActiveProblem
        {
            Id = CreateId("log", $"{Slug(logScan)}_{Slug(pattern)}", "match"),
            Type = "log",
            Source = "FileMonitoring",
            Severity = isCritical ? ProblemSeverity.Critical : ProblemSeverity.Warning,
            Message = $"Log '{logScan}': {count}× '{pattern}'{sample}",
            WikiUrl = wikiUrl,
            RemediationSteps = remediationSteps ?? new List<string>(),
            Context = new Dictionary<string, object>
            {
                ["logScan"] = logScan,
                ["logPath"] = logPath,
                ["pattern"] = pattern,
                ["count"] = count
            }
        };
    }

    /// <summary>Creates a problem when a watched directory / log path cannot be reached.</summary>
    public static ActiveProblem FileShareUnreachable(string label, string error)
        => new()
        {
            Id = CreateId("file", Slug(label), "unreachable"),
            Type = "file",
            Source = "FileMonitoring",
            Severity = ProblemSeverity.Critical,
            Message = $"Verzeichnis '{label}' nicht erreichbar — {error}",
            Context = new Dictionary<string, object> { ["label"] = label, ["error"] = error }
        };

    /// <summary>Reduces an arbitrary label to an ID-safe token (ASCII letters/digits, others to underscore).</summary>
    private static string Slug(string value)
        => string.IsNullOrEmpty(value)
            ? "unknown"
            : new string(value.Select(c =>
                c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_').ToArray());
}

/// <summary>
/// Problem severity enumeration
/// </summary>
public enum ProblemSeverity
{
    /// <summary>
    /// Informational only
    /// </summary>
    Info,

    /// <summary>
    /// Warning level
    /// </summary>
    Warning,

    /// <summary>
    /// Critical level
    /// </summary>
    Critical
}
