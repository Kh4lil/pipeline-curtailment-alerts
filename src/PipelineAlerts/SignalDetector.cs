using System.Text.RegularExpressions;

namespace PipelineAlerts;

internal static class SignalDetector
{
    internal static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Keywords = new(
        @"(?<!\bno[ \t]+)\b(?:force\s+majeure|outages?|curtailments?)\b", MatchOptions);

    private static readonly Regex IssuerHeading = new(
        @"^[ \t]*Sabine[ \t]+Pipe[ \t]*Line(?:,?[ \t]+LLC)?\.?[ \t]*\r?$",
        MatchOptions | RegexOptions.Multiline);

    private static readonly Regex Areas = new(
        @"\b(?:Louisiana|Henry\s+Hub|Sabine\s+Pipe\s*Line|Bridgeline\s+East|Columbia\s+Gulf|Gulf\s*South|NGPL|Natural\s+Gas\s+Pipeline\s+Company\s+of\s+America)\b|(?<=\p{L},)[ \t]*(?<postal>(?-i:LA))\b(?![-/]\w)",
        MatchOptions);

    private static readonly Regex Volumes = new(
        @"(?<![\w.,-])(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]+)?k?\s*(?:MMBtu|Dth)(?:\s*(?:/\s*(?:day|d)|per\s+day))?\b",
        MatchOptions);

    internal static TradingSignal? Match(Notice notice, DateTimeOffset now)
    {
        if (notice.PostedAt < now - Lookback || notice.PostedAt > now)
            return null;

        string text = $"{notice.Title}\n{notice.Body}";
        var keyword = Keywords.Match(text);
        var bodyWithoutHeading = IssuerHeading.Replace(notice.Body, "");
        var area = Areas.Match($"{notice.Title}\n{notice.Location}\n{bodyWithoutHeading}");
        if (!keyword.Success || !area.Success)
            return null;

        var volume = Volumes.Match(text);
        return new TradingSignal(
            notice,
            Normalize(keyword.Value),
            area.Groups["postal"].Success ? "Louisiana" : Normalize(area.Value),
            volume.Success ? Normalize(volume.Value) : null);
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ");
}
