using System.Net;

namespace PipelineAlerts;

internal sealed class BulletinRetryHandler(HttpMessageHandler innerHandler, TimeSpan? retryDelay = null)
    : DelegatingHandler(innerHandler)
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan defaultDelay = TimeSpan.FromMilliseconds(
        Math.Clamp(retryDelay?.TotalMilliseconds ?? 250, 0, MaximumRetryDelay.TotalMilliseconds));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            return await base.SendAsync(request, cancellationToken);

        TimeSpan delay;
        try
        {
            var response = await SendAndBufferAsync(request, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
            {
                return response;
            }

            var retryAfter = response.Headers.RetryAfter;
            delay = retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow ?? defaultDelay;
            if (delay > MaximumRetryDelay)
                return response;

            response.Dispose();
        }
        catch (Exception ex) when (IsTransientTransportError(ex))
        {
            delay = defaultDelay;
        }

        await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
        return await SendAndBufferAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndBufferAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            // Bulletin callers already buffer these bodies. Read them here so an interrupted read can retry.
            if (response.IsSuccessStatusCode)
                await response.Content.LoadIntoBufferAsync(cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static bool IsTransientTransportError(Exception exception) =>
        (exception switch
        {
            HttpRequestException ex => ex.HttpRequestError,
            HttpIOException ex => ex.HttpRequestError,
            _ => HttpRequestError.Unknown
        }) is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded;
}
