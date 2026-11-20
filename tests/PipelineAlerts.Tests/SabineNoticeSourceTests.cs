using PipelineAlerts;
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
            informational => Assert.Equal("Shipper portal maintenance", informational.Title),
            planned =>
            {
                Assert.Equal("Planned Service Outage / Maintenance", planned.Type);
                Assert.Equal("Beaumont, Texas", planned.Location);
                Assert.Contains("during the outage", planned.Body);
            });
    }
}
