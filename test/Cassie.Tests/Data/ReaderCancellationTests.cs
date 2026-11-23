using Cntryl.Cassie.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Data;

public sealed class ReaderCancellationTests
{
    [Fact]
    public async Task ShouldShareCancelRequestGivenConcurrentRequestsWhenCancellationIsRequested()
    {
        // Arrange
        TaskCompletionSource<bool> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeReaderSession session = new() { CancelHandler = () => response.Task };
        await using ReaderCancellation cancellation = new(session, 0);

        // Act
        var first = cancellation.RequestCancelAsync();
        var second = cancellation.RequestCancelAsync();
        response.SetResult(true);
        var results = await Task.WhenAll(first, second);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(1, session.CancelRequests);
        Assert.All(results, Assert.True);
    }

    [Fact]
    public async Task ShouldStartCancelRequestGivenTokenAlreadyCanceledWhenFrameIsAlreadyBuffered()
    {
        // Arrange
        FakeReaderSession session = new() { CancelHandler = static () => Task.FromResult(true) };
        session.Enqueue((byte)'D', [1]);
        await using ReaderCancellation cancellation = new(session, 0);
        using CancellationTokenSource caller = new();
        await caller.CancelAsync();

        // Act
        var (tag, _) = await cancellation.ReadFrameAsync(caller.Token);

        // Assert
        Assert.Equal((byte)'D', tag);
        Assert.Equal(1, session.CancelRequests);
    }

    [Fact]
    public async Task ShouldSkipCancelRequestGivenLiveTokenWhenFrameIsAlreadyBuffered()
    {
        // Arrange
        FakeReaderSession session = new() { CancelHandler = static () => Task.FromResult(true) };
        session.Enqueue((byte)'D', [1]);
        await using ReaderCancellation cancellation = new(session, 0);
        using CancellationTokenSource caller = new();

        // Act
        var reading = cancellation.ReadFrameAsync(caller.Token);
        var completedSynchronously = reading.IsCompletedSuccessfully;
        _ = await reading;

        // Assert
        Assert.True(completedSynchronously);
        Assert.Equal(0, session.CancelRequests);
    }

    [Fact]
    public async Task ShouldFinishCurrentFrameGivenCallerCancellationWhenReadHasStarted()
    {
        // Arrange
        TaskCompletionSource<(byte Tag, byte[] Payload)> response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeReaderSession session = new() { ReadHandler = _ => response.Task };
        await using ReaderCancellation cancellation = new(session, 0);
        using CancellationTokenSource caller = new();

        // Act
        var reading = cancellation.ReadFrameAsync(caller.Token).AsTask();
        await caller.CancelAsync();
        var frameStillPending = !reading.IsCompleted;
        response.SetResult(((byte)'D', [1, 2, 3]));
        var (tag, payload) = await reading;

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
        await using ReaderCancellation cancellation = new(session, 0);
        using CancellationTokenSource caller = new();

        // Act
        _ = await cancellation.ReadFrameAsync(caller.Token, true);

        // Assert
        Assert.Equal(caller.Token, Assert.Single(session.ReadTokens));
        Assert.Equal(0, session.CancelRequests);
    }

    [Fact]
    public async Task ShouldLeaveLaterOperationUncancelledGivenRetiredDeadlineWhenOriginalTimeoutElapses()
    {
        // Arrange
        FakeReaderSession session = new();
        ReaderCancellation retired = new(session, 1);
        await retired.DisposeAsync();
        await using ReaderCancellation later = new(session, 0);

        // Act
        var laterCancel = await later.RequestCancelAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
        var retiredCancel = await retired.RequestCancelAsync();

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
        ReaderCancellation retired = new(session, 1);

        try
        {
            await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await retired.DisposeAsync();
            session.CancelHandler = null;
            await using ReaderCancellation later = new(session, 0);

            // Act
            cancelResponse.SetResult(false);
            var laterCancel = await later.RequestCancelAsync();

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
