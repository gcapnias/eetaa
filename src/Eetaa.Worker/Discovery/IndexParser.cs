using System.Net;
using System.Text.RegularExpressions;

namespace Eetaa.Worker.Discovery;

public sealed partial class IndexParser
{
    // Regex matching anchor tags linking to settlements:
    // e.g. <a href="oikmet_details.php?id=25734">21η Απριλίου (Θεσσαλονίκης)</a>
    // or <a href='oikmet.php?id=...'> or oikmet2csv.php?sid=...
    [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""']?([^""'>\s]+)[""']?[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"(?:[?&]|\b)(?:s?id)=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SettlementIdParamRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"oik_metaboles\.php\?[^""'>\s]*\bpg=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PaginationPageRegex();

    [GeneratedRegex(@"<table\b[^>]*\bid\s*=\s*[""']?results[""']?[^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultsTableRegex();

    /// <summary>
    /// Parses settlements (ID and name hint) from an EETAA index page HTML string.
    /// Safely handles irregular markup, unclosed tags, and skips non-settlement links.
    /// </summary>
    public IReadOnlyList<DiscoveredSettlement> ParseSettlements(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        // Scope parsing to the results table if present; fallback to full HTML if table tag is missing/irregular
        var tableMatch = ResultsTableRegex().Match(html);
        var searchScope = tableMatch.Success ? tableMatch.Groups[1].Value : html;

        // Target links pointing to settlement details or CSV endpoints:
        // oikmet_details.php?id=..., oikmet.php?id=..., or oikmet2csv.php?sid=...
        var matches = AnchorRegex().Matches(searchScope);
        var results = new List<DiscoveredSettlement>(matches.Count);

        foreach (Match match in matches)
        {
            var href = match.Groups[1].Value;
            var innerHtml = match.Groups[2].Value;

            // Only consider links with settlement endpoints: oikmet_details, oikmet, or oikmet2csv
            if (!href.Contains("oikmet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if href contains settlement id (id=\d+ or sid=\d+)
            var idMatch = SettlementIdParamRegex().Match(href);
            if (!idMatch.Success)
            {
                continue;
            }

            if (!int.TryParse(idMatch.Groups[1].Value, out var settlementId))
            {
                continue;
            }

            // Strip nested HTML tags and decode HTML entities
            var cleanText = HtmlTagRegex().Replace(innerHtml, string.Empty);
            cleanText = WebUtility.HtmlDecode(cleanText).Trim();

            results.Add(new DiscoveredSettlement(settlementId, cleanText));
        }

        return results;
    }

    /// <summary>
    /// Deduplicates settlements by SettlementId, preserving the first occurrence.
    /// </summary>
    public IEnumerable<DiscoveredSettlement> Deduplicate(IEnumerable<DiscoveredSettlement> settlements)
    {
        var seenIds = new HashSet<int>();
        foreach (var settlement in settlements)
        {
            if (seenIds.Add(settlement.SettlementId))
            {
                yield return settlement;
            }
        }
    }

    /// <summary>
    /// Extracts the highest page number found in the pagination controls.
    /// Returns 1 if no pagination is found.
    /// </summary>
    public int ParseTotalPages(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return 1;
        }

        var matches = PaginationPageRegex().Matches(html);
        var maxPage = 1;

        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var page) && page > maxPage)
            {
                maxPage = page;
            }
        }

        return maxPage;
    }
}
