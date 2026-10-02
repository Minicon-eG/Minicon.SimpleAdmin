using Minicon.SimpleAdmin.Checkers;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class EmailProbeSenderTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), $"simpleadmin_tests_{Guid.NewGuid():N}");
    private readonly Mock<ILogger<EmailProbeSender>> _logger = new();
    private readonly Mock<IDateTimeProvider> _clock = new();

    private static readonly DateTime FixedNow = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public EmailProbeSenderTests()
    {
        Directory.CreateDirectory(_stateDir);
        _clock.Setup(c => c.UtcNow).Returns(FixedNow);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    private EmailProbeSender BuildSender() =>
        new(_logger.Object, _clock.Object, _stateDir);

    private static EmailProbeConfig MinimalProbe(string name = "test-probe", int intervalMinutes = 15) =>
        new()
        {
            Name = name,
            Description = "Test Probe",
            Smtp = new SmtpConfig
            {
                Host = "localhost",
                Port = 1, // port 1 — connection refused immediately
                UseSsl = false,
                From = "from@example.com",
                To = "to@example.com"
            },
            Subject = "[SA-Probe] test",
            SendIntervalMinutes = intervalMinutes
        };

    // Writes the rate-limiting timestamp (.lastattempt) — controls whether the interval has elapsed
    private void WriteAttemptFile(string probeName, DateTime timestamp)
    {
        var safe = string.Concat(probeName.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var path = Path.Combine(_stateDir, $"emailprobe.{safe}.lastattempt");
        File.WriteAllText(path, timestamp.ToString("O"));
    }

    // Writes the success timestamp (.lastsent) — used for LastSentAt display only
    private void WriteSuccessFile(string probeName, DateTime timestamp)
    {
        var safe = string.Concat(probeName.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var path = Path.Combine(_stateDir, $"emailprobe.{safe}.lastsent");
        File.WriteAllText(path, timestamp.ToString("O"));
    }

    // ── Empty list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_EmptyList_ReturnsEmptyDictionary()
    {
        var result = await BuildSender().SendPendingProbesAsync(new List<EmailProbeConfig>(), "");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SendPendingProbesAsync_ProbeWithEmptyName_IsSkipped()
    {
        var probe = MinimalProbe("");
        var result = await BuildSender().SendPendingProbesAsync([probe], "");
        result.Should().BeEmpty();
    }

    // ── Interval logic ────────────────────────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_IntervalNotElapsed_SkipsSmtp_ReturnsHealthy()
    {
        // Attempt file written 5 minutes ago; interval is 15 minutes → should skip
        WriteAttemptFile("test-probe", FixedNow.AddMinutes(-5));

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result.Should().ContainKey("test-probe");
        result["test-probe"].Status.Should().Be(MetricStatus.Healthy);
        result["test-probe"].ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SendPendingProbesAsync_IntervalNotElapsed_LastSentAtIsPopulated()
    {
        var lastSent = FixedNow.AddMinutes(-5);
        WriteAttemptFile("test-probe", lastSent);    // rate-limit: within interval
        WriteSuccessFile("test-probe", lastSent);    // display: show last successful send

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result["test-probe"].LastSentAt.Should().Be(lastSent);
    }

    [Fact]
    public async Task SendPendingProbesAsync_NoStateFile_IntervalElapsed_AttemptsSmtp_ReturnsCritical()
    {
        // No attempt file → treated as DateTime.MinValue → interval always elapsed
        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result.Should().ContainKey("test-probe");
        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
        result["test-probe"].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendPendingProbesAsync_IntervalExactlyElapsed_AttemptsSmtp()
    {
        // Attempt file written exactly 15 minutes ago (interval = 15) → should attempt send
        WriteAttemptFile("test-probe", FixedNow.AddMinutes(-15));

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe(intervalMinutes: 15)], "");

        // Will attempt SMTP and fail (port 1), so Critical
        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
    }

    // ── Gap 4: rate-limiting on failure ──────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_SmtpFailure_WritesAttemptFile_SoNextCycleIsRateLimited()
    {
        // No attempt file → will try SMTP (fails) → writes .lastattempt
        await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        // Verify the attempt file was written — its presence is what rate-limits the next cycle
        var safe = "test-probe";
        var attemptPath = Path.Combine(_stateDir, $"emailprobe.{safe}.lastattempt");
        File.Exists(attemptPath).Should().BeTrue();
        // Round-trip parse using the same flags as the reader to avoid local-time drift
        var text = File.ReadAllText(attemptPath).Trim();
        DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var writtenTimestamp).Should().BeTrue();
        writtenTimestamp.ToUniversalTime().Should().BeCloseTo(FixedNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SendPendingProbesAsync_SmtpFailure_LastSentAtRemainsNull_WhenNeverSucceeded()
    {
        // No .lastsent file exists → SMTP fails → LastSentAt should remain null
        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result["test-probe"].LastSentAt.Should().BeNull();
    }

    [Fact]
    public async Task SendPendingProbesAsync_SmtpFailure_PreservesLastSentAt_FromPreviousSuccess()
    {
        var previousSuccess = FixedNow.AddMinutes(-30);
        WriteSuccessFile("test-probe", previousSuccess);  // last known good send
        // No attempt file → interval elapsed → SMTP fails

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
        result["test-probe"].LastSentAt.Should().Be(previousSuccess);
    }

    // ── Gap 2/3: ConsecutiveFailures carry-forward ────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_SmtpFailure_IncrementsConsecutiveFailures()
    {
        var prevStates = new Dictionary<string, EmailProbeState>
        {
            ["test-probe"] = new EmailProbeState { ProbeId = "test-probe", ConsecutiveFailures = 2 }
        };

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "", prevStates);

        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
        result["test-probe"].ConsecutiveFailures.Should().Be(3);
    }

    [Fact]
    public async Task SendPendingProbesAsync_IntervalNotElapsed_CarriesForwardConsecutiveFailures()
    {
        // Skip cycle (interval not elapsed) — ConsecutiveFailures must be preserved, not reset
        WriteAttemptFile("test-probe", FixedNow.AddMinutes(-5));

        var prevStates = new Dictionary<string, EmailProbeState>
        {
            ["test-probe"] = new EmailProbeState { ProbeId = "test-probe", ConsecutiveFailures = 3 }
        };

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "", prevStates);

        result["test-probe"].Status.Should().Be(MetricStatus.Healthy);
        result["test-probe"].ConsecutiveFailures.Should().Be(3);  // not reset on skip
    }

    [Fact]
    public async Task SendPendingProbesAsync_NoPreviousState_ConsecutiveFailuresStartsAtOne()
    {
        // First failure ever — no previous state
        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe()], "");

        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
        result["test-probe"].ConsecutiveFailures.Should().Be(1);
    }

    // ── Encrypted password ────────────────────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_EncryptedPasswordWithoutMasterKey_ReturnsCritical()
    {
        var realKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var encrypted = ConnectionStringEncryption.Encrypt("secret123", realKey);

        var probe = MinimalProbe();
        probe.Smtp.Password = encrypted;

        var result = await BuildSender().SendPendingProbesAsync([probe], masterKey: "");

        result["test-probe"].Status.Should().Be(MetricStatus.Critical);
        result["test-probe"].ErrorMessage.Should().Contain("Encryption key not configured");
    }

    [Fact]
    public async Task SendPendingProbesAsync_PlainTextPassword_WithoutMasterKey_DoesNotReturnCriticalDueToEncryption()
    {
        var probe = MinimalProbe();
        probe.Smtp.Password = "plainpassword";

        var result = await BuildSender().SendPendingProbesAsync([probe], masterKey: "");

        result["test-probe"].ErrorMessage.Should().NotContain("Encryption key not configured");
    }

    // ── Multiple probes ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_MultipleProbes_AllSkipped_ReturnsAllHealthy()
    {
        WriteAttemptFile("probe-a", FixedNow.AddMinutes(-1));
        WriteAttemptFile("probe-b", FixedNow.AddMinutes(-2));

        var probes = new List<EmailProbeConfig>
        {
            MinimalProbe("probe-a"),
            MinimalProbe("probe-b")
        };

        var result = await BuildSender().SendPendingProbesAsync(probes, "");

        result.Should().HaveCount(2);
        result["probe-a"].Status.Should().Be(MetricStatus.Healthy);
        result["probe-b"].Status.Should().Be(MetricStatus.Healthy);
    }

    [Fact]
    public async Task SendPendingProbesAsync_MultipleProbes_ResultKeyedByProbeName()
    {
        WriteAttemptFile("probe-a", FixedNow.AddMinutes(-1));
        WriteAttemptFile("probe-b", FixedNow.AddMinutes(-1));

        var probes = new List<EmailProbeConfig>
        {
            MinimalProbe("probe-a"),
            MinimalProbe("probe-b")
        };

        var result = await BuildSender().SendPendingProbesAsync(probes, "");

        result.Keys.Should().BeEquivalentTo(["probe-a", "probe-b"]);
    }

    // ── State file persistence ────────────────────────────────────────────────

    [Fact]
    public async Task SendPendingProbesAsync_AttemptFileContainsValidTimestamp_IsReadCorrectly()
    {
        // 100 minutes ago, interval = 9999 → not elapsed → Healthy
        var lastAttempt = FixedNow.AddMinutes(-100);
        var lastSent = FixedNow.AddMinutes(-100);
        WriteAttemptFile("test-probe", lastAttempt);
        WriteSuccessFile("test-probe", lastSent);

        var result = await BuildSender().SendPendingProbesAsync([MinimalProbe(intervalMinutes: 9999)], "");

        result["test-probe"].Status.Should().Be(MetricStatus.Healthy);
        result["test-probe"].LastSentAt.Should().Be(lastSent);
    }

    [Fact]
    public async Task SendPendingProbesAsync_CorruptAttemptFile_TreatsAsMinValue_AttemptsSmtp()
    {
        var safe = "corrupt-probe";
        File.WriteAllText(Path.Combine(_stateDir, $"emailprobe.{safe}.lastattempt"), "this-is-not-a-date");

        var probe = MinimalProbe("corrupt-probe");
        var result = await BuildSender().SendPendingProbesAsync([probe], "");

        // Treated as MinValue → interval elapsed → SMTP attempt → fails
        result["corrupt-probe"].Status.Should().Be(MetricStatus.Critical);
    }
}
