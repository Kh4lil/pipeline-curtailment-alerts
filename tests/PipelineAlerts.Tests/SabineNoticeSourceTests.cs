using System.Net;
using System.Net.Http.Headers;
using PipelineAlerts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PipelineAlerts.Tests;

public sealed class SabineNoticeSourceTests
{
    [Theory]
    [InlineData(1, -6)]
    [InlineData(7, -5)]
    public async Task ReadsRecentNoticesAndTheirAttachmentsUsingCentralTime(int month, int expectedOffset)
    {
        var now = new DateTimeOffset(2026, month, 8, 12, 30, 0, TimeSpan.Zero);
        using var client = new HttpClient(new SampleBulletin(now));

        var notices = await new SabineNoticeSource(client).FetchAsync(now.AddDays(-3), now);

        Assert.Collection(notices,
            critical =>
            {
                Assert.Equal("Sabine Pipe Line", critical.Pipeline);
                Assert.Equal("Critical / Operational Alert", critical.Type);
                Assert.Equal("Henry Hub compressor restriction", critical.Title);
                Assert.Equal(now.AddHours(-2), critical.PostedAt);
                Assert.Equal(TimeSpan.FromHours(expectedOffset), critical.PostedAt.Offset);
                Assert.Equal("Henry Hub, Erath, Louisiana", critical.Location);
                Assert.Equal("https://www.gasnom.com/ip/SABINE/notice_details_new.cfm?id=9001&type=1", critical.Link.AbsoluteUri);
                Assert.Contains("48,000 Dth/day", critical.Body);
            },
            informational =>
            {
                Assert.StartsWith("Non-Critical", informational.Type);
                Assert.Equal("Shipper portal maintenance", informational.Title);
            },
            planned =>
            {
                Assert.Equal("Planned Service Outage / Maintenance", planned.Type);
                Assert.Equal("Beaumont, Texas", planned.Location);
                Assert.Contains("during the outage", planned.Body);
            });
    }

    [Theory]
    [InlineData("GULF SOUTH - HH", "112564", false)]
    [InlineData("SONAT - HH", "46786", false)]
    [InlineData("KM Texas - High", "463541", true)]
    public async Task ReadsLocationAndMeterFromSabinePdfTable(string location, string meter, bool multipleRows)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(multipleRows ? "Location:  Loc #:" : "Location Name  Loc #", 12, new PdfPoint(40, 750), font);
        var prefix = multipleRows ? "• " : "";
        page.AddText($"{prefix}{location}  {meter}", 12, new PdfPoint(40, 730), font);
        var expected = $"{location} (meter {meter})";
        if (multipleRows)
        {
            page.AddText("• Trunkline Holmwood  482116", 12, new PdfPoint(40, 710), font);
            expected += " / Trunkline Holmwood (meter 482116)";
        }

        var now = new DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero);
        using var client = new HttpClient(new BulletinOverride(new SampleBulletin(now),
            uri => uri.Query == "?id=9001" ? Pdf(builder.Build()) : null));
        var notices = await new SabineNoticeSource(client).FetchAsync(now.AddDays(-3), now);

        Assert.Equal(expected, notices.First().Location);
    }

    [Fact]
    public async Task ReadsExplicitLocationIdsFromBothPdfColumns()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Acadian- HH (Loc 11203)        NGPL- HH (Loc 44949)", 12, new PdfPoint(40, 750), font);
        page.AddText("Gulf South- HH (Loc 112564)   Trunkline Rec LP- HH (Loc 11200)", 12, new PdfPoint(40, 730), font);
        page.AddText("Capacity is reduced (meter #s 847121, 2441).", 12, new PdfPoint(40, 710), font);

        var now = new DateTimeOffset(2026, 9, 3, 18, 0, 0, TimeSpan.Zero);
        using var client = new HttpClient(new BulletinOverride(new SampleBulletin(now),
            uri => uri.Query == "?id=9001" ? Pdf(builder.Build()) : null));

        var notices = await new SabineNoticeSource(client).FetchAsync(now.AddDays(-3), now);

        Assert.Equal("Acadian- HH (meter 11203) / NGPL- HH (meter 44949) / Gulf South- HH (meter 112564) / Trunkline Rec LP- HH (meter 11200)",
            notices.First().Location);
    }

    [Fact]
    public async Task SkipsFutureAndStaleDetailsAndIncludesBothWindowEndpoints()
    {
        var now = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
        var since = now.AddDays(-3);
        var rows = new[] { (701, since), (702, since.AddSeconds(-1)), (703, now), (704, now.AddSeconds(1)) }
            .Select(row =>
            {
                var (id, time) = row;
                var local = TimeZoneInfo.ConvertTime(time, SabineNoticeSource.BulletinTimeZone);
                return FormattableString.Invariant($"""
                    <tr><td>Operational Alert</td><td id="pDate">{local:MMM d, yyyy h:mm:ss tt}</td>
                    <td><a class="trview" href="notice_details_new.cfm?id={id}&amp;type=1">Notice {id}</a></td></tr>
                    """);
            });
        using var client = new HttpClient(new BulletinOverride(new SampleBulletin(now), uri => uri.Query switch
        {
            "?type=1" => Html($"<table><tr><td><strong>Notice Type</strong></td></tr>{string.Concat(rows)}</table>"),
            "?id=701&type=1" or "?id=703&type=1" => Detail("Affected segment: Henry Hub"),
            "?id=702&type=1" or "?id=704&type=1" => Html("<table></table>"),
            _ => null
        }));

        var notices = await new SabineNoticeSource(client).FetchAsync(since, now);

        Assert.Equal(new[] { now, since },
            notices.Where(notice => notice.Type.StartsWith("Critical", StringComparison.Ordinal)).Select(notice => notice.PostedAt));
    }

    [Fact]
    public async Task ReadsCityAndStateFromNoticeProse()
    {
        var now = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
        using var client = new HttpClient(new BulletinOverride(new SampleBulletin(now), uri =>
            uri.Query == "?id=9002&type=2"
                ? Detail("An outage at the Gillis Compressor Station in Starks, LA reduced deliveries by 12,000 Dth/day.")
                : null));

        var notices = await new SabineNoticeSource(client).FetchAsync(now.AddDays(-3), now);

        var notice = Assert.Single(notices, notice => notice.Link.Query == "?id=9002&type=2");
        Assert.Equal("Starks, LA", notice.Location);
    }

    [Fact]
    public async Task UnreadablePdfFailsWithNoticeContext()
    {
        var now = new DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero);
        var bytes = "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8.ToArray();
        using var client = new HttpClient(new BulletinOverride(new SampleBulletin(now),
            uri => uri.Query == "?id=9001" ? Pdf(bytes) : null));
        var source = new SabineNoticeSource(client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.FetchAsync(now.AddDays(-3), now));

        Assert.Contains("?id=9001&type=1", error.Message);
        Assert.Contains("unreadable PDF", error.Message);
        Assert.IsType<PdfDocumentFormatException>(error.InnerException);
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html)
    };

    private static HttpResponseMessage Detail(string body) =>
        Html($"<table><tr><td>Notice Text</td></tr><tr><td>{body}</td></tr></table>");

    private static HttpResponseMessage Pdf(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class BulletinOverride(HttpMessageHandler bulletin, Func<Uri, HttpResponseMessage?> respond)
        : DelegatingHandler(bulletin)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request.RequestUri!) is { } response
                ? Task.FromResult(response)
                : base.SendAsync(request, cancellationToken);
    }
}
