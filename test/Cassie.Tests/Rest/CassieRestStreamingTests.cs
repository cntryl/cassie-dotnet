using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cntryl.Cassie.Rest;
using Cntryl.Cassie.Tests.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest;

public sealed class CassieRestStreamingTests
{
    private static readonly Uri ServiceAddress = new("https://cassie.example/");

    [Fact]
    public async Task ShouldStreamSuccessGivenNonUtf8JsonWhenResponseIsRead()
    {
        // Arrange
        using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ReadOnlyJson(Encoding.Unicode.GetBytes("""["café"]"""))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-16" } }
            }
        }));
        using HttpClient http = new(handler) { BaseAddress = ServiceAddress, MaxResponseContentBufferSize = 16 };
        using CassieRestClient client = new(http);

        // Act
        var result = await client.ListCollectionsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("café", Assert.Single(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectOversizedJsonGivenResponseLimitWhenContentIsRead(bool declaredLength)
    {
        // Arrange
        using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ReadOnlyJson("""["long collection name"]"""u8.ToArray())
            {
                Headers = { ContentLength = declaredLength ? 24 : null }
            }
        }));
        using HttpClient http = new(handler) { BaseAddress = ServiceAddress, MaxResponseContentBufferSize = 8 };
        using CassieRestClient client = new(http);

        // Act
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListCollectionsAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("MaxResponseContentBufferSize", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldPreserveStatusGivenOversizedErrorBodyWhenReadingErrorResponse()
    {
        // Arrange
        using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(
            ScriptedHttpHandler.Json( /*lang=json,strict*/ """{"error":"the request conflicts with existing data"}""", HttpStatusCode.Conflict)));
        using HttpClient http = new(handler) { BaseAddress = ServiceAddress, MaxResponseContentBufferSize = 12 };
        using CassieRestClient client = new(http);

        // Act
        var failure = await Assert.ThrowsAsync<CassieRestException>(() =>
            client.ListCollectionsAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal("Conflict", failure.Error);
    }

    [Fact]
    public async Task ShouldTimeOutGivenSlowHeadersAndBodyWhenCombinedDelayExceedsDeadline()
    {
        // Arrange
        using SlowJsonBody stream = new();
        using ScriptedHttpHandler handler = new(async (_, token) =>
        {
            await Task.Delay(300, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        });
        using HttpClient http = new(handler) { BaseAddress = ServiceAddress, Timeout = TimeSpan.FromMilliseconds(500) };
        using CassieRestClient client = new(http);

        // Act
        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.ListCollectionsAsync(TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.True(stream.ReadStarted);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task ShouldTimeOutGivenPendingBodyWhenRestClientClockAdvances()
    {
        // Arrange
        using var clock = new DeadlineClock();
        using var body = new PendingHttpBodyStream();
        using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using HttpClient http = new(handler) { BaseAddress = ServiceAddress, Timeout = TimeSpan.FromSeconds(120) };
        using CassieRestClient client = new(http, clock);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reading = client.ListCollectionsAsync(lifetime.Token);
        await body.Reading.WaitAsync(lifetime.Token);

        // Act
        clock.Advance(TimeSpan.FromSeconds(120));
        var failure = await Record.ExceptionAsync(() => reading);

        // Assert
        _ = Assert.IsType<TimeoutException>(Assert.IsType<TaskCanceledException>(failure).InnerException);
        Assert.True(body.IsDisposed);
    }

    private sealed class SlowJsonBody() : MemoryStream("[]"u8.ToArray(), false)
    {
        public bool ReadStarted { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!ReadStarted)
            {
                ReadStarted = true;
                await Task.Delay(350, cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ReadOnlyJson(byte[] data) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new MemoryStream(data, false));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new InvalidOperationException("The success body must be read through its stream.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
