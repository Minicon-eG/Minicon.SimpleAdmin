using Minicon.SimpleAdmin.Checkers;
using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class SqlQueryCheckerTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    private static List<Dictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows)
        => [..rows];

    private static Dictionary<string, object?> Row(string col, object? val)
        => new(StringComparer.OrdinalIgnoreCase) { [col] = val };

    private static SqlColumnAssertion ColAssertion(
        string col, string expected, string op = "==",
        SqlValueType type = SqlValueType.String)
        => new() { Name = $"{col}-check", Column = col, ExpectedValue = expected, Operator = op, ValueType = type };

    private static SqlQueryConfig QueryConfig(string name = "q1", string sql = "SELECT 1",
        SqlRowCountAssertion? rowCount = null, params SqlColumnAssertion[] cols)
        => new() { Name = name, Sql = sql, RowCount = rowCount, ColumnAssertions = [..cols] };

    private SqlQueryChecker BuildChecker(Mock<ISqlQueryExecutor> mock, string masterKey = "")
        => new(new Mock<ILogger<SqlQueryChecker>>().Object, mock.Object, masterKey);

    private Mock<ISqlQueryExecutor> MockReturning(List<Dictionary<string, object?>> rows)
    {
        var m = new Mock<ISqlQueryExecutor>();
        m.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(rows);
        return m;
    }

    // ─── Section A: EvaluateRowCount ────────────────────────────────────────

    [Theory]
    [InlineData(5, 5, "==", true)]
    [InlineData(5, 6, "==", false)]
    [InlineData(5, 4, "!=", true)]
    [InlineData(5, 5, "!=", false)]
    [InlineData(5, 4, ">",  true)]
    [InlineData(5, 5, ">",  false)]
    [InlineData(5, 5, ">=", true)]
    [InlineData(5, 6, ">=", false)]
    [InlineData(4, 5, "<",  true)]
    [InlineData(5, 5, "<",  false)]
    [InlineData(5, 5, "<=", true)]
    [InlineData(6, 5, "<=", false)]
    public void EvaluateRowCount_OperatorVariants(int actual, int expected, string op, bool shouldPass)
    {
        var result = SqlQueryChecker.EvaluateRowCount(
            actual,
            new SqlRowCountAssertion { ExpectedCount = expected, Operator = op });

        result.Passed.Should().Be(shouldPass);
        result.ActualCount.Should().Be(actual);
        result.ExpectedCount.Should().Be(expected);
        result.Operator.Should().Be(op);
    }

    [Fact]
    public void EvaluateQuery_NoRowCountAssertion_RowCountIsNullAndStatusHealthy()
    {
        var query = QueryConfig();
        var result = SqlQueryChecker.EvaluateQuery(query, []);

        result.RowCount.Should().BeNull();
        result.Status.Should().Be(MetricStatus.Healthy);
    }

    [Fact]
    public void EvaluateQuery_FailingRowCount_StatusIsCritical()
    {
        var query = QueryConfig(rowCount: new SqlRowCountAssertion { ExpectedCount = 0, Operator = "==" });
        var result = SqlQueryChecker.EvaluateQuery(query, Rows(Row("id", 1)));

        result.RowCount!.Passed.Should().BeFalse();
        result.Status.Should().Be(MetricStatus.Critical);
    }

    // ─── Section B: EvaluateString ──────────────────────────────────────────

    [Fact]
    public void EvaluateString_CaseInsensitiveMatch_Passes()
    {
        SqlQueryChecker.EvaluateString("ACTIVE", "active", "==", out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void EvaluateString_Mismatch_FailsWithReason()
    {
        var passed = SqlQueryChecker.EvaluateString("inactive", "active", "==", out var reason);

        passed.Should().BeFalse();
        reason.Should().Contain("Expected").And.Contain("got");
    }

    [Fact]
    public void EvaluateString_NotEqual_WhenValuesDiffer_Passes()
    {
        SqlQueryChecker.EvaluateString("stopped", "running", "!=", out _).Should().BeTrue();
    }

    [Fact]
    public void EvaluateString_NotEqual_WhenValuesMatch_Fails()
    {
        SqlQueryChecker.EvaluateString("running", "running", "!=", out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("<")]
    [InlineData("<=")]
    public void EvaluateString_UnsupportedOperator_FailsWithDescriptiveMessage(string op)
    {
        var passed = SqlQueryChecker.EvaluateString("x", "y", op, out var reason);

        passed.Should().BeFalse();
        reason.Should().Contain("not supported for String type");
    }

    // ─── Section C: EvaluateColumnAssertion — all-rows validation ───────────

    [Fact]
    public void EvaluateColumnAssertion_EmptyResultSet_PassesTrivially()
    {
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("col", "val"), []);

        result.Passed.Should().BeTrue();
        result.ActualValue.Should().Be("0 rows");
    }

    [Fact]
    public void EvaluateColumnAssertion_AllRowsPass_ReturnsPassedWithSummary()
    {
        var rows = Rows(Row("status", "running"), Row("status", "running"), Row("status", "running"));
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("status", "running"), rows);

        result.Passed.Should().BeTrue();
        result.ActualValue.Should().Be("3 rows OK");
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public void EvaluateColumnAssertion_SingleRow_PassesSingularSummary()
    {
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("status", "ok"), Rows(Row("status", "ok")));

        result.Passed.Should().BeTrue();
        result.ActualValue.Should().Be("1 row OK");
    }

    [Fact]
    public void EvaluateColumnAssertion_OneRowFails_ReportsFailure()
    {
        var rows = Rows(Row("status", "running"), Row("status", "stopped"), Row("status", "running"));
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("status", "running"), rows);

        result.Passed.Should().BeFalse();
        result.ActualValue.Should().Be("1/3 rows failed");
        result.FailureReason.Should().Contain("Row 1='stopped'");
    }

    [Fact]
    public void EvaluateColumnAssertion_MultipleRowsFail_ReportsAllFailures()
    {
        var rows = Rows(Row("v", "x"), Row("v", "ok"), Row("v", "y"));
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("v", "ok"), rows);

        result.Passed.Should().BeFalse();
        result.ActualValue.Should().Be("2/3 rows failed");
        result.FailureReason.Should().Contain("Row 0='x'").And.Contain("Row 2='y'");
    }

    [Fact]
    public void EvaluateColumnAssertion_FailingRowDoesNotAbortRemainingRows()
    {
        // Row 1 fails, but rows 0 and 2 must still be evaluated and reported correctly
        var rows = Rows(Row("v", "ok"), Row("v", "bad"), Row("v", "ok"));
        var result = SqlQueryChecker.EvaluateColumnAssertion(ColAssertion("v", "ok"), rows);

        result.Passed.Should().BeFalse();
        result.ActualValue.Should().Be("1/3 rows failed");
        result.FailureReason.Should().Contain("Row 1='bad'");
        result.FailureReason.Should().NotContain("Row 0").And.NotContain("Row 2");
    }

    [Fact]
    public void EvaluateColumnAssertion_ColumnNotFound_FailsWithReason()
    {
        var result = SqlQueryChecker.EvaluateColumnAssertion(
            ColAssertion("missing_col", "val"), Rows(Row("other_col", "val")));

        result.Passed.Should().BeFalse();
        result.FailureReason.Should().Contain("not found");
    }

    [Fact]
    public void EvaluateColumnAssertion_ColumnNameCaseInsensitive_Passes()
    {
        var result = SqlQueryChecker.EvaluateColumnAssertion(
            ColAssertion("STATUS", "running"), Rows(Row("status", "running")));

        result.Passed.Should().BeTrue();
    }

    // ─── Section D: EvaluateInteger ─────────────────────────────────────────

    [Theory]
    [InlineData("10", "10", "==", true)]
    [InlineData("10",  "9", "==", false)]
    [InlineData("10",  "9", "!=", true)]
    [InlineData("10", "10", "!=", false)]
    [InlineData("10",  "9", ">",  true)]
    [InlineData("10", "10", ">",  false)]
    [InlineData("10", "10", ">=", true)]
    [InlineData("10", "11", ">=", false)]
    [InlineData( "9", "10", "<",  true)]
    [InlineData("10", "10", "<",  false)]
    [InlineData("10", "10", "<=", true)]
    [InlineData("11", "10", "<=", false)]
    public void EvaluateInteger_OperatorVariants(string actual, string expected, string op, bool shouldPass)
    {
        SqlQueryChecker.EvaluateInteger(actual, expected, op, out _).Should().Be(shouldPass);
    }

    [Fact]
    public void EvaluateInteger_NonParsableActual_FailsWithReason()
    {
        var passed = SqlQueryChecker.EvaluateInteger("not_a_number", "5", "==", out var reason);

        passed.Should().BeFalse();
        reason.Should().Contain("Could not parse actual value");
    }

    [Fact]
    public void EvaluateInteger_NonParsableExpected_FailsWithReason()
    {
        var passed = SqlQueryChecker.EvaluateInteger("5", "nope", "==", out var reason);

        passed.Should().BeFalse();
        reason.Should().Contain("Could not parse expected value");
    }

    // ─── Section E: EvaluateDateTime / ParseExpectedDateTime ────────────────

    [Fact]
    public void EvaluateDateTime_RawDateTimeObject_UsedDirectlyWithoutStringParsing()
    {
        var dt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var passed = SqlQueryChecker.EvaluateDateTime(
            dt.ToString("O"), dt, "2025-06-15T12:00:00Z", "==", out var reason);

        passed.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void EvaluateDateTime_StringValue_LaterDateIsGreater()
    {
        // Uses > to avoid exact-equality timezone issues when parsing string dates
        var passed = SqlQueryChecker.EvaluateDateTime(
            "2025-06-15", null, "2025-01-01", ">", out var reason);

        passed.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void ParseExpectedDateTime_NowToken_ReturnsValueCloseToUtcNow()
    {
        var before = DateTime.UtcNow;
        var result = SqlQueryChecker.ParseExpectedDateTime("now", out var error);

        error.Should().BeNull();
        result.Should().NotBeNull();
        result!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ParseExpectedDateTime_TodayToken_ReturnsUtcNowDate()
    {
        var result = SqlQueryChecker.ParseExpectedDateTime("today", out _);

        result.Should().Be(DateTime.UtcNow.Date);
    }

    [Theory]
    [InlineData("now-30m", -30, 'm')]
    [InlineData("now+1h",    1, 'h')]
    [InlineData("now-1d",   -1, 'd')]
    [InlineData("now+2d",    2, 'd')]
    public void ParseExpectedDateTime_RelativeTokens_ResolveCorrectly(string token, int amount, char unit)
    {
        var expectedApprox = unit switch
        {
            'm' => DateTime.UtcNow.AddMinutes(amount),
            'h' => DateTime.UtcNow.AddHours(amount),
            _   => DateTime.UtcNow.AddDays(amount)
        };

        var result = SqlQueryChecker.ParseExpectedDateTime(token, out _);

        result.Should().NotBeNull();
        result!.Value.Should().BeCloseTo(expectedApprox, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ParseExpectedDateTime_InvalidToken_ReturnsNullWithDescriptiveError()
    {
        // "now-abc": int.TryParse("ab", ...) fails → hits the error message path
        var result = SqlQueryChecker.ParseExpectedDateTime("now-abc", out var error);

        result.Should().BeNull();
        error.Should().Contain("Unrecognised");
    }

    [Fact]
    public void EvaluateDateTime_NonParsableActualString_FailsWithReason()
    {
        var passed = SqlQueryChecker.EvaluateDateTime("not-a-date", "not-a-date", "now", ">", out var reason);

        passed.Should().BeFalse();
        reason.Should().Contain("Could not parse actual value");
    }

    [Fact]
    public void EvaluateDateTime_NonParsableExpectedString_FailsWithReason()
    {
        var dt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var passed = SqlQueryChecker.EvaluateDateTime(dt.ToString("O"), dt, "not-a-date-token", ">", out var reason);

        passed.Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    // ─── Section F: CheckAllAsync integration (mocked executor) ────────────

    [Fact]
    public async Task CheckAllAsync_ExecutorThrowsOnFirstCall_SetsConnectionError()
    {
        var mock = new Mock<ISqlQueryExecutor>();
        mock.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                       It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "DB Check", ConnectionString = "Server=fake;",
                Queries = [QueryConfig()]
            }
        };

        var result = await checker.CheckAllAsync(checks, defaultTimeoutSeconds: 30);

        result["check1"].ConnectionError.Should().Be("Connection refused");
        result["check1"].Status.Should().Be(MetricStatus.Critical);
    }

    [Fact]
    public async Task CheckAllAsync_ExecutorThrowsOnSecondQuery_FirstPassesSecondSetsExecutionError()
    {
        var mock = new Mock<ISqlQueryExecutor>();
        mock.SetupSequence(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                               It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rows(Row("cnt", 1L)))
            .ThrowsAsync(new Exception("Timeout"));

        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "DB Check", ConnectionString = "Server=fake;",
                Queries =
                [
                    QueryConfig("q1", rowCount: new SqlRowCountAssertion { ExpectedCount = 1, Operator = "==" }),
                    QueryConfig("q2")
                ]
            }
        };

        var result = await checker.CheckAllAsync(checks, 30);
        var state = result["check1"];

        state.ConnectionError.Should().BeNull();
        state.Results.Should().HaveCount(2);
        state.Results.Should().Contain(r => r.QueryName == "q1" && r.ExecutionError == null);
        state.Results.Should().Contain(r => r.QueryName == "q2" && r.ExecutionError == "Timeout");
    }

    [Fact]
    public async Task CheckAllAsync_AllAssertionsPass_StatusIsHealthy()
    {
        var mock = MockReturning(Rows(Row("cnt", 1L)));
        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "DB Check", ConnectionString = "Server=fake;",
                Queries = [QueryConfig(rowCount: new SqlRowCountAssertion { ExpectedCount = 1, Operator = "==" })]
            }
        };

        var result = await checker.CheckAllAsync(checks, 30);

        result["check1"].Status.Should().Be(MetricStatus.Healthy);
        result["check1"].ConnectionError.Should().BeNull();
        result["check1"].Results[0].RowCount!.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAllAsync_ColumnAssertionFails_StatusIsCritical()
    {
        var mock = MockReturning(Rows(Row("status", "stopped")));
        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "DB Check", ConnectionString = "Server=fake;",
                Queries = [QueryConfig(cols: ColAssertion("status", "running"))]
            }
        };

        var result = await checker.CheckAllAsync(checks, 30);
        var state = result["check1"];

        state.Status.Should().Be(MetricStatus.Critical);
        state.Results[0].ColumnResults[0].Passed.Should().BeFalse();
        state.Results[0].ColumnResults[0].ActualValue.Should().Be("1/1 rows failed");
    }

    [Fact]
    public async Task CheckAllAsync_MultipleCheckGroups_AllReturnedKeyedById()
    {
        var mock = new Mock<ISqlQueryExecutor>();
        mock.SetupSequence(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                               It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rows(Row("cnt", 0L)))   // check-a
            .ReturnsAsync(Rows(Row("cnt", 5L)));   // check-b

        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig { Name = "check-a", Description = "A", ConnectionString = "Server=a;", Queries = [QueryConfig()] },
            new SqlQueryCheckConfig { Name = "check-b", Description = "B", ConnectionString = "Server=b;", Queries = [QueryConfig()] }
        };

        var result = await checker.CheckAllAsync(checks, 30);

        result.Should().ContainKey("check-a");
        result.Should().ContainKey("check-b");
        result["check-a"].Status.Should().Be(MetricStatus.Healthy);
        result["check-b"].Status.Should().Be(MetricStatus.Healthy);
    }

    [Fact]
    public async Task CheckAllAsync_EncryptedConnectionStringWithoutKey_SetsConnectionError()
    {
        // "enc:v1:" prefix is enough to trigger the IsEncrypted check — no master key configured
        var encryptedConnStr = "enc:v1:fakesalt.fakeiv.fakeciphertext";
        var mock = new Mock<ISqlQueryExecutor>();
        var checker = BuildChecker(mock, masterKey: "");
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "Encrypted Check", ConnectionString = encryptedConnStr,
                Queries = [QueryConfig()]
            }
        };

        var result = await checker.CheckAllAsync(checks, 30);

        mock.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                        It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        result["check1"].ConnectionError.Should().Contain("Encryption key not configured");
        result["check1"].Status.Should().Be(MetricStatus.Critical);
    }

    [Fact]
    public async Task CheckAllAsync_EmptyQueriesList_ReturnsHealthyWithNoResults()
    {
        var mock = new Mock<ISqlQueryExecutor>();
        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig
            {
                Name = "check1", Description = "Empty Check", ConnectionString = "Server=fake;",
                Queries = []
            }
        };

        var result = await checker.CheckAllAsync(checks, 30);

        mock.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                        It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        result["check1"].Status.Should().Be(MetricStatus.Healthy);
        result["check1"].Results.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAllAsync_BlankConnectionString_CheckIsFilteredAndNotInResult()
    {
        var mock = new Mock<ISqlQueryExecutor>();
        var checker = BuildChecker(mock);
        var checks = new[]
        {
            new SqlQueryCheckConfig { Name = "no-conn", Description = "No Conn", ConnectionString = "   ", Queries = [QueryConfig()] },
            new SqlQueryCheckConfig { Name = "valid",   Description = "Valid",   ConnectionString = "Server=fake;", Queries = [QueryConfig()] }
        };
        mock.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(),
                                       It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await checker.CheckAllAsync(checks, 30);

        result.Should().NotContainKey("no-conn");
        result.Should().ContainKey("valid");
    }
}
