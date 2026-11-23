using System.Globalization;
using System.Net.Http.Json;

namespace PipelineAlerts;

internal sealed class SlackNotifier(HttpClient httpClient, Uri webhookUrl, bool isDemo = false)
{
    internal async Task SendAsync(TradingSignal signal, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            text = FormatMessage(signal, isDemo),
            unfurl_links = false,
            unfurl_media = false
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(webhookUrl, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Slack rejected the alert (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not connect to the Slack webhook.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The Slack webhook request timed out.");
        }
    }

    internal static string FormatMessage(TradingSignal signal, bool isDemo = false)
    {
        var notice = signal.Notice;
        var posted = notice.PostedAt.ToString("MMM d, yyyy HH:mm zzz", CultureInfo.InvariantCulture);
        var link = isDemo ? new Uri(notice.Link, ".") : notice.Link;
        var source = Escape(link.AbsoluteUri.Replace("|", "%7C"));
        var linkLabel = isDemo ? "Open Sabine bulletin" : "Open full notice";
        List<string> lines =
        [
            isDemo ? "*DEMO ONLY · Synthetic pipeline curtailment alert*" : "*Pipeline curtailment alert*",
            Escape(notice.Title),
            $"*Pipeline* {Escape(notice.Pipeline)} · *Type* {Escape(notice.Type)}",
            $"*Match* {Escape(signal.Keyword)} · {Escape(signal.Area)}",
            $"*Posted* {posted}"
        ];
        if (!string.IsNullOrWhiteSpace(notice.Location))
        {
            lines.Add($"*Location* {Escape(notice.Location)}");
        }
        if (!string.IsNullOrWhiteSpace(signal.Volume))
        {
            lines.Add($"*Reported volume* {Escape(signal.Volume)}");
        }
        lines.Add($"<{source}|{linkLabel}>");
        return string.Join('\n', lines);
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
