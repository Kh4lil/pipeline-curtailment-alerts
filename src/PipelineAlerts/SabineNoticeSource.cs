using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace PipelineAlerts;

internal sealed class SabineNoticeSource(HttpClient httpClient, Uri? bulletinUrl = null, string? pipelineName = null)
{
    internal const string PipelineName = "Sabine Pipe Line";
    internal static readonly TimeZoneInfo BulletinTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    internal static readonly Uri DefaultBulletin = new("https://www.gasnom.com/ip/SABINE/");

    private readonly Uri bulletin = bulletinUrl ?? DefaultBulletin;
    private readonly string name = pipelineName ?? PipelineName;
    private static readonly (int Id, string Type)[] Categories =
    [
        (1, "Critical"),
        (2, "Non-Critical"),
        (3, "Planned Service Outage"),
        (4, "Waste Heat"),
        (5, "Request to Purchase Releasable Capacity")
    ];

    internal async Task<IReadOnlyList<Notice>> FetchAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var notices = new List<Notice>();
        foreach (var category in Categories)
        {
            var listing = new Uri(bulletin, $"notices.cfm?type={category.Id}");
            var document = Parse(await httpClient.GetStringAsync(listing, cancellationToken));
            var table = document.DocumentNode.SelectSingleNode(
                "//tr[td/strong[normalize-space()='Notice Type']]/ancestor::table[1]")
                ?? throw new InvalidDataException($"The {category.Type} listing did not contain a notice table.");

            foreach (var row in table.SelectNodes(".//tr[td[@id='pDate']]") ?? Enumerable.Empty<HtmlNode>())
            {
                var date = ReadText(row.SelectSingleNode("td[@id='pDate']")!);
                if (!DateTime.TryParseExact(date, "MMM d, yyyy h:mm:ss tt", CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var localTime))
                {
                    throw new InvalidDataException($"Unexpected posting date in the {category.Type} listing.");
                }

                var postedAt = new DateTimeOffset(localTime, BulletinTimeZone.GetUtcOffset(localTime));
                if (postedAt < since || postedAt > until)
                {
                    continue;
                }

                var subject = row.SelectSingleNode("td/a[@class='trview']")
                    ?? throw new InvalidDataException("A notice is missing its subject link.");
                var title = ReadText(subject);
                var link = NoticeLink(subject.GetAttributeValue("href", ""), "notice_details_new.cfm");
                var body = await ReadBodyAsync(link, cancellationToken);
                var tags = ReadText(row.SelectSingleNode("td[1]")!)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var type = string.Join(" / ", tags.Prepend(category.Type).Distinct(StringComparer.OrdinalIgnoreCase));
                notices.Add(new Notice(name, type, title, postedAt, ReadLocation(title, body), link, body));
            }
        }

        return notices.OrderByDescending(notice => notice.PostedAt).ToArray();
    }

    private async Task<string> ReadBodyAsync(Uri notice, CancellationToken cancellationToken)
    {
        var detail = Parse(await httpClient.GetStringAsync(notice, cancellationToken));
        var body = detail.DocumentNode.SelectSingleNode(
            "//tr[td[normalize-space()='Notice Text']]/following-sibling::tr[1]/td")
            ?? throw new InvalidDataException($"Notice {notice.Query} did not contain a notice body.");

        if (body.SelectSingleNode(".//iframe") is not { } frame)
        {
            return ReadText(body);
        }

        // The detail request establishes the anonymous session needed to follow this PDF link.
        var attachment = NoticeLink(frame.GetAttributeValue("src", ""), "getNoticeText.cfm");
        using var response = await httpClient.GetAsync(attachment, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/pdf")
        {
            throw new InvalidDataException($"Notice {notice.Query} did not return its expected PDF attachment.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            return string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or PdfDocumentEncryptedException)
        {
            throw new InvalidDataException($"Notice {notice.Query} contains an unreadable PDF attachment.", ex);
        }
    }

    private static string? ReadLocation(string title, string body)
    {
        var label = Regex.Match(body, @"^[ \t]*(?:Affected[ \t]+)?(?:Locations?|Segments?)(?:\(s\))?[ \t]*:[ \t]*([^\r\n]{1,100})",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (label.Success && !label.Groups[1].Value.Contains("Loc #", StringComparison.OrdinalIgnoreCase))
        {
            return label.Groups[1].Value.Trim();
        }

        var meterTables = Regex.Matches(body, """
            ^[ \t]*(?:
                Location(?:[ \t]+Name|:)[ \t]+Loc[ \t]*\#:?
                | from\ the\ following\ mainline\ receipt\ points[^\r\n:]*:
            )[ \t]*
            (?:
                (?:\r?\n[ \t]*)+ (?:•[ \t]*)?
                (?<name>[^\r\n]{1,100}?) [ \t]{2,} (?<meter>[0-9]+) [ \t]* \r?$
            )+
            """, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace);
        if (meterTables.Count > 0)
        {
            return FormatMeters(meterTables);
        }

        var proseLocation = Regex.Match(body, """
            (?i:\b(?:in|at|near))[ \t]+
            (?:
                (?<location>\p{Lu}[\p{L}.'’-]*(?:[ \t]+\p{Lu}[\p{L}.'’-]*){0,3},[ \t]*(?:LA|TX|(?i:Louisiana|Texas)))\b(?![-/]\w)
                | (?i:the)[ \t]+(?<location>(?:\p{Lu}[\p{L}.'’-]*[ \t]+){1,4}Interconnect)\b
            )
            """, RegexOptions.IgnorePatternWhitespace);
        if (proseLocation.Success)
        {
            return Regex.Replace(proseLocation.Groups["location"].Value, @"[ \t]+", " ");
        }

        var locationRows = Regex.Matches(body, """
            ^[ \t]*(?:•[ \t]*)?
            (?:
                (?<name>[\p{L}\p{N}][\p{L}\p{N} .&'’/–-]{0,99}?) [ \t]+ \(Loc[ \t]+(?<meter>[0-9]+)\)
                (?:[ \t]{2,}|(?=[ \t]*\r?$))
            )+
            [ \t]*\r?$
            """, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace);
        if (locationRows.Count > 0)
        {
            return FormatMeters(locationRows);
        }

        var namedPlace = Regex.Match(title + "\n" + body,
            @"\b(?:Henry\s+Hub|Lake\s+Charles|Jefferson\s+Island|Bridgeline\s+East|Calcasieu|Vermilion|Egan|Louisiana)\b",
            RegexOptions.IgnoreCase);
        return namedPlace.Success ? Regex.Replace(namedPlace.Value, @"\s+", " ") : null;
    }

    private static string FormatMeters(MatchCollection rows) =>
        string.Join(" / ", rows.SelectMany(row => row.Groups["name"].Captures.Select((name, index) =>
        {
            var location = Regex.Replace(name.Value.Trim(), @"[ \t]+", " ");
            return $"{location} (meter {row.Groups["meter"].Captures[index].Value})";
        })));

    private Uri NoticeLink(string href, string file)
    {
        if (Uri.TryCreate(bulletin, HtmlEntity.DeEntitize(href), out var uri)
            && uri.Scheme == bulletin.Scheme && uri.Authority == bulletin.Authority
            && uri.AbsolutePath == bulletin.AbsolutePath + file)
        {
            return uri;
        }

        throw new InvalidDataException($"The notice did not link to its expected {file} page.");
    }

    private static HtmlDocument Parse(string html)
    {
        var document = new HtmlDocument { OptionEnableBreakLineForInnerText = true };
        document.LoadHtml(html);
        return document;
    }

    private static string ReadText(HtmlNode node) => HtmlEntity.DeEntitize(node.InnerText).Trim();
}
