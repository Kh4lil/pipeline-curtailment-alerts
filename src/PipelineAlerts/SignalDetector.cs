using System.Text.RegularExpressions;

namespace PipelineAlerts;

internal static class SignalDetector
{
    internal static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Keywords = new(
        @"(?<dismissed>\bno[ \t]+)?\b(?<evidence>force\s+majeure|outages?|curtailments?)\b"
        + @"(?<dismissed>[ \t]+(?:(?:has|have)[ \t]+ended|(?:(?:has|have)[ \t]+been|was|were)[ \t]+(?:resolved|lifted|cancelled|canceled)|lifted)\b)?",
        MatchOptions);

    private static readonly Regex IssuerHeading = new(
        @"^[ \t]*Sabine[ \t]+Pipe[ \t]*Line(?:,?[ \t]+LLC)?\.?[ \t]*\r?$",
        MatchOptions | RegexOptions.Multiline);

    private static readonly Regex Areas = new(
        @"(?<evidence>\b(?:Louisiana|Henry\s+Hub|Sabine\s+Pipe\s*Line|Bridgeline\s+East|SONAT\s*[-–—]\s*HH|Columbia\s+Gulf|Gulf\s*South|NGPL|Natural\s+Gas\s+Pipeline\s+Company\s+of\s+America)\b|(?<=\p{L},)[ \t]*(?<postal>(?-i:LA))\b(?![-/]\w))"
        + @"(?<dismissed>[ \t]+(?:(?:deliveries|flows|operations|service)[ \t]+)?(?:is|are|remains?)[ \t]+unaffected\b)?",
        MatchOptions);

    private static readonly Regex Volumes = new(
        @"(?<![\w.,-])(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]+)?k?\s*(?:MMBtu|Dth)(?:\s*(?:/\s*(?:day|d)|per\s+day))?\b",
        MatchOptions);

    internal static TradingSignal? Match(Notice notice, DateTimeOffset now)
    {
        if (notice.PostedAt < now - Lookback || notice.PostedAt > now)
            return null;

        var keyword = FindEvidence(Keywords, notice.Title, notice.Body);
        var bodyWithoutHeading = IssuerHeading.Replace(notice.Body, "");
        var area = FindEvidence(Areas, $"{notice.Title}\n{notice.Location}", bodyWithoutHeading);
        if (keyword is null || area is null)
            return null;

        var volume = Volumes.Match($"{notice.Title}\n{notice.Body}");
        return new TradingSignal(
            notice,
            keyword,
            area,
            volume.Success ? Normalize(volume.Value) : null);
    }

    private static string? FindEvidence(Regex pattern, string summary, string body)
    {
        var bodyMatches = pattern.Matches(body);
        var dismissed = bodyMatches.Where(match => match.Groups["dismissed"].Success)
            .Select(EvidenceText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The notice body can disqualify a bare mention repeated in the title or extracted location.
        var eligible = pattern.Matches(summary).FirstOrDefault(match =>
            !match.Groups["dismissed"].Success && !dismissed.Contains(EvidenceText(match)))
            ?? bodyMatches.FirstOrDefault(match => !match.Groups["dismissed"].Success);
        return eligible is null ? null : EvidenceText(eligible);
    }

    private static string EvidenceText(Match match) =>
        match.Groups["postal"].Success ? "Louisiana" : Normalize(match.Groups["evidence"].Value);

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ");
}
