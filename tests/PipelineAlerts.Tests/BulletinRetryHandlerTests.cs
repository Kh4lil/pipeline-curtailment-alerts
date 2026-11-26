using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace PipelineAlerts.Tests;

public sealed class BulletinRetryHandlerTests
{
    private static readonly Uri Bulletin = new("https://www.gasnom.com/ip/SABINE/notices.cfm?type=1");

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task RetriesTransientBulletinStatusWithTheSameRequest(HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Bulletin);
        request.Headers.UserAgent.ParseAdd("PipelineCurtailmentAlerts/1.0");
        var calls = 0;
        using var client = CreateClient(requestAgain =>
        {
            Assert.Same(request, requestAgain);
            Assert.Equal("PipelineCurtailmentAlerts/1.0", requestAgain.Headers.UserAgent.ToString());
            return ++calls == 1 ? new HttpResponseMessage(status) : Ok();
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bulletin data", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReturnsSecondFailureWithoutAnotherRetry()
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });

        using var response = await client.GetAsync(Bulletin);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.NotFound)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable)]
    public async Task DoesNotRetryOtherFailuresOrMethods(string method, HttpStatusCode status)
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(status);
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), Bulletin);

        using var response = await client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetriesConnectionFailure()
    {
        var calls = 0;
        using var client = CreateClient(_ => ++calls == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset.")
            : Ok());

        Assert.Equal("Bulletin data", await client.GetStringAsync(Bulletin));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RetriesInterruptedResponseBody()
    {
        var calls = 0;
        using var client = CreateClient(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new InterruptedContent() }
            : Ok());

        Assert.Equal("Bulletin data", await client.GetStringAsync(Bulletin));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DoesNotRetryBeforeLongRetryAfter()
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync(Bulletin, cancellation.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationStopsRetry()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(_ =>
        {
            calls++;
            cancellation.Cancel();
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Bulletin, cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("Bulletin data") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new BulletinRetryHandler(new StubHandler(respond), TimeSpan.Zero));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class InterruptedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
