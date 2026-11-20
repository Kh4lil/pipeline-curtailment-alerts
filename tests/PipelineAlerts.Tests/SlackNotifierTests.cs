using System.Net;
using System.Text.Json;
using Xunit;

namespace PipelineAlerts.Tests;

public class SlackNotifierTests
{
    private static readonly Uri Webhook = new("https://hooks.slack.com/services/example-webhook-value");
    private static readonly TradingSignal Signal = new(
        new Notice(
            "Example Pipeline", "Critical", "Force majeure & outage <!here>",
            new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.FromHours(-6)),
            "St. Mary Parish, Louisiana", new Uri("https://ebb.example.com/notice/42?id=42&view=full"), ""),
        "Force Majeure", "Louisiana", "25,000 Dth/d");

    [Fact]
    public void FormatsNoticeDetailsAndEscapesSlackMentions()
    {
        var message = SlackNotifier.FormatMessage(Signal);

        Assert.Contains("Force majeure &amp; outage &lt;!here&gt;", message);
        Assert.Contains("Example Pipeline", message);
        Assert.Contains("Critical", message);
        Assert.Contains("St. Mary Parish, Louisiana", message);
        Assert.Contains("25,000 Dth/d", message);
        Assert.Contains("<https://ebb.example.com/notice/42?id=42&amp;view=full|Open full notice>", message);
        Assert.DoesNotContain("Reported volume", SlackNotifier.FormatMessage(Signal with { Volume = null }));
    }

    [Fact]
    public async Task PostsJsonToConfiguredWebhook()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Webhook, request.RequestUri);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(SlackNotifier.FormatMessage(Signal), payload.RootElement.GetProperty("text").GetString());
            Assert.False(payload.RootElement.GetProperty("unfurl_links").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("unfurl_media").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await new SlackNotifier(client, Webhook).SendAsync(Signal);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectedNotificationReportsStatusWithoutWebhookOrResponseBody()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(Webhook.AbsoluteUri) })));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SlackNotifier(client, Webhook).SendAsync(Signal));

        Assert.Equal("Slack rejected the alert (HTTP 403).", error.Message);
        Assert.DoesNotContain(Webhook.AbsoluteUri, error.ToString());
    }

    [Fact]
    public async Task ConnectionFailureOmitsWebhookUrlFromException()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException(Webhook.AbsoluteUri)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SlackNotifier(client, Webhook).SendAsync(Signal));

        Assert.Equal("Could not connect to the Slack webhook.", error.Message);
        Assert.DoesNotContain(Webhook.AbsoluteUri, error.ToString());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            reply(request, cancellationToken);
    }
}
