using FluentAssertions;
using Minicon.SimpleAdmin.Models.Status;
using Minicon.SimpleAdmin.WebUI.Helpers;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public class DisplayHelpersTests
{
    [Theory]
    [InlineData(0, 0, 30, "< 1 min")]
    [InlineData(0, 0, 12 * 60, "12 min")]
    [InlineData(0, 3, 12 * 60, "3 h 12 min")]
    [InlineData(2, 3, 0, "2 T 3 h")]
    public void Duration_IncludesDaysAndHours(int days, int hours, int seconds, string expected)
    {
        Display.Duration(new TimeSpan(days, hours, 0, 0) + TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void Duration_Negative_IsTreatedAsZero()
    {
        Display.Duration(TimeSpan.FromMinutes(-5)).Should().Be("< 1 min");
    }

    [Fact]
    public void AsUtc_UnspecifiedKind_IsInterpretedAsUtc()
    {
        var unspecified = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Unspecified);

        var utc = Display.AsUtc(unspecified);

        utc.Kind.Should().Be(DateTimeKind.Utc);
        utc.Hour.Should().Be(12);
        Display.Iso(unspecified).Should().StartWith("2026-10-14T12:00:00");
    }

    [Theory]
    [InlineData(ServiceStatus.Healthy, "Gesund", "ok")]
    [InlineData(ServiceStatus.Degraded, "Beeinträchtigt", "warn")]
    [InlineData(ServiceStatus.Unhealthy, "Kritisch", "crit")]
    [InlineData(ServiceStatus.Unreachable, "Nicht erreichbar", "crit")]
    [InlineData(ServiceStatus.Acknowledged, "Quittiert", "ack")]
    [InlineData(ServiceStatus.Unknown, "Unbekannt", "muted")]
    public void ServiceStatus_HasGermanLabelAndTone(ServiceStatus status, string label, string tone)
    {
        Display.Label(status).Should().Be(label);
        Display.Tone(status).Should().Be(tone);
    }
}

public class ChartGeometryTests
{
    [Fact]
    public void LinePath_ScalesIntoBox_WithInvariantNumbers()
    {
        var geo = new ChartGeometry([0, 50, 100], 200, 100);

        geo.LinePath().Should().Be("M0 100 L100 50 L200 0");
        geo.AreaPath().Should().Be("M0 100 L100 50 L200 0 L200 100 L0 100 Z");
    }

    [Fact]
    public void IncludeRange_WidensScale()
    {
        var geo = new ChartGeometry([40, 60], 100, 100, includeMin: 0, includeMax: 100);

        geo.Y(40).Should().BeApproximately(60, 0.001);
        geo.Y(60).Should().BeApproximately(40, 0.001);
    }

    [Fact]
    public void FlatSeries_DoesNotDivideByZero()
    {
        var geo = new ChartGeometry([5, 5, 5], 100, 100);

        geo.LinePath().Should().NotContain("NaN").And.NotContain("Infinity");
    }

    [Fact]
    public void SinglePoint_RendersHorizontalLine()
    {
        var geo = new ChartGeometry([7], 100, 50);

        geo.LinePath().Should().StartWith("M0 ").And.Contain("L100 ");
    }

    [Fact]
    public void Coordinates_UseDotAsDecimalSeparator_RegardlessOfCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            ChartGeometry.F(12.5).Should().Be("12.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
