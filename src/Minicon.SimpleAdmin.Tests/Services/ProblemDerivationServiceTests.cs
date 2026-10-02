using Minicon.SimpleAdmin.Checkers;
using FluentAssertions;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.Services;
using StatusMetricStatus = Minicon.SimpleAdmin.Models.Status.MetricStatus;
using StateMetricStatus = Minicon.SimpleAdmin.Models.State.MetricStatus;

namespace Minicon.SimpleAdmin.Tests.Services;

public class ProblemDerivationServiceTests
{
    // -------------------------------------------------------------------------
    // AppPool tests
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_WithStoppedPool_CreatesCriticalProblem()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            { "DefaultAppPool", new AppPoolState { Name = "DefaultAppPool", Status = AppPoolStatus.Stopped } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("apppool");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Contain("defaultapppool").And.Contain("stopped");
    }

    [Fact]
    public void DeriveProblems_WithRunningHealthyPool_NoProblem()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            { "DefaultAppPool", new AppPoolState { Name = "DefaultAppPool", Status = AppPoolStatus.Running, MemoryStatus = StateMetricStatus.Healthy, UptimeStatus = StateMetricStatus.Healthy } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_WithHighMemoryWarning_CreatesWarningProblem_WithCorrectThreshold()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            {
                "DefaultAppPool", new AppPoolState
                {
                    Name = "DefaultAppPool",
                    Status = AppPoolStatus.Running,
                    MemoryMB = 750,
                    MemoryStatus = StateMetricStatus.Warning,
                    MemoryWarningMB = 500,
                    MemoryCriticalMB = 1000,
                    UptimeStatus = StateMetricStatus.Healthy
                }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("apppool");
        problems[0].Severity.Should().Be(ProblemSeverity.Warning);
        problems[0].Message.Should().Contain("750").And.Contain("500");
        problems[0].Context["memoryMB"].Should().Be(750.0);
        problems[0].Context["thresholdMB"].Should().Be(500);
    }

    [Fact]
    public void DeriveProblems_WithHighMemoryCritical_CreatesCriticalProblem_WithCorrectThreshold()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            {
                "DefaultAppPool", new AppPoolState
                {
                    Name = "DefaultAppPool",
                    Status = AppPoolStatus.Running,
                    MemoryMB = 1200,
                    MemoryStatus = StateMetricStatus.Critical,
                    MemoryWarningMB = 500,
                    MemoryCriticalMB = 1000,
                    UptimeStatus = StateMetricStatus.Healthy
                }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().ContainSingle();
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Context["memoryMB"].Should().Be(1200.0);
        problems[0].Context["thresholdMB"].Should().Be(1000);
    }

    [Fact]
    public void DeriveProblems_WithLongUptime_CreatesWarningProblem()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            {
                "DefaultAppPool", new AppPoolState
                {
                    Name = "DefaultAppPool",
                    Status = AppPoolStatus.Running,
                    MemoryStatus = StateMetricStatus.Healthy,
                    UptimeHours = 200,
                    UptimeStatus = StateMetricStatus.Warning,
                    UptimeWarningHours = 168
                }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("apppool");
        problems[0].Severity.Should().Be(ProblemSeverity.Warning);
        problems[0].Id.Should().Contain("defaultapppool").And.Contain("uptime");
        problems[0].Context["uptimeHours"].Should().Be(200.0);
        problems[0].Context["thresholdHours"].Should().Be(168);
    }

    [Fact]
    public void DeriveProblems_WithNormalUptime_NoProblem()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            {
                "DefaultAppPool", new AppPoolState
                {
                    Name = "DefaultAppPool",
                    Status = AppPoolStatus.Running,
                    MemoryStatus = StateMetricStatus.Healthy,
                    UptimeHours = 48,
                    UptimeStatus = StateMetricStatus.Healthy,
                    UptimeWarningHours = 168
                }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_WithStoppedPoolAndHighMemory_CreatesTwoProblems()
    {
        // Stopped + high memory → both problems; stopped takes priority in status but both are reported
        var pools = new Dictionary<string, AppPoolState>
        {
            {
                "DefaultAppPool", new AppPoolState
                {
                    Name = "DefaultAppPool",
                    Status = AppPoolStatus.Stopped,
                    MemoryMB = 1200,
                    MemoryStatus = StateMetricStatus.Critical,
                    MemoryWarningMB = 500,
                    MemoryCriticalMB = 1000
                }
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Id.Contains("stopped"));
        problems.Should().Contain(p => p.Id.Contains("memory"));
    }

    [Fact]
    public void DeriveProblems_WithNullAppPools_NoProblem()
    {
        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_AppPoolStoppedProblemId_IsDeterministic()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            { "DefaultAppPool", new AppPoolState { Name = "DefaultAppPool", Status = AppPoolStatus.Stopped } }
        };

        var problems1 = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);
        var problems2 = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools);

        problems1[0].Id.Should().Be(problems2[0].Id);
    }

    [Fact]
    public void DeriveProblems_WithAcknowledgedStoppedPool_MarksAsAcknowledged()
    {
        var pools = new Dictionary<string, AppPoolState>
        {
            { "DefaultAppPool", new AppPoolState { Name = "DefaultAppPool", Status = AppPoolStatus.Stopped } }
        };

        var problemId = ProblemDerivationService.DeriveProblems(new List<Metric>(), pools)[0].Id;

        var acknowledges = new List<Acknowledge>
        {
            new Acknowledge
            {
                Id = Guid.NewGuid().ToString(),
                ServerId = "server-a",
                ProblemId = problemId,
                Status = AcknowledgeStatus.Active,
                AcknowledgedBy = "tester",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), pools, null, acknowledges, "server-a");

        problems[0].Acknowledged.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Windows Service tests
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_WithStoppedService_CreatesProblem()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", DisplayName = "SQL Server VSS Writer", Status = "Stopped", IsHealthy = false } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("service");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Contain("sqlwriter");
    }

    [Fact]
    public void DeriveProblems_WithRunningService_NoProblem()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", Status = "Running", IsHealthy = true } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_WithNotFoundService_CreatesProblem()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "W3SVC", new ServiceState { Name = "W3SVC", DisplayName = "World Wide Web Publishing Service", Status = "NotFound", IsHealthy = false } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("service");
    }

    [Fact]
    public void DeriveProblems_WithHealthyService_NoProblem()
    {
        // IsHealthy = true means WindowsServiceChecker decided this entry is not a problem
        // (e.g. service is running, or was filtered out by startup-type and should not appear at all)
        var services = new Dictionary<string, ServiceState>
        {
            { "OptionalSvc", new ServiceState { Name = "OptionalSvc", Status = "NotFound", IsHealthy = true } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_WithStoppedAutomaticService_MessageContainsStartart()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", DisplayName = "SQL Server VSS Writer", Status = "Stopped", IsHealthy = false, StartupType = "Automatic" } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().ContainSingle();
        problems[0].Message.Should().Contain("Startart: Automatisch");
        problems[0].Context.Should().ContainKey("startupType").WhoseValue.Should().Be("Automatic");
    }

    [Fact]
    public void DeriveProblems_WithStoppedManualService_MessageContainsStartart()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "W3SVC", new ServiceState { Name = "W3SVC", Status = "Stopped", IsHealthy = false, StartupType = "Manual" } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().ContainSingle();
        problems[0].Message.Should().Contain("Startart: Manuell");
    }

    [Fact]
    public void DeriveProblems_WithStoppedServiceNoStartupType_MessageHasNoStartart()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "W3SVC", new ServiceState { Name = "W3SVC", Status = "NotFound", IsHealthy = false, StartupType = null } }
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems.Should().ContainSingle();
        problems[0].Message.Should().NotContain("Startart");
    }

    [Fact]
    public void DeriveProblems_WithMixedServicesAndMetrics_CombinesProblems()
    {
        var metrics = new List<Metric>
        {
            new Metric
            {
                Name = "cpu",
                Value = 95,
                Status = StatusMetricStatus.Critical,
                Threshold = new MetricThreshold { Warning = 70, Critical = 85, Operator = ">" }
            }
        };
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", Status = "Stopped", IsHealthy = false } }
        };

        var problems = ProblemDerivationService.DeriveProblems(metrics, null, services);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Type == "cpu");
        problems.Should().Contain(p => p.Type == "service");
    }

    [Fact]
    public void DeriveProblems_WithNullServices_NoProblemFromServices()
    {
        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null);

        problems.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Problem ID determinism
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_ServiceDownProblemId_IsDeterministic()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", Status = "Stopped", IsHealthy = false } }
        };

        var problems1 = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);
        var problems2 = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);

        problems1[0].Id.Should().Be(problems2[0].Id);
    }

    // -------------------------------------------------------------------------
    // Acknowledge application
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Event Log tests
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_WithCriticalEventLog_CreatesProblem()
    {
        var eventLogs = new EventLogState
        {
            EventCount = 25,
            Status = StateMetricStatus.Critical,
            RecentEvents = new List<EventLogEntry>()
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, eventLogs);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("eventlog");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Contain("eventlog");
    }

    [Fact]
    public void DeriveProblems_WithWarningEventLog_CreatesWarningProblem()
    {
        var eventLogs = new EventLogState
        {
            EventCount = 7,
            Status = StateMetricStatus.Warning,
            RecentEvents = new List<EventLogEntry>()
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, eventLogs);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("eventlog");
        problems[0].Severity.Should().Be(ProblemSeverity.Warning);
    }

    [Fact]
    public void DeriveProblems_WithHealthyEventLog_NoProblem()
    {
        var eventLogs = new EventLogState
        {
            EventCount = 2,
            Status = StateMetricStatus.Healthy,
            RecentEvents = new List<EventLogEntry>()
        };

        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, eventLogs);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_WithMatchingAcknowledge_MarksServiceProblemAsAcknowledged()
    {
        var services = new Dictionary<string, ServiceState>
        {
            { "SQLWriter", new ServiceState { Name = "SQLWriter", Status = "Stopped", IsHealthy = false } }
        };

        // First derive without acknowledges to get the problem ID
        var problems = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, services);
        var problemId = problems[0].Id;

        var acknowledges = new List<Acknowledge>
        {
            new Acknowledge
            {
                Id = Guid.NewGuid().ToString(),
                ServerId = "server-a",
                ProblemId = problemId,
                Status = AcknowledgeStatus.Active,
                AcknowledgedBy = "tester",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            }
        };

        var problemsWithAck = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, services, acknowledges, "server-a");

        problemsWithAck[0].Acknowledged.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // SQL Query Check tests
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_SqlConnectionError_CreatesCriticalProblem()
    {
        var checks = new Dictionary<string, SqlQueryCheckState>
        {
            ["check1"] = new SqlQueryCheckState
            {
                CheckId = "check1",
                CheckName = "DB Check",
                ConnectionError = "Host not found",
                Status = StateMetricStatus.Critical
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, checks);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("sqlquery");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Contain("check1").And.Contain("connection_error");
    }

    [Fact]
    public void DeriveProblems_SqlRowCountMismatch_CreatesProblemWithRowCountMismatchId()
    {
        var checks = new Dictionary<string, SqlQueryCheckState>
        {
            ["check1"] = new SqlQueryCheckState
            {
                CheckId = "check1",
                CheckName = "DB Check",
                Status = StateMetricStatus.Critical,
                Results =
                [
                    new SqlQueryResult
                    {
                        QueryName = "open-jobs",
                        Status = StateMetricStatus.Critical,
                        RowCount = new SqlRowCountResult
                        {
                            ActualCount = 3,
                            ExpectedCount = 0,
                            Operator = "==",
                            Passed = false
                        }
                    }
                ]
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, checks);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("sqlquery");
        problems[0].Id.Should().Contain("rowcount_mismatch");
    }

    [Fact]
    public void DeriveProblems_SqlColumnMismatch_CreatesProblemWithAssertionNameInId()
    {
        var checks = new Dictionary<string, SqlQueryCheckState>
        {
            ["check1"] = new SqlQueryCheckState
            {
                CheckId = "check1",
                CheckName = "DB Check",
                Status = StateMetricStatus.Critical,
                Results =
                [
                    new SqlQueryResult
                    {
                        QueryName = "service-status",
                        Status = StateMetricStatus.Critical,
                        ColumnResults =
                        [
                            new SqlColumnResult
                            {
                                AssertionName = "status-check",
                                Column = "status",
                                ActualValue = "1/1 rows failed",
                                ExpectedValue = "running",
                                Operator = "==",
                                Passed = false,
                                FailureReason = "Row 0='stopped'"
                            }
                        ]
                    }
                ]
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, checks);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("sqlquery");
        problems[0].Id.Should().Contain("status-check").And.Contain("column_mismatch");
    }

    [Fact]
    public void DeriveProblems_SqlExecutionError_CreatesProblemWithErrorId()
    {
        var checks = new Dictionary<string, SqlQueryCheckState>
        {
            ["check1"] = new SqlQueryCheckState
            {
                CheckId = "check1",
                CheckName = "DB Check",
                Status = StateMetricStatus.Critical,
                Results =
                [
                    new SqlQueryResult
                    {
                        QueryName = "open-jobs",
                        Status = StateMetricStatus.Critical,
                        ExecutionError = "Invalid object name 'dbo.bts_Jobs'"
                    }
                ]
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, checks);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("sqlquery");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Contain("check1").And.Contain("open-jobs").And.Contain("error");
    }

    // -------------------------------------------------------------------------
    // Email Probe tests (sender role)
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_EmailProbe_SmtpError_CreatesCriticalProblem()
    {
        var probes = new Dictionary<string, EmailProbeState>
        {
            ["probe1"] = new EmailProbeState
            {
                ProbeId = "probe1",
                ProbeName = "Segment 1 Probe",
                Status = StateMetricStatus.Critical,
                ErrorMessage = "Connection refused"
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, probes, null);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("emailprobe");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Be("emailprobe_probe1_smtp_error");
    }

    [Fact]
    public void DeriveProblems_EmailProbe_Healthy_NoProblem()
    {
        var probes = new Dictionary<string, EmailProbeState>
        {
            ["probe1"] = new EmailProbeState
            {
                ProbeId = "probe1",
                ProbeName = "Segment 1 Probe",
                Status = StateMetricStatus.Healthy
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, probes, null);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_EmailProbe_CriticalWithoutErrorMessage_NoProblem()
    {
        // Critical status without ErrorMessage is not actionable — no problem created
        var probes = new Dictionary<string, EmailProbeState>
        {
            ["probe1"] = new EmailProbeState
            {
                ProbeId = "probe1",
                Status = StateMetricStatus.Critical,
                ErrorMessage = null
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, probes, null);

        problems.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Email Delivery tests (checker role)
    // -------------------------------------------------------------------------

    [Fact]
    public void DeriveProblems_EmailDelivery_ImapConnectionError_CreatesImapErrorProblem()
    {
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Segment 1 Check",
                Status = StateMetricStatus.Critical,
                ErrorMessage = "Connection refused",
                IsConnectionError = true
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, delivery);

        problems.Should().ContainSingle();
        problems[0].Type.Should().Be("emaildelivery");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        problems[0].Id.Should().Be("emaildelivery_check1_imap_error");
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_NoMessagesFound_CreatesStaleNotImapError()
    {
        // "No messages found" sets IsNotFound=true and IsConnectionError=false.
        // Must produce emaildelivery_{id}_stale, not emaildelivery_{id}_imap_error.
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Segment 1 Check",
                Status = StateMetricStatus.Critical,
                ErrorMessage = "Keine Nachrichten mit Filter '[SA-Probe]' gefunden",
                IsConnectionError = false,
                IsNotFound = true
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, delivery);

        problems.Should().ContainSingle();
        problems[0].Id.Should().Be("emaildelivery_check1_stale");
        problems[0].Id.Should().NotContain("imap_error");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
        // Gap 1: message must not say "0 Minuten" — it should say "not found in mailbox"
        problems[0].Message.Should().Contain("gefunden");
        problems[0].Message.Should().NotContain("0 Minuten");
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_WarningAge_CreatesWarningStale()
    {
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Segment 1 Check",
                Status = StateMetricStatus.Warning,
                AgeMinutes = 25.0,
                IsConnectionError = false
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, delivery);

        problems.Should().ContainSingle();
        problems[0].Id.Should().Be("emaildelivery_check1_stale");
        problems[0].Severity.Should().Be(ProblemSeverity.Warning);
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_CriticalAge_CreatesCriticalStale()
    {
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Segment 1 Check",
                Status = StateMetricStatus.Critical,
                AgeMinutes = 90.0,
                IsConnectionError = false
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, delivery);

        problems.Should().ContainSingle();
        problems[0].Id.Should().Be("emaildelivery_check1_stale");
        problems[0].Severity.Should().Be(ProblemSeverity.Critical);
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_Healthy_NoProblem()
    {
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Segment 1 Check",
                Status = StateMetricStatus.Healthy,
                AgeMinutes = 5.0
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, null, delivery);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_ImapErrorAndProbeError_CombinesProblems()
    {
        var probes = new Dictionary<string, EmailProbeState>
        {
            ["probe1"] = new EmailProbeState
            {
                ProbeId = "probe1",
                ProbeName = "Probe 1",
                Status = StateMetricStatus.Critical,
                ErrorMessage = "Auth failed"
            }
        };
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check1"] = new EmailDeliveryCheckState
            {
                CheckId = "check1",
                CheckName = "Check 1",
                Status = StateMetricStatus.Critical,
                ErrorMessage = "Connection refused",
                IsConnectionError = true
            }
        };

        var problems = ProblemDerivationService.DeriveProblems(
            new List<Metric>(), null, null, null, null, probes, delivery);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Id == "emailprobe_probe1_smtp_error");
        problems.Should().Contain(p => p.Id == "emaildelivery_check1_imap_error");
    }

    [Fact]
    public void DeriveProblems_EmailDelivery_ProblemIdIsDeterministic()
    {
        var delivery = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["my-check"] = new EmailDeliveryCheckState
            {
                CheckId = "my-check",
                Status = StateMetricStatus.Critical,
                IsConnectionError = true,
                ErrorMessage = "timeout"
            }
        };

        var p1 = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, null, null, null, delivery);
        var p2 = ProblemDerivationService.DeriveProblems(new List<Metric>(), null, null, null, null, null, delivery);

        p1[0].Id.Should().Be(p2[0].Id);
    }
}
