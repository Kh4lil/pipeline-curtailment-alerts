using System.Net;
using System.Net.Http.Headers;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PipelineAlerts;

internal sealed class SampleBulletin(DateTimeOffset now, bool historical = false) : HttpMessageHandler
{
    internal static readonly DateTimeOffset HistoricalAsOf = new(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        var route = (Path.GetFileName(uri.AbsolutePath), uri.Query);
        HttpContent? content = historical ? HistoricalContent(route) : route switch
        {
            ("notices.cfm", "?type=1") => Listing(
                Row(now.AddHours(-2), 9001, 1, "Operational Alert", "Henry Hub compressor restriction"),
                Row(now.AddDays(-4), 9004, 1, "Operational Alert", "Older Louisiana outage")),
            ("notices.cfm", "?type=2") => Listing(
                Row(now.AddHours(-8), 9002, 2, "Customer Services Update", "Shipper portal maintenance")),
            ("notices.cfm", "?type=3") => Listing(
                Row(now.AddDays(-1), 9003, 3, "Maintenance<br>Planned Service Outage", "Planned outage at Beaumont interconnect")),
            ("notices.cfm", "?type=4" or "?type=5") => Listing(),
            ("notice_details_new.cfm", "?id=9001&type=1") => Detail("<iframe src=\"getNoticeText.cfm?id=9001\"></iframe>"),
            ("notice_details_new.cfm", "?id=9002&type=2") => Detail(
                "Routine portal maintenance for Louisiana shippers. Nomination services remain available."),
            ("notice_details_new.cfm", "?id=9003&type=3") => Detail(
                "Affected segment: Beaumont, Texas<br>Flow will be restricted during the outage."),
            ("getNoticeText.cfm", "?id=9001") => SamplePdf(),
            _ => null
        };

        return Task.FromResult(content is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static HttpContent? HistoricalContent((string File, string Query) route) => route switch
    {
        ("notices.cfm", "?type=3") => Listing(Row(
            new DateTimeOffset(2026, 6, 19, 13, 27, 34, TimeSpan.FromHours(-5)),
            440, 3, "Maintenance<br>Other<br>Planned Service Outage", "Planned Service Outage SABINE JUNE 23-24")),
        ("notices.cfm", "?type=1" or "?type=2" or "?type=4" or "?type=5") => Listing(),
        ("notice_details_new.cfm", "?id=440&type=3") => Detail("<iframe src=\"getNoticeText.cfm?id=440\"></iframe>"),
        ("getNoticeText.cfm", "?id=440") => HistoricalPdf(),
        _ => null
    };

    private static StreamContent HistoricalPdf()
    {
        var pdf = typeof(SampleBulletin).Assembly.GetManifestResourceStream("SabineNotice440.pdf")
            ?? throw new InvalidDataException("The PDF for Sabine notice 440 is missing from the replay.");
        var content = new StreamContent(pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return content;
    }

    private static StringContent Listing(params string[] rows) =>
        new($"""
            <table>
              <tr><td><strong>Notice Type</strong></td><td><strong>Posted<br>Date/Time</strong></td>
                <td>Effective Date</td><td>End Date</td><td>Identifier</td><td>Subject</td></tr>
              {string.Join("\n", rows)}
            </table>
            """);

    private static string Row(DateTimeOffset postedAt, int id, int type, string tags, string title)
    {
        var local = TimeZoneInfo.ConvertTime(postedAt, SabineNoticeSource.BulletinTimeZone);
        return FormattableString.Invariant($"""
            <tr><td>{tags}</td><td id="pDate">{local:MMM d, yyyy}<br>{local:hh:mm:ss tt}</td>
              <td></td><td></td><td>{id}</td>
              <td><a class="trview" href="notice_details_new.cfm?id={id}&amp;type={type}">{title}</a></td></tr>
            """);
    }

    private static StringContent Detail(string body) =>
        new($"<table><tr><td colspan='2'>Notice Text</td></tr><tr><td colspan='2'>{body}</td></tr></table>");

    private static ByteArrayContent SamplePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Affected location: Henry Hub, Erath, Louisiana", 12, new PdfPoint(40, 750), font);
        page.AddText("A compressor outage reduced available flow by 48,000 Dth/day.", 12, new PdfPoint(40, 730), font);
        var content = new ByteArrayContent(builder.Build());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return content;
    }
}
