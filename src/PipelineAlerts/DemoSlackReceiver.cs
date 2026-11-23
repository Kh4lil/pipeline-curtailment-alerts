using System.Net;
using System.Text.Json;

namespace PipelineAlerts;

internal sealed class DemoSlackReceiver : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var body = await request.Content!.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        Console.WriteLine();
        Console.WriteLine("Slack message captured by local receiver");
        Console.WriteLine(payload.RootElement.GetProperty("text").GetString());

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
    }
}
