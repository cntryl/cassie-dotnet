using Cntryl.Cassie.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class ReaderCancellationTests
    {
        [Fact]
        public async Task ShouldShareCancelRequestGivenConcurrentRequestsWhenCancellationIsRequested()
        {
            // Arrange
            TaskCompletionSource<bool> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeReaderSession session = new() { CancelHandler = () => response.Task };
            await using ReaderCancellation cancellation = new(session, commandTimeout: 0);

            // Act
            Task<bool> first = cancellation.RequestCancelAsync();
            Task<bool> second = cancellation.RequestCancelAsync();
            response.SetResult(true);
            bool[] results = await Task.WhenAll(first, second);

            // Assert
            Assert.Same(first, second);
            Assert.Equal(1, session.CancelRequests);
            Assert.All(results, Assert.True);
        }

        [Fact]
        public async Task ShouldFinishCurrentFrameGivenCallerCancellationWhenReadHasStarted()
        {
            // Arrange
            TaskCompletionSource<(byte Tag, byte[] Payload)> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeReaderSession session = new() { ReadHandler = _ => response.Task };
            await using ReaderCancellation cancellation = new(session, commandTimeout: 0);
            using CancellationTokenSource caller = new();

            // Act
            Task<(byte Tag, byte[] Payload)> reading = cancellation.ReadFrameAsync(caller.Token);
            await caller.CancelAsync();
            bool frameStillPending = !reading.IsCompleted;
            response.SetResult(((byte)'D', [1, 2, 3]));
            (byte tag, byte[] payload) = await reading;

            // Assert
            Assert.True(frameStillPending);
            Assert.Equal((byte)'D', tag);
            Assert.Equal(new byte[] { 1, 2, 3 }, payload);
            Assert.Equal(CancellationToken.None, Assert.Single(session.ReadTokens));
            Assert.Equal(1, session.CancelRequests);
            Assert.Equal(0, session.Aborts);
        }

        [Fact]
        public async Task ShouldPassCallerTokenGivenDrainAllowsAbortWhenFrameIsRead()
        {
            // Arrange
            FakeReaderSession session = new();
            session.Enqueue((byte)'Z', [(byte)'I']);
            await using ReaderCancellation cancellation = new(session, commandTimeout: 0);
            using CancellationTokenSource caller = new();

            // Act
            _ = await cancellation.ReadFrameAsync(caller.Token, allowAbortOnCancellation: true);

            // Assert
            Assert.Equal(caller.Token, Assert.Single(session.ReadTokens));
            Assert.Equal(0, session.CancelRequests);
        }

        [Fact]
        public async Task ShouldLeaveLaterOperationUncancelledGivenRetiredDeadlineWhenOriginalTimeoutElapses()
        {
            // Arrange
            FakeReaderSession session = new();
            ReaderCancellation retired = new(session, commandTimeout: 1);
            await retired.DisposeAsync();
            await using ReaderCancellation later = new(session, commandTimeout: 0);

            // Act
            bool laterCancel = await later.RequestCancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
            bool retiredCancel = await retired.RequestCancelAsync();

            // Assert
            Assert.True(laterCancel);
            Assert.True(retiredCancel);
            Assert.False(retired.TimedOut);
            Assert.Equal(1, session.CancelRequests);
            Assert.Equal(0, session.Aborts);
        }

        [Fact]
        public async Task ShouldIgnoreLateCancelFailureGivenRetiredTimeoutWhenSessionIsReused()
        {
            // Arrange
            TaskCompletionSource cancelStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Inline completion makes the old timeout observer finish before the final assertions.
            TaskCompletionSource<bool> cancelResponse = new();
            FakeReaderSession session = new()
            {
                CancelHandler = () =>
                {
                    cancelStarted.SetResult();
                    return cancelResponse.Task;
                }
            };
            ReaderCancellation retired = new(session, commandTimeout: 1);
            try
            {
                await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await retired.DisposeAsync();
                session.CancelHandler = null;
                await using ReaderCancellation later = new(session, commandTimeout: 0);

                // Act
                cancelResponse.SetResult(false);
                bool laterCancel = await later.RequestCancelAsync();

                // Assert
                Assert.True(retired.TimedOut);
                Assert.True(laterCancel);
                Assert.Equal(2, session.CancelRequests);
                Assert.Equal(0, session.Aborts);
            }
            finally
            {
                _ = cancelResponse.TrySetResult(true);
                await retired.DisposeAsync();
            }
        }
    }
}
