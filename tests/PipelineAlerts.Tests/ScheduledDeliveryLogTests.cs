using Xunit;

namespace PipelineAlerts.Tests;

public sealed class ScheduledDeliveryLogTests : IDisposable
{
    private static readonly Notice Original = new(
        "Sabine Pipe Line",
        "Critical",
        "Compressor outage",
        new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero),
        "Henry Hub",
        new Uri("https://ebb.example.com/notices?id=440"),
        "Deliveries curtailed by 350,000 Dth/day.");

    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("pipeline-delivery-log-");
    private string LogPath => Path.Combine(directory.FullName, "state", "delivered.log");

    [Fact]
    public void PersistsDeliveryAcrossRunsAndRecordsEachRevisionOnce()
    {
        using (var log = new ScheduledDeliveryLog(LogPath))
        {
            Assert.False(log.IsDelivered(Original));
            log.RecordDelivered(Original);
            Assert.True(log.IsDelivered(Original));
        }

        using (var log = new ScheduledDeliveryLog(LogPath))
        {
            Assert.True(log.IsDelivered(Original));
            log.RecordDelivered(Original);
        }

        Assert.Single(File.ReadAllLines(LogPath));
    }

    [Fact]
    public void DistinguishesNewNoticesAndSubstantiveRevisions()
    {
        using var log = new ScheduledDeliveryLog(LogPath);
        log.RecordDelivered(Original);

        Assert.False(log.IsDelivered(Original with { Link = new Uri("https://ebb.example.com/notices?id=441") }));
        Assert.False(log.IsDelivered(Original with { PostedAt = Original.PostedAt.AddMinutes(5) }));
        Assert.False(log.IsDelivered(Original with { Body = "Deliveries curtailed by 450,000 Dth/day." }));
        Assert.True(log.IsDelivered(Original with { Body = "Deliveries curtailed\nby 350,000 Dth/day." }));
    }

    [Fact]
    public void RecoversFromAPartiallyWrittenRecord()
    {
        var revised = Original with { Title = "Revised compressor outage" };
        using (var log = new ScheduledDeliveryLog(LogPath))
            log.RecordDelivered(Original);
        File.AppendAllText(LogPath, "AF23");

        using (var log = new ScheduledDeliveryLog(LogPath))
        {
            Assert.True(log.IsDelivered(Original));
            Assert.False(log.IsDelivered(revised));
            log.RecordDelivered(revised);
        }

        using var reopened = new ScheduledDeliveryLog(LogPath);
        Assert.True(reopened.IsDelivered(Original));
        Assert.True(reopened.IsDelivered(revised));
    }

    [Theory]
    [InlineData("Unrelated-file heading\nAF23")]
    [InlineData("Unrelated file with no final newline")]
    [InlineData("\nAF23")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void LeavesInvalidStateFilesUnchanged(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, contents);

        Assert.Throws<InvalidDataException>(() =>
        {
            using var invalid = new ScheduledDeliveryLog(LogPath);
        });
        Assert.Equal(contents, File.ReadAllText(LogPath));
    }

    [Fact]
    public void HoldsExclusiveAccessUntilDisposed()
    {
        using var log = new ScheduledDeliveryLog(LogPath);
        Assert.Throws<IOException>(() =>
        {
            using var overlapping = new ScheduledDeliveryLog(LogPath);
        });
    }

    public void Dispose() => directory.Delete(recursive: true);
}
