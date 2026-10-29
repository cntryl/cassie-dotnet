using Cntryl.Cassie.Rest;
using System.Net;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest
{
    public sealed class CassieRestCancellationTests
    {
        [Fact]
        public async Task ShouldCancelBeforeHeadersGivenUnresponsivePeerWhenCallerCancels()
        {
            // Arrange
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default;
            using ScriptedHttpHandler handler = new(async (_, cancellationToken) =>
            {
                observed = cancellationToken;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ScriptedHttpHandler.Json("[]");
            });
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);
            using CancellationTokenSource caller = new();
            Task<IReadOnlyList<string>> reading = client.ListCollectionsAsync(caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            await caller.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await reading.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.True(observed.IsCancellationRequested);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldCancelResponseBodyGivenHeadersAlreadyReceivedWhenCallerCancels()
        {
            // Arrange
            using PendingHttpBodyStream body = new();
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            }));
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);
            using CancellationTokenSource caller = new();
            Task<IReadOnlyList<string>> reading = client.ListCollectionsAsync(caller.Token);
            await body.Reading.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            await caller.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await reading.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            // Assert
            Assert.True(body.IsDisposed);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldAvoidRequestGivenAlreadyCancelledCallerWhenCollectionsAreRequested()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(ScriptedHttpHandler.Json("[]")));
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);
            using CancellationTokenSource caller = new();
            await caller.CancelAsync();

            // Act
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await client.ListCollectionsAsync(caller.Token));

            // Assert
            Assert.Equal(0, handler.Requests);
        }
    }
}
