using FluentAssertions;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public class ChangelogParserTests
{
    private const string Sample = """
        # Changelog

        Header text that belongs to no release.

        ## [2.1.0] – 2026-10-20 – Neue Seite

        **Kurzfassung der Version.**

        ### Neu
        - **Seite** „Neuigkeiten“ mit `CHANGELOG.md`
          - Unterpunkt eins
          - Unterpunkt zwei
        - Zweiter Punkt,
          mit Fortsetzungszeile

        ### Intern
        - Parser-Tests

        ## [2.0.0] – 2026-10-01

        ### Behoben
        - Fehler behoben

        [2.1.0]: https://github.com/example/compare/v2.0.0...v2.1.0
        [2.0.0]: https://github.com/example/releases/tag/v2.0.0
        """;

    [Fact]
    public void Parse_ReadsReleasesNewestFirst_WithDateAndTitle()
    {
        var releases = ChangelogParser.Parse(Sample);

        releases.Select(r => r.Version).Should().Equal("2.1.0", "2.0.0");
        releases[0].Date.Should().Be("2026-10-20");
        releases[0].TitleHtml.Should().Be("Neue Seite");
        releases[1].TitleHtml.Should().BeNull();
    }

    [Fact]
    public void Parse_SummaryLine_IsRenderedAsLead()
    {
        var release = ChangelogParser.Parse(Sample)[0];

        release.SummaryHtml.Should().ContainSingle()
            .Which.Should().Be("<strong>Kurzfassung der Version.</strong>");
    }

    [Fact]
    public void Parse_SectionsItemsAndNestedItems()
    {
        var release = ChangelogParser.Parse(Sample)[0];

        release.Sections.Select(s => s.Title).Should().Equal("Neu", "Intern");
        var items = release.Sections[0].Items;
        items.Should().HaveCount(2);
        items[0].Html.Should().Be("<strong>Seite</strong> „Neuigkeiten“ mit <code>CHANGELOG.md</code>");
        items[0].ChildrenHtml.Should().Equal("Unterpunkt eins", "Unterpunkt zwei");
        items[1].Html.Should().Be("Zweiter Punkt, mit Fortsetzungszeile");
    }

    [Theory]
    [InlineData("Intern", true)]
    [InlineData("Entwickler", true)]
    [InlineData("entwickler", true)]
    [InlineData("Neu", false)]
    [InlineData("Behoben", false)]
    [InlineData("Hinweise", false)]
    public void Section_DeveloperSections_AreTechnical(string title, bool technical)
    {
        new ChangelogSection(title, [], []).IsTechnical.Should().Be(technical);
    }

    [Fact]
    public void Parse_IgnoresHeaderAndLinkReferences()
    {
        var releases = ChangelogParser.Parse(Sample);

        releases[1].Sections.Should().ContainSingle()
            .Which.Items.Should().ContainSingle()
            .Which.Html.Should().Be("Fehler behoben");
        releases.SelectMany(r => r.SummaryHtml).Should().NotContain(p => p.Contains("Header text"));
    }

    [Fact]
    public void Parse_BulletWithoutSection_GoesToImplicitSection()
    {
        var releases = ChangelogParser.Parse("## [1.0.0]\n- Erster Punkt\n");

        releases.Should().ContainSingle()
            .Which.Sections.Should().ContainSingle()
            .Which.Items.Single().Html.Should().Be("Erster Punkt");
    }

    [Fact]
    public void Parse_CodeFence_IsKeptAsEncodedText()
    {
        var releases = ChangelogParser.Parse("## [1.0.0]\n### Hinweise\n```json\n{ \"a\": \"<b>\" }\n```\n");

        releases[0].Sections[0].ParagraphsHtml.Should().Equal("{ &quot;a&quot;: &quot;&lt;b&gt;&quot; }");
    }

    [Fact]
    public void RenderInline_EncodesHtml()
    {
        ChangelogParser.RenderInline("<script>alert(1)</script> & **fett**")
            .Should().Be("&lt;script&gt;alert(1)&lt;/script&gt; &amp; <strong>fett</strong>");
    }

    [Fact]
    public void RenderInline_CodeSpan_IsLiteral()
    {
        ChangelogParser.RenderInline("`**nicht fett** <x>`")
            .Should().Be("<code>**nicht fett** &lt;x&gt;</code>");
    }

    [Theory]
    [InlineData("[Doku](https://example.org/a?b=1&c=2)", "<a href=\"https://example.org/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener\">Doku</a>")]
    [InlineData("[Seite](/Problems)", "<a href=\"/Problems\" target=\"_blank\" rel=\"noopener\">Seite</a>")]
    [InlineData("[böse](javascript:alert%281%29)", "böse")]
    [InlineData("[extern](//evil.example)", "extern")]
    public void RenderInline_OnlySafeLinksBecomeAnchors(string markdown, string expected)
    {
        ChangelogParser.RenderInline(markdown).Should().Be(expected);
    }

    [Fact]
    public void RenderInline_LinkUrlCannotBreakOutOfAttribute()
    {
        ChangelogParser.RenderInline("[x](https://a.example/\"onmouseover=\"alert(1))")
            .Should().NotContain("\"onmouseover=\"");
    }

    [Fact]
    public void Provider_MissingResource_ReturnsNoReleases()
    {
        new ChangelogProvider(() => null).Releases.Should().BeEmpty();
    }

    [Fact]
    public void Provider_EmbeddedChangelog_ContainsTheInstalledVersion()
    {
        // Guards the release rule "every release gets a CHANGELOG.md entry": the newest entry
        // of the embedded file must be the version this assembly is built as.
        var releases = new ChangelogProvider().Releases;

        releases.Should().NotBeEmpty("CHANGELOG.md must be embedded into the WebUI assembly");
        releases[0].Version.Should().Be(ChangelogProvider.InstalledVersion);
    }
}
