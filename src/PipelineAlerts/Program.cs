using System.Globalization;
using PipelineAlerts;

if (args is ["--help"] or ["-h"])
{
    PrintUsage();
    return 0;
}

if (args is not ([] or ["--demo"] or ["--dry-run"]))
{
    PrintUsage();
    return 2;
}

bool demo = args is ["--demo"];
var now = DateTimeOffset.UtcNow;
using var bulletinClient = demo ? new HttpClient(new SampleBulletin(now)) : new HttpClient();
bulletinClient.Timeout = TimeSpan.FromSeconds(20);
bulletinClient.DefaultRequestHeaders.UserAgent.ParseAdd("PipelineCurtailmentAlerts/1.0");

Console.WriteLine($"Checking {SabineNoticeSource.PipelineName} ({(demo ? "sample replay" : "live bulletin")})...");
Console.WriteLine("Delivery mode: preview");

IReadOnlyList<Notice> notices;
try
{
    notices = await new SabineNoticeSource(bulletinClient).FetchAsync(now - SignalDetector.Lookback);
}
catch (HttpRequestException ex)
{
    var status = ex.StatusCode is { } code ? $" (HTTP {(int)code})" : "";
    Console.Error.WriteLine($"Could not fetch the Sabine bulletin{status}.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The Sabine bulletin request timed out.");
    return 1;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"Could not read the Sabine bulletin. {ex.Message}");
    return 1;
}

var signals = notices.Select(notice => SignalDetector.Match(notice, now)).OfType<TradingSignal>().ToArray();
Console.WriteLine($"Found {notices.Count} recent notice(s).");
Console.WriteLine($"Detected {signals.Length} trading signal(s).");

foreach (var signal in signals)
{
    var notice = signal.Notice;
    var posted = notice.PostedAt.ToString("MMM d, yyyy HH:mm zzz", CultureInfo.InvariantCulture);
    Console.WriteLine();
    Console.WriteLine($"  {notice.Type}  |  {notice.Title}");
    Console.WriteLine($"  {notice.Pipeline}  |  Posted {posted}");
    Console.WriteLine($"  Rule match   {signal.Keyword} + {signal.Area}");
    if (notice.Location is { } location)
        Console.WriteLine($"  Location     {location}");
    if (signal.Volume is { } volume)
        Console.WriteLine($"  Volume       {volume}");
    Console.WriteLine($"  {(demo ? "Sample link" : "Full notice")}  {notice.Link.AbsoluteUri}");
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project src/PipelineAlerts -- [--demo | --dry-run]");
    Console.WriteLine("  --demo     Replay sample notices offline.");
    Console.WriteLine("  --dry-run  Read the live bulletin and preview alerts in the console.");
}
