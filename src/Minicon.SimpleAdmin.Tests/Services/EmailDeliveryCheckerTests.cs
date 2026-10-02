using Minicon.SimpleAdmin.Checkers;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class EmailDeliveryCheckerTests
{
    private readonly Mock<ILogger<EmailDeliveryChecker>> _logger = new();
    private readonly Mock<IDateTimeProvider> _clock = new();

    private static readonly DateTime FixedNow = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public EmailDeliveryCheckerTests()
    {
        _clock.Setup(c => c.UtcNow).Returns(FixedNow);
    }

    private EmailDeliveryChecker BuildChecker() => new(_logger.Object, _clock.Object);

    private static ServerEmailDeliveryConfig ConfigWithChecks(params EmailDeliveryCheckConfig[] checks) =>
        new()
        {
            Enabled = true,
            Imap = new ImapConfig
            {
                Host = "localhost",
                Port = 1, // port 1 — connection refused immediately
                UseSsl = false,
                Username = "user",
                Password = "pass",
                Folder = "INBOX"
            },
            Checks = [..checks]
        };

    private static EmailDeliveryCheckConfig MakeCheck(
        string name = "check1",
        string subjectFilter = "[SA-Probe] test",
        int warnMin = 20,
        int critMin = 60,
        bool deleteAfterCheck = true) =>
        new()
        {
            Name = name,
            Description = $"Check {name}",
            SubjectFilter = subjectFilter,
            MaxAgeWarningMinutes = warnMin,
            MaxAgeCriticalMinutes = critMin,
            DeleteAfterCheck = deleteAfterCheck
        };

    // ── EvaluateAgeStatus (pure logic, no network) ────────────────────────────

    [Theory]
    [InlineData(5.0,   20, 60, MetricStatus.Healthy)]   // well within warning threshold
    [InlineData(19.9,  20, 60, MetricStatus.Healthy)]   // just under warning
    [InlineData(20.0,  20, 60, MetricStatus.Warning)]   // exactly at warning threshold
    [InlineData(45.0,  20, 60, MetricStatus.Warning)]   // between warning and critical
    [InlineData(59.9,  20, 60, MetricStatus.Warning)]   // just under critical
    [InlineData(60.0,  20, 60, MetricStatus.Critical)]  // exactly at critical threshold
    [InlineData(120.0, 20, 60, MetricStatus.Critical)]  // well above critical
    [InlineData(0.0,   20, 60, MetricStatus.Healthy)]   // just arrived
    public void EvaluateAgeStatus_ThresholdBoundaries(
        double ageMinutes, int warnMin, int critMin, MetricStatus expected)
    {
        EmailDeliveryChecker.EvaluateAgeStatus(ageMinutes, warnMin, critMin)
            .Should().Be(expected);
    }

    [Fact]
    public void EvaluateAgeStatus_ZeroThresholds_AlwaysCritical()
    {
        // Edge case: both thresholds at 0 → any age is Critical
        EmailDeliveryChecker.EvaluateAgeStatus(0.1, 0, 0).Should().Be(MetricStatus.Critical);
    }

    // ── Empty checks list ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckDeliveryAsync_EmptyChecksList_ReturnsEmptyDictionary()
    {
        var config = ConfigWithChecks(); // no checks
        var result = await BuildChecker().CheckDeliveryAsync(config, "");
        result.Should().BeEmpty();
    }

    // ── Encryption key handling (no network required) ─────────────────────────

    [Fact]
    public async Task CheckDeliveryAsync_EncryptedPasswordWithoutMasterKey_ReturnsCriticalAllChecks()
    {
        var realKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var encrypted = ConnectionStringEncryption.Encrypt("secretpass", realKey);

        var config = new ServerEmailDeliveryConfig
        {
            Enabled = true,
            Imap = new ImapConfig
            {
                Host = "localhost", Port = 993, UseSsl = true,
                Username = "user", Password = encrypted, Folder = "INBOX"
            },
            Checks = [MakeCheck("check-a"), MakeCheck("check-b")]
        };

        var result = await BuildChecker().CheckDeliveryAsync(config, masterKey: "");

        result.Should().HaveCount(2);
        result["check-a"].Status.Should().Be(MetricStatus.Critical);
        result["check-b"].Status.Should().Be(MetricStatus.Critical);
        result["check-a"].ErrorMessage.Should().Contain("Encryption key not configured");
        result["check-b"].ErrorMessage.Should().Contain("Encryption key not configured");
    }

    [Fact]
    public async Task CheckDeliveryAsync_EncryptedPasswordWithMasterKey_ProceedsToConnect()
    {
        // Correct master key → password decrypts → proceeds to connect (will fail with port 1)
        var realKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var encrypted = ConnectionStringEncryption.Encrypt("secretpass", realKey);

        var config = new ServerEmailDeliveryConfig
        {
            Enabled = true,
            Imap = new ImapConfig
            {
                Host = "localhost", Port = 1, UseSsl = false,
                Username = "user", Password = encrypted, Folder = "INBOX"
            },
            Checks = [MakeCheck("check-a")]
        };

        var result = await BuildChecker().CheckDeliveryAsync(config, masterKey: realKey);

        // Should get Critical due to IMAP connection failure (not encryption error)
        result["check-a"].Status.Should().Be(MetricStatus.Critical);
        result["check-a"].ErrorMessage.Should().NotContain("Encryption key not configured");
    }

    // ── IMAP connection failure ────────────────────────────────────────────────

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_AllChecksCritical()
    {
        var config = ConfigWithChecks(MakeCheck("check-a"), MakeCheck("check-b"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result.Should().HaveCount(2);
        result["check-a"].Status.Should().Be(MetricStatus.Critical);
        result["check-b"].Status.Should().Be(MetricStatus.Critical);
        result["check-a"].ErrorMessage.Should().NotBeNullOrEmpty();
        result["check-b"].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_IsConnectionErrorIsSet()
    {
        var config = ConfigWithChecks(MakeCheck("check-a"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result["check-a"].IsConnectionError.Should().BeTrue();
        result["check-a"].IsNotFound.Should().BeFalse();
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_CheckIdsArePreserved()
    {
        var config = ConfigWithChecks(MakeCheck("seg1-check"), MakeCheck("seg2-check"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result.Keys.Should().BeEquivalentTo(["seg1-check", "seg2-check"]);
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_LastCheckedIsSetToNow()
    {
        var config = ConfigWithChecks(MakeCheck("check-a"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result["check-a"].LastChecked.Should().Be(FixedNow);
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_CheckNameIsSetFromDescription()
    {
        var check = MakeCheck("check-a");
        check.Description = "Segment 1 Check";
        var config = ConfigWithChecks(check);

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result["check-a"].CheckName.Should().Be("Segment 1 Check");
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_CheckNameFallsBackToIdWhenDescriptionEmpty()
    {
        var check = MakeCheck("check-a");
        check.Description = "";
        var config = ConfigWithChecks(check);

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result["check-a"].CheckName.Should().Be("check-a");
    }

    // ── Gap 2/3: ConsecutiveFailures carry-forward ────────────────────────────

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_NoPreviousState_ConsecutiveFailuresIsOne()
    {
        var config = ConfigWithChecks(MakeCheck("check-a"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "");

        result["check-a"].ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task CheckDeliveryAsync_ImapConnectionFails_IncrementsConsecutiveFailuresFromPreviousState()
    {
        var prevStates = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check-a"] = new EmailDeliveryCheckState { CheckId = "check-a", ConsecutiveFailures = 3 }
        };

        var config = ConfigWithChecks(MakeCheck("check-a"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "", prevStates);

        result["check-a"].ConsecutiveFailures.Should().Be(4);
    }

    [Fact]
    public async Task CheckDeliveryAsync_EncryptedPasswordMissing_IncrementsConsecutiveFailures()
    {
        var realKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var encrypted = ConnectionStringEncryption.Encrypt("secret", realKey);

        var prevStates = new Dictionary<string, EmailDeliveryCheckState>
        {
            ["check-a"] = new EmailDeliveryCheckState { CheckId = "check-a", ConsecutiveFailures = 2 }
        };

        var config = new ServerEmailDeliveryConfig
        {
            Enabled = true,
            Imap = new ImapConfig { Host = "x", Port = 993, UseSsl = false, Username = "u", Password = encrypted, Folder = "INBOX" },
            Checks = [MakeCheck("check-a")]
        };

        var result = await BuildChecker().CheckDeliveryAsync(config, masterKey: "", prevStates);

        result["check-a"].ConsecutiveFailures.Should().Be(3);
    }

    // ── CancellationToken propagation ─────────────────────────────────────────

    [Fact]
    public async Task CheckDeliveryAsync_PreCancelledToken_HandlesCancellationGracefully()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var config = ConfigWithChecks(MakeCheck("check-a"));

        var result = await BuildChecker().CheckDeliveryAsync(config, "", cancellationToken: cts.Token);

        // Pre-cancelled token: IMAP connect throws → caught → Critical
        result["check-a"].Status.Should().Be(MetricStatus.Critical);
    }
}
