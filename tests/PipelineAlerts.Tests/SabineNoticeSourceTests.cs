using System.Net;
using System.Net.Http.Headers;
using PipelineAlerts;
using UglyToad.PdfPig.Core;
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

        var notices = await new SabineNoticeSource(client).FetchAsync(now.AddDays(-3));

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

    [Fact]
    public async Task UnreadablePdfFailsWithNoticeContext()
    {
        var now = new DateTimeOffset(2026, 7, 8, 12, 30, 0, TimeSpan.Zero);
        using var client = new HttpClient(new CorruptAttachmentHandler(new SampleBulletin(now)));
        var source = new SabineNoticeSource(client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.FetchAsync(now.AddDays(-3)));

        Assert.Contains("?id=9001&type=1", error.Message);
        Assert.Contains("unreadable PDF", error.Message);
        Assert.IsType<PdfDocumentFormatException>(error.InnerException);
    }

    private sealed class CorruptAttachmentHandler(HttpMessageHandler bulletin) : DelegatingHandler(bulletin)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Query != "?id=9001")
            {
                return base.SendAsync(request, cancellationToken);
            }

            var content = new ByteArrayContent("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
