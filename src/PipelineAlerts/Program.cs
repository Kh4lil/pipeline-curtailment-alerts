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
Uri? webhook = demo ? new Uri("https://slack.example.invalid/demo") : null;
if (args.Length == 0)
{
    var configuredUrl = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL");
    if (!string.IsNullOrWhiteSpace(configuredUrl))
    {
        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out webhook)
            || webhook.Scheme != Uri.UriSchemeHttps
            || webhook.Host is not ("hooks.slack.com" or "hooks.slack-gov.com")
            || !webhook.IsDefaultPort || webhook.UserInfo.Length > 0
            || !webhook.AbsolutePath.StartsWith("/services/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("SLACK_WEBHOOK_URL must be a valid Slack Incoming Webhook URL.");
            return 2;
        }
    }
}

var now = DateTimeOffset.UtcNow;
using var bulletinClient = demo ? new HttpClient(new SampleBulletin(now)) : new HttpClient();
bulletinClient.Timeout = TimeSpan.FromSeconds(20);
bulletinClient.DefaultRequestHeaders.UserAgent.ParseAdd("PipelineCurtailmentAlerts/1.0");

Console.WriteLine($"Checking {SabineNoticeSource.PipelineName} ({(demo ? "sample replay" : "live bulletin")})...");
Console.WriteLine($"Delivery mode: {(demo ? "simulated Slack" : webhook is null ? "preview" : "Slack")}");

IReadOnlyList<Notice> notices;
try
{
    notices = await new SabineNoticeSource(bulletinClient).FetchAsync(now - SignalDetector.Lookback, now);
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

if (!demo)
{
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
        Console.WriteLine($"  Full notice  {notice.Link.AbsoluteUri}");
    }
}

if (webhook is null || signals.Length == 0)
    return 0;

using var slackClient = new HttpClient(demo ? new DemoSlackReceiver() : new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(10)
};
var notifier = new SlackNotifier(slackClient, webhook, isDemo: demo);
int sent = 0;

foreach (var signal in signals)
{
    try
    {
        await notifier.SendAsync(signal);
        sent++;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
}

Console.WriteLine();
Console.WriteLine(demo
    ? $"Simulated Slack delivery: {sent} of {signals.Length} alert(s) accepted."
    : $"Slack delivery: {sent} of {signals.Length} alert(s) sent.");
return sent == signals.Length ? 0 : 1;

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project src/PipelineAlerts -- [--demo | --dry-run]");
    Console.WriteLine("  --demo     Replay sample notices and show the outgoing Slack alert offline.");
    Console.WriteLine("  --dry-run  Read the live bulletin and preview alerts in the console.");
    Console.WriteLine("  Default    Read the live bulletin and send alerts if SLACK_WEBHOOK_URL is set.");
}
