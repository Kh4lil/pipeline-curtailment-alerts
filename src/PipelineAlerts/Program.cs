using System.Globalization;
using PipelineAlerts;

if (args is ["--help"] or ["-h"])
{
    PrintUsage();
    return 0;
}

if (args is not ([] or ["--demo"] or ["--demo-synthetic"] or ["--dry-run"] or ["--scheduled", _]))
{
    PrintUsage();
    return 2;
}

bool historical = args is ["--demo"];
bool synthetic = args is ["--demo-synthetic"];
bool demo = historical || synthetic;
string? deliveryLogPath = args is ["--scheduled", var path] ? path : null;
Uri? webhook = demo ? new Uri("https://slack.example.invalid/demo") : null;
if (args.Length == 0 || deliveryLogPath is not null)
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

if (deliveryLogPath is not null && (string.IsNullOrWhiteSpace(deliveryLogPath) || webhook is null))
{
    Console.Error.WriteLine("Scheduled runs require a state-file path and SLACK_WEBHOOK_URL.");
    return 2;
}

var bulletinUrl = SabineNoticeSource.DefaultBulletin;
var pipelineName = SabineNoticeSource.PipelineName;
if (!demo)
{
    var configuredUrl = Environment.GetEnvironmentVariable("PIPELINE_BULLETIN_URL")?.Trim();
    if (!string.IsNullOrEmpty(configuredUrl))
    {
        if (!Uri.TryCreate(configuredUrl.TrimEnd('/') + "/", UriKind.Absolute, out var configuredBulletin)
            || configuredBulletin.Scheme != Uri.UriSchemeHttps || configuredBulletin.Host.Length == 0
            || configuredBulletin.UserInfo.Length > 0 || configuredBulletin.Query.Length > 0
            || configuredBulletin.Fragment.Length > 0)
        {
            Console.Error.WriteLine("PIPELINE_BULLETIN_URL must be an HTTPS bulletin base URL without a query, fragment or credentials.");
            return 2;
        }
        bulletinUrl = configuredBulletin;
    }

    var configuredName = Environment.GetEnvironmentVariable("PIPELINE_NAME")?.Trim();
    if (!string.IsNullOrEmpty(configuredName))
        pipelineName = configuredName;
}

var now = historical ? SampleBulletin.HistoricalAsOf : DateTimeOffset.UtcNow;
using var bulletinClient = new HttpClient(demo
    ? new SampleBulletin(now, historical)
    : new BulletinRetryHandler(new HttpClientHandler()));
bulletinClient.Timeout = TimeSpan.FromSeconds(20);
bulletinClient.DefaultRequestHeaders.UserAgent.ParseAdd("PipelineCurtailmentAlerts/1.0");

var bulletinMode = historical ? $"historical replay as of {now.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}"
    : synthetic ? "synthetic replay" : "live bulletin";
Console.WriteLine($"Checking {pipelineName} ({bulletinMode})...");
Console.WriteLine($"Delivery mode: {(demo ? "simulated Slack" : webhook is null ? "preview" : "Slack")}");

IReadOnlyList<Notice> notices;
try
{
    notices = await new SabineNoticeSource(bulletinClient, bulletinUrl, pipelineName)
        .FetchAsync(now - SignalDetector.Lookback, now);
}
catch (HttpRequestException ex)
{
    var status = ex.StatusCode is { } code ? $" (HTTP {(int)code})" : "";
    Console.Error.WriteLine($"Could not fetch the bulletin{status}.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The bulletin request timed out.");
    return 1;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"Could not read the bulletin. {ex.Message}");
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

ScheduledDeliveryLog? deliveryLog = null;
if (deliveryLogPath is not null)
{
    try
    {
        deliveryLog = new ScheduledDeliveryLog(deliveryLogPath);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine("Could not open the scheduled delivery log. Check its path, permissions, and other running checks.");
        return 1;
    }
}

using var scheduledDeliveryLog = deliveryLog;
using var slackClient = new HttpClient(demo ? new DemoSlackReceiver() : new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(10)
};
var notifier = new SlackNotifier(slackClient, webhook, isDemo: synthetic);
int sent = 0;
int skipped = 0;

foreach (var signal in signals)
{
    if (scheduledDeliveryLog?.IsDelivered(signal.Notice) == true)
    {
        skipped++;
        continue;
    }

    try
    {
        var alert = historical
            ? signal with { Notice = signal.Notice with { Title = $"HISTORICAL REPLAY · {signal.Notice.Title}" } }
            : signal;
        await notifier.SendAsync(alert);
        sent++;
        scheduledDeliveryLog?.RecordDelivered(signal.Notice);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Slack accepted an alert, but its delivery could not be saved. A later scheduled check may repeat it.");
        return 1;
    }
}

Console.WriteLine();
if (scheduledDeliveryLog is not null)
    Console.WriteLine($"Skipped {skipped} already delivered alert(s).");
Console.WriteLine(demo
    ? $"Simulated Slack delivery: {sent} of {signals.Length} alert(s) accepted."
    : $"Slack delivery: {sent} of {signals.Length - skipped} alert(s) sent.");
return sent + skipped == signals.Length ? 0 : 1;

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project src/PipelineAlerts -- [--demo | --demo-synthetic | --dry-run | --scheduled <state-file>]");
    Console.WriteLine("  --demo            Replay historical Sabine notice 440 offline.");
    Console.WriteLine("  --demo-synthetic  Replay synthetic notices offline.");
    Console.WriteLine("  --dry-run         Read the live bulletin and preview alerts in the console.");
    Console.WriteLine("  --scheduled       Read the live bulletin and remember delivered alerts in a state file.");
    Console.WriteLine("  Default           Read the live bulletin and send alerts if SLACK_WEBHOOK_URL is set.");
}
