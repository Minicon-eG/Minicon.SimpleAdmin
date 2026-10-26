using System.Text;
using System.Text.RegularExpressions;

namespace Minicon.SimpleAdmin.WebUI.Services;

/// <summary>One version block of CHANGELOG.md (<c>## [X.Y.Z] – date – headline</c>).</summary>
internal sealed record ChangelogRelease(
    string Version,
    string? Date,
    string? TitleHtml,
    IReadOnlyList<string> SummaryHtml,
    IReadOnlyList<ChangelogSection> Sections);

/// <summary>A <c>### Neu / Geändert / Behoben / Intern …</c> block inside a release.</summary>
internal sealed record ChangelogSection(
    string Title,
    IReadOnlyList<ChangelogItem> Items,
    IReadOnlyList<string> ParagraphsHtml)
{
    private static readonly string[] TechnicalTitles = ["Intern", "Entwickler"];

    /// <summary>
    /// Sections for developers/operators ("Entwickler", "Intern"): packages, hosts, APIs,
    /// dependencies, tests. Never shown on the "Neuigkeiten" page, which is for users only.
    /// </summary>
    public bool IsTechnical => TechnicalTitles.Contains(Title, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A bullet point with optional nested bullets; both already rendered to safe HTML.</summary>
internal sealed record ChangelogItem(string Html, IReadOnlyList<string> ChildrenHtml);

/// <summary>
/// Parses the repository's CHANGELOG.md (Keep-a-Changelog subset: release/section headings,
/// paragraphs, bullets with one nesting level, <c>**bold**</c>, <c>`code`</c>, links) into
/// releases with pre-rendered HTML. Everything is HTML-encoded first; only http(s) and
/// relative links become anchors, so the output is safe to emit with <c>Html.Raw</c>.
/// </summary>
internal static class ChangelogParser
{
    private static readonly Regex ReleaseHeading = new(
        @"^##\s+\[(?<version>[^\]]+)\]\s*(?:[–—-]\s*(?<date>\d{4}-\d{2}-\d{2}))?\s*(?:[–—-]\s*(?<title>.+?))?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex LinkReference = new(@"^\[[^\]]+\]:\s*\S+", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(?<indent>\s*)[-*]\s+(?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex CodeSpan = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

    public static IReadOnlyList<ChangelogRelease> Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        ReleaseBuilder? release = null;
        SectionBuilder? section = null;
        ItemBuilder? item = null;
        var inFence = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var match = ReleaseHeading.Match(line);
                release = match.Success
                    ? new ReleaseBuilder(
                        match.Groups["version"].Value.Trim(),
                        match.Groups["date"].Success ? match.Groups["date"].Value : null,
                        match.Groups["title"].Success ? match.Groups["title"].Value : null)
                    : null;
                section = null;
                item = null;
                continue;
            }

            if (release is null)
                continue; // file header before the first release

            if (inFence)
            {
                // Code blocks are rare in release notes; keep their text as a plain paragraph line.
                if (line.Length > 0)
                    AddParagraph(Encode(line.Trim()));
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                section = new SectionBuilder(line[4..].Trim());
                release.Sections.Add(section);
                item = null;
                continue;
            }

            if (line.Length == 0 || LinkReference.IsMatch(line))
            {
                item = line.Length == 0 ? item : null;
                continue;
            }

            var bullet = Bullet.Match(line);
            if (bullet.Success)
            {
                var html = RenderInline(bullet.Groups["text"].Value);
                if (bullet.Groups["indent"].Value.Length >= 2 && item is not null)
                {
                    item.Children.Add(html);
                }
                else
                {
                    section ??= AddImplicitSection();
                    item = new ItemBuilder(html);
                    section.Items.Add(item);
                }
                continue;
            }

            if (char.IsWhiteSpace(rawLine, 0) && item is not null)
            {
                // Indented continuation of the previous bullet.
                var continuation = RenderInline(line.Trim());
                if (item.Children.Count > 0)
                    item.Children[^1] += " " + continuation;
                else
                    item.Html += " " + continuation;
                continue;
            }

            item = null;
            AddParagraph(RenderInline(line.Trim()));
        }

        Flush();
        return releases;

        void AddParagraph(string html)
        {
            if (section is not null)
                section.Paragraphs.Add(html);
            else
                release!.Summary.Add(html);
        }

        SectionBuilder AddImplicitSection()
        {
            var implicitSection = new SectionBuilder("Änderungen");
            release!.Sections.Add(implicitSection);
            return implicitSection;
        }

        void Flush()
        {
            if (release is not null)
                releases.Add(release.Build());
        }
    }

    /// <summary>
    /// Renders one line of inline markdown to HTML. Code spans are taken literally;
    /// links are only emitted for http(s) and site-relative targets.
    /// </summary>
    internal static string RenderInline(string text)
    {
        var html = new StringBuilder();
        var position = 0;
        foreach (Match code in CodeSpan.Matches(text))
        {
            html.Append(RenderText(text[position..code.Index]));
            html.Append("<code>").Append(Encode(code.Groups[1].Value)).Append("</code>");
            position = code.Index + code.Length;
        }
        html.Append(RenderText(text[position..]));
        return html.ToString();
    }

    private static string RenderText(string text)
    {
        var encoded = Encode(text);
        encoded = Link.Replace(encoded, m =>
        {
            var url = m.Groups[2].Value;
            var safe = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                       || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal));
            return safe
                ? $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">{m.Groups[1].Value}</a>"
                : m.Groups[1].Value;
        });
        return Bold.Replace(encoded, "<strong>$1</strong>");
    }

    /// <summary>
    /// Escapes the HTML-significant characters only. WebUtility.HtmlEncode would also turn every
    /// umlaut into a numeric entity, which is correct but bloats the German text.
    /// </summary>
    private static string Encode(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");

    private sealed class ReleaseBuilder(string version, string? date, string? title)
    {
        public List<string> Summary { get; } = [];
        public List<SectionBuilder> Sections { get; } = [];

        public ChangelogRelease Build() => new(
            version,
            date,
            title is null ? null : RenderInline(title),
            Summary,
            Sections.Select(s => s.Build()).ToList());
    }

    private sealed class SectionBuilder(string title)
    {
        public List<ItemBuilder> Items { get; } = [];
        public List<string> Paragraphs { get; } = [];

        public ChangelogSection Build() => new(
            title,
            Items.Select(i => new ChangelogItem(i.Html, i.Children)).ToList(),
            Paragraphs);
    }

    private sealed class ItemBuilder(string html)
    {
        public string Html { get; set; } = html;
        public List<string> Children { get; } = [];
    }
}
