using Xunit;

namespace PipelineAlerts.Tests;

public class SignalDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("force majeure reports a 42,500 Dth/day restriction.", "force majeure", "42,500 Dth/day")]
    [InlineData("OUTAGES affect 8000.5 MMBtu per day.", "OUTAGES", "8000.5 MMBtu per day")]
    [InlineData("A curtailment of 935k MMBtu/day is expected.", "curtailment", "935k MMBtu/day")]
    [InlineData("A curtailment is expected.", "curtailment", null)]
    public void FindsBodyKeywordsAndOptionalVolumes(string body, string keyword, string? volume)
    {
        var signal = Assert.IsType<TradingSignal>(
            SignalDetector.Match(CreateNotice(body, location: "Erath, Louisiana"), Now));

        Assert.Equal(keyword, signal.Keyword);
        Assert.Equal("Louisiana", signal.Area);
        Assert.Equal(volume, signal.Volume);
    }

    [Theory]
    [InlineData("Henry Hub")]
    [InlineData("Sabine Pipe Line")]
    [InlineData("Gulf South")]
    [InlineData("Columbia Gulf")]
    [InlineData("NGPL")]
    public void RecognizesNamedHubConnections(string connection)
    {
        var notice = CreateNotice($"The affected interconnect is {connection}.") with { Title = "Service outage" };
        Assert.Equal(connection, SignalDetector.Match(notice, Now)?.Area);
    }

    [Theory]
    [InlineData("Lake Charles, Louisiana")]
    [InlineData("Lake Charles, LA")]
    public void RecognizesBothSpellingsInTheBodyOrLocation(string location)
    {
        var inBody = CreateNotice($"Outage at the compressor in {location}.");
        var inLocation = CreateNotice("Compressor outage will stop deliveries.", location);

        Assert.Equal("Louisiana", SignalDetector.Match(inBody, Now)?.Area);
        Assert.Equal("Louisiana", SignalDetector.Match(inLocation, Now)?.Area);
    }

    [Theory]
    [InlineData("Current outage in Texas")]
    [InlineData("An outage affected the LA control panel.")]
    [InlineData("Following an outage, LA-12 is undergoing repairs.")]
    [InlineData("Routine update in Louisiana")]
    [InlineData("No outage is expected at Henry Hub. Normal flow continues.")]
    [InlineData("There is no curtailment at Henry Hub.")]
    [InlineData("No force majeure applies in Louisiana.")]
    [InlineData("Dataoutage field in Louisiana")]
    public void RequiresAnAffirmativeWholeKeywordAndAreaEvidence(string body)
    {
        Assert.Null(SignalDetector.Match(CreateNotice(body), Now));
    }

    [Fact]
    public void FindsAnAffirmativeEventAfterANegatedKeyword()
    {
        var notice = CreateNotice("No outage is expected at Henry Hub, but a curtailment was confirmed.");

        Assert.Equal("curtailment", SignalDetector.Match(notice, Now)?.Keyword);
    }

    [Fact]
    public void DoesNotTreatTheIssuerHeadingAsEvidenceForATexasOnlyEvent()
    {
        var notice = CreateNotice(
            "SABINE PIPE LINE LLC\nAffected segment: Beaumont, Texas\nRepairs apply only to Texas operations.",
            "Beaumont, Texas") with { Title = "Outage report" };

        Assert.Null(SignalDetector.Match(notice, Now));
    }

    [Fact]
    public void KeepsThePipelineNameWhenItIsTheAffectedLocation()
    {
        var notice = CreateNotice("A compressor outage was confirmed.", "Sabine Pipe Line");

        Assert.Equal("Sabine Pipe Line", SignalDetector.Match(notice, Now)?.Area);
    }

    [Fact]
    public void UsesBridgelineEastAsEvidenceForAPlannedOutage()
    {
        var notice = CreateNotice(
            "SABINE PIPE LINE LLC\r\nDeliveries to Bridgeline East will be restricted to 350,000 Dth/day.",
            "Bridgeline East") with { Title = "Planned Service Outage SABINE JUNE 23-24" };

        var signal = Assert.IsType<TradingSignal>(SignalDetector.Match(notice, Now));
        Assert.Equal("Outage", signal.Keyword);
        Assert.Equal("Bridgeline East", signal.Area);
        Assert.Equal("350,000 Dth/day", signal.Volume);
    }

    [Theory]
    [InlineData(-4321, false)]
    [InlineData(-4320, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void UsesAnInclusiveRolling72HourWindow(int minutesFromNow, bool expected)
    {
        var notice = CreateNotice("Outage at Henry Hub.") with { PostedAt = Now.AddMinutes(minutesFromNow) };
        Assert.Equal(expected, SignalDetector.Match(notice, Now) is not null);
    }

    private static Notice CreateNotice(string body, string? location = null) =>
        new("Sabine Pipe Line", "Critical", "Service status", Now.AddHours(-3), location,
            new Uri("https://example.invalid/notice/1"), body);
}
