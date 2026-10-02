using FluentAssertions;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Tests.Services;

public class NotificationMailRendererTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);

    private static NotificationsFeatureConfig Notif() => new() { ReNotifyIntervalHours = 4 };

    private static List<(string ServerId, ActiveProblem Problem)> Due(params (string, ActiveProblem)[] items)
        => items.ToList();

    private static List<(string ServerId, NotifyEntry Entry, string ProblemId)> NoResolved()
        => new();

    [Fact]
    public void Render_DueProblem_PlainTextAndHtmlCarryMessageAndDeepLink()
    {
        var problem = ActiveProblem.ServiceDown("Spooler", "Druckwarteschlange");
        var mail = NotificationMailRenderer.Render(Notif(), Due(("web01", problem)), NoResolved(), Now, "https://mon.test");

        // Plain text (the multipart/alternative fallback).
        mail.PlainText.Should().Contain("Druckwarteschlange");
        mail.PlainText.Should().Contain("https://mon.test/Problems?ackServer=web01");

        // HTML template.
        mail.Html.Should().StartWith("<!DOCTYPE html>");
        mail.Html.Should().Contain("SimpleAdmin Monitoring");
        mail.Html.Should().Contain("KRITISCH");
        mail.Html.Should().Contain("Druckwarteschlange");
        mail.Html.Should().Contain("https://mon.test/Problems?ackServer=web01");
        mail.Html.Should().Contain(">Quittieren</a>");
    }

    [Fact]
    public void Render_Resolved_StatesExactClearedProblem_NotJustThatSomethingWasResolved()
    {
        var entry = new NotifyEntry
        {
            ServerId = "web01",
            Severity = "Critical",
            Message = "Service 'Druckwarteschlange' ist nicht gestartet",
            FirstSeenAt = Now.AddHours(-3)
        };
        var resolved = new List<(string, NotifyEntry, string)> { ("web01", entry, "service_spooler_stopped") };

        var mail = NotificationMailRenderer.Render(Notif(), Due(), resolved, Now, "https://mon.test");

        mail.PlainText.Should().Contain("Behoben / Quittiert");
        mail.PlainText.Should().Contain("ist nicht gestartet"); // the exact problem, not just "Problem behoben"
        mail.Html.Should().Contain("Behoben / Quittiert");
        mail.Html.Should().Contain("ist nicht gestartet");
        mail.Html.Should().Contain("war seit"); // duration detail
    }

    [Fact]
    public void Render_ResolvedWithoutMessage_FallsBackToSeverity()
    {
        // State written by an older notifier version has no Message.
        var entry = new NotifyEntry { ServerId = "web01", Severity = "Warning" };
        var resolved = new List<(string, NotifyEntry, string)> { ("web01", entry, "cpu_usage_warning") };

        var mail = NotificationMailRenderer.Render(Notif(), Due(), resolved, Now, null);

        mail.PlainText.Should().Contain("web01: Warning");
        mail.Html.Should().Contain("Warning");
    }

    [Fact]
    public void Render_HtmlEncodesDynamicContent()
    {
        var problem = new ActiveProblem
        {
            Id = "log_x_match",
            Severity = ProblemSeverity.Warning,
            Message = "Pfad <C:\\temp> & <script>"
        };

        var mail = NotificationMailRenderer.Render(Notif(), Due(("s1", problem)), NoResolved(), Now, null);

        mail.Html.Should().Contain("Pfad &lt;C:\\temp&gt; &amp; &lt;script&gt;");
        mail.Html.Should().NotContain("<script>");
    }

    [Fact]
    public void Render_RemediationStepsAndWiki_AppearInBothFormats()
    {
        var problem = ActiveProblem.FileStuck(
            directory: @"\\share\in",
            filePath: @"\\share\in\order.xml",
            ageMinutes: 90,
            reason: "age",
            isCritical: true,
            wikiUrl: "https://wiki.test/schnittstelle",
            remediationSteps: new List<string> { "Datei prüfen", "Dienst neu starten" });

        var mail = NotificationMailRenderer.Render(Notif(), Due(("biztalk01", problem)), NoResolved(), Now, "https://mon.test");

        mail.PlainText.Should().Contain("Datei prüfen");
        mail.PlainText.Should().Contain("https://wiki.test/schnittstelle");
        // Dynamic content is HTML-encoded (umlauts → numeric entities), so assert on the
        // umlaut-free remediation step and the wiki link to stay robust.
        mail.Html.Should().Contain("Dienst neu starten");
        mail.Html.Should().Contain("https://wiki.test/schnittstelle");
    }

    [Fact]
    public void Render_NoDueOnlyResolved_ShowsEntwarnungBanner()
    {
        var entry = new NotifyEntry { ServerId = "web01", Severity = "Critical", Message = "Server nicht erreichbar" };
        var resolved = new List<(string, NotifyEntry, string)> { ("web01", entry, "server_web01_stale") };

        var mail = NotificationMailRenderer.Render(Notif(), Due(), resolved, Now, "https://mon.test");

        mail.PlainText.Should().Contain("Entwarnung");
        mail.Html.Should().Contain("Entwarnung");
    }
}
