using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class CopyRecoveryContractTests
{
    [Fact]
    public async Task ShouldRejectInvalidByteCountGivenInputStreamWhenCopyFrameIsBuilt()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using ScriptedServer server = new(async session =>
        {
            _ = await session.ReadFrameAsync();
            await session.WriteAsync((byte)'G', [0, 0, 0]);
            Assert.Equal(FrontendMessage.CopyFail, (await session.ReadFrameAsync()).Tag);
            await Commands.BatchPeer.ReplyWithErrorAsync(session, "57014", "COPY input failed");
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "COPY ledger FROM STDIN";
        using InvalidCountStream input = new();

        // Act
        var failure = await Assert.ThrowsAsync<IOException>(() => command.CopyFromAsync(input, token));

        // Assert
        Assert.Contains("invalid byte count", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
        command.CommandText = "SELECT 1";
        _ = await command.ExecuteNonQueryAsync(token);
    }

    [Fact]
    public async Task ShouldDiscardSessionGivenPartialCopyFrameWhenWriteDeadlineExpires()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        TaskCompletionSource<byte> dataStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            _ = await session.ReadFrameAsync();
            await session.WriteAsync((byte)'G', [0, 0, 0]);
            var prefix = new byte[6];
            await session.Stream.ReadExactlyAsync(prefix, token);
            dataStarted.SetResult(prefix[0]);
            await release.Task;
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "COPY ledger FROM STDIN";
        command.CommandTimeout = 1;
        using MemoryStream source = new(new byte[16 * 1024 * 1024], false);

        try
        {
            // Act
            _ = await Assert.ThrowsAsync<TimeoutException>(() => command.CopyFromAsync(source, token));

            // Assert
            Assert.Equal((byte)'d', await dataStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), token));
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.CancelRequests);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task ShouldBoundRecoveryGivenSilentCopyFailPeerWhenInputStreamFails()
    {
        // Arrange
        TaskCompletionSource copyFailReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            _ = await session.ReadFrameAsync();
            await session.WriteAsync((byte)'G', [0, 0, 0]);
            var (tag, _) = await session.ReadFrameAsync();
            Assert.Equal((byte)'f', tag);
            copyFailReceived.SetResult();
            await session.StayVerySilentAsync();
        });
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        IOException original = new("input failed");

        async Task<int> CopyAsync()
        {
            using FailedInputStream source = new(original);
            return await connection.ExecuteCopyFromAsync("COPY ledger FROM STDIN", source, 0,
                TestContext.Current.CancellationToken);
        }

        var copying = CopyAsync();
        Exception? failure;

        try
        {
            await copyFailReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            failure = await Record.ExceptionAsync(async () =>
                _ = await copying.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (!copying.IsCompleted)
            {
                connection.AbortTransport();
            }

            try
            {
                _ = await copying;
            }
            catch (IOException)
            {
                // The original input failure is asserted after cleanup.
            }
        }

        // Assert

        Assert.Same(original, failure);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("input-failure")]
    [InlineData("input-cancellation")]
    [InlineData("output-cancellation")]
    [InlineData("output-timeout")]
    [InlineData("output-timeout-then-cancellation")]
    [InlineData("start-error")]
    [InlineData("output-error")]
    [InlineData("completion-error")]
    [InlineData("wrong-direction")]
    [InlineData("reject-input")]
    public async Task ShouldPreserveOriginalFailureGivenSilentRecoveryPeerWhenSingleDeadlineExpires(string scenario)
    {
        // Arrange
        TaskCompletionSource queryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource recoveryMessageReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource inputReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outputWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionNumber = 0;
        using ScriptedServer server = new(async session =>
        {
            if (Interlocked.Increment(ref sessionNumber) > 1)
            {
                await session.ServeQueriesAsync(_ => session.WriteCommandCompleteAsync("UPDATE 1"));
                return;
            }

            _ = await session.ReadFrameAsync();

            switch (scenario)
            {
                case "start-error":
                    await session.WriteAsync((byte)'E', ServerError("23505"));
                    break;
                case "output-error":
                    await session.WriteAsync((byte)'H', [0, 0, 0]);
                    await session.WriteAsync((byte)'E', ServerError("23505"));
                    break;
                case "completion-error":
                    await session.WriteAsync((byte)'G', [0, 0, 0]);
                    var (done, _) = await session.ReadFrameAsync();
                    Assert.Equal((byte)'c', done);
                    await session.WriteAsync((byte)'E', ServerError("23505"));
                    break;
                case "output-cancellation":
                case "output-timeout":
                case "output-timeout-then-cancellation":
                    await session.WriteAsync((byte)'H', [0, 0, 0]);
                    await session.WriteAsync((byte)'d', [1]);
                    break;
                default:
                    await session.WriteAsync((byte)'G', [0, 0, 0]);
                    break;
            }


            queryStarted.SetResult();

            if (scenario is "input-failure" or "input-cancellation" or "wrong-direction" or "reject-input")
            {
                var (failed, _) = await session.ReadFrameAsync();
                Assert.Equal((byte)'f', failed);
                recoveryMessageReceived.SetResult();
            }


            await session.StayVerySilentAsync();
        });
        using DeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        var connectionString = server.ConnectionString("Pooling=true;Maximum Pool Size=1;Command Timeout=0;Timeout=0");
        await using CassieConnection connection = new(clock)
        {
            ConnectionString = connectionString
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        IOException inputFailure = new("input failed");

        async Task<int> CopyAsync()
        {
            if (scenario == "reject-input")
            {
                await using var reader = await connection.ExecuteQueryAsync("COPY ledger FROM STDIN", caller.Token);
                return 0;
            }

            if (scenario is "input-failure" or "input-cancellation" or "completion-error")
            {
                using var source = scenario switch
                {
                    "input-failure" => new FailedInputStream(inputFailure),
                    "input-cancellation" => new PendingInputStream(inputReadStarted),
                    _ => new MemoryStream()
                };
                return await connection.ExecuteCopyFromAsync("COPY ledger FROM STDIN", source, 0, caller.Token);
            }

            using var destination =
                scenario is "output-cancellation" or "output-timeout" or "output-timeout-then-cancellation"
                    ? new PendingOutputStream(outputWriteStarted)
                    : new MemoryStream();
            return await connection.ExecuteCopyToAsync("COPY ledger TO STDOUT", destination,
                scenario is "output-timeout" or "output-timeout-then-cancellation" ? 1 : 0, caller.Token);
        }

        var copying = CopyAsync();
        Exception? failure;

        try
        {
            await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            if (scenario == "input-cancellation")
            {
                await inputReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }


            if (scenario is "output-cancellation" or "output-timeout" or "output-timeout-then-cancellation")
            {
                await outputWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }

            if (scenario is "input-cancellation" or "output-cancellation")
            {
                await caller.CancelAsync();
            }

            if (scenario is "output-timeout" or "output-timeout-then-cancellation")
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            if (scenario == "output-timeout-then-cancellation")
            {
                await caller.CancelAsync();
            }


            if (scenario is "input-failure" or "input-cancellation" or "wrong-direction" or "reject-input")
            {
                await recoveryMessageReceived.Task.WaitAsync(TimeSpan.FromSeconds(3),
                    TestContext.Current.CancellationToken);
            }
            else if (scenario is "output-cancellation" or "output-timeout" or "output-timeout-then-cancellation")
            {
                Assert.True(ScriptedServer.WaitUntil(() => server.CancelRequests == 1));
            }

            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(copying.IsCompleted);

            // Act
            clock.Advance(TimeSpan.FromSeconds(1));
            failure = await Record.ExceptionAsync(async () =>
                _ = await copying.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (!copying.IsCompleted)
            {
                connection.AbortTransport();
            }

            try
            {
                _ = await copying;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException
                                              or CassieException or InvalidOperationException or NotSupportedException)
            {
                // The original visible failure is asserted below after observing the operation.
            }
        }

        // Assert

        AssertOriginalFailure(scenario, failure, inputFailure, caller.Token);
        Assert.Equal(
            scenario is "output-timeout" or "output-timeout-then-cancellation"
                ? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)]
                : [TimeSpan.FromSeconds(5)], clock.InitialDueTimes);
        Assert.Equal(ConnectionState.Closed, connection.State);
        await using CassieConnection replacement = new(clock) { ConnectionString = connectionString };
        await replacement.OpenAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Accepted);
        CassieConnection.ClearPool(replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReuseSameSessionGivenRecoveryReachesReadyWhenCopyFails(bool cancelOutput)
    {
        // Arrange
        TaskCompletionSource queryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource sendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outputWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            _ = await session.ReadFrameAsync();
            await session.WriteAsync(cancelOutput ? (byte)'H' : (byte)'G', [0, 0, 0]);

            if (cancelOutput)
            {
                await session.WriteAsync((byte)'d', [1]);
            }


            queryStarted.SetResult();

            if (!cancelOutput)
            {
                var (failed, _) = await session.ReadFrameAsync();
                Assert.Equal((byte)'f', failed);
            }


            await sendReady.Task;
            await session.WriteAsync((byte)'E', ServerError("57014"));
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(_ => session.WriteCommandCompleteAsync("UPDATE 1"));
        });
        using DeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        await using CassieConnection connection = new(clock)
        {
            ConnectionString = server.ConnectionString("Pooling=false;Timeout=0;Command Timeout=0")
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var identity = connection.SessionIdentity;
        IOException original = new("input failed");

        async Task<int> CopyAsync()
        {
            using MemoryStream stream =
                cancelOutput ? new PendingOutputStream(outputWriteStarted) : new FailedInputStream(original);
            return cancelOutput
                ? await connection.ExecuteCopyToAsync("COPY ledger TO STDOUT", stream, 0, caller.Token)
                : await connection.ExecuteCopyFromAsync("COPY ledger FROM STDIN", stream, 0, caller.Token);
        }

        var copying = CopyAsync();
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        if (cancelOutput)
        {
            await outputWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await caller.CancelAsync();
        }


        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        if (cancelOutput)
        {
            Assert.True(ScriptedServer.WaitUntil(() => server.CancelRequests == 1));
        }

        // Act

        sendReady.SetResult();
        var failure = await Record.ExceptionAsync(async () =>
            _ = await copying.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        await using var next = connection.CreateCommand();
        next.CommandText = "UPDATE ledger SET value = 'ok'";
        var affected = await next.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        AssertOriginalFailure(cancelOutput ? "output-cancellation" : "input-failure", failure, original, caller.Token);
        Assert.Equal(1, affected);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(identity, connection.SessionIdentity);
        Assert.Equal(1, server.Accepted);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(clock.InitialDueTimes));
    }

    [Fact]
    public async Task ShouldKeepRemainingDrainBudgetGivenSlowCancelNegotiationWhenRecoveryDeadlineExpires()
    {
        // Arrange
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outputWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelNegotiationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource continueCancelNegotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ServeAsync()
        {
            using var regular = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = regular.GetStream();
            await stream.ReadExactlyAsync(new byte[8], TestContext.Current.CancellationToken);
            await stream.WriteAsync("N"u8.ToArray(), TestContext.Current.CancellationToken);
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadInt32BigEndian(header) - 4],
                TestContext.Current.CancellationToken);
            await PgFraming.WriteFrontendFrameAsync(stream, (byte)'R', [0, 0, 0, 0],
                TestContext.Current.CancellationToken);
            await PgFraming.WriteFrontendFrameAsync(stream, (byte)'K', [0, 0, 0, 42, 0, 0, 0, 7],
                TestContext.Current.CancellationToken);
            await PgFraming.WriteFrontendFrameAsync(stream, (byte)'Z', "I"u8.ToArray(),
                TestContext.Current.CancellationToken);
            _ = await PgFraming.ReadBackendFrameAsync(stream, TestContext.Current.CancellationToken);
            await PgFraming.WriteFrontendFrameAsync(stream, (byte)'H', [0, 0, 0],
                TestContext.Current.CancellationToken);
            await PgFraming.WriteFrontendFrameAsync(stream, (byte)'d', [1], TestContext.Current.CancellationToken);
            copyStarted.SetResult();

            using var cancel = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var cancelStream = cancel.GetStream();
            await cancelStream.ReadExactlyAsync(new byte[8], TestContext.Current.CancellationToken);
            cancelNegotiationStarted.SetResult();
            await continueCancelNegotiation.Task;
            await cancelStream.WriteAsync("N"u8.ToArray(), TestContext.Current.CancellationToken);
            await cancelStream.ReadExactlyAsync(new byte[16], TestContext.Current.CancellationToken);
            cancelReceived.SetResult();
            _ = await stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken);
        }

        var peer = ServeAsync();
        using DeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        await using CassieConnection connection = new(clock)
        {
            ConnectionString = $"Host=127.0.0.1;Port={port};Username=app;SSL Mode=Prefer;Timeout=0;Pooling=false"
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        async Task<int> CopyAsync()
        {
            using PendingOutputStream destination = new(outputWriteStarted);
            return await connection.ExecuteCopyToAsync("COPY ledger TO STDOUT", destination, 0, caller.Token);
        }

        var copying = CopyAsync();
        Exception? failure;

        try
        {
            await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await outputWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await caller.CancelAsync();
            await cancelNegotiationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(copying.IsCompleted);
            continueCancelNegotiation.SetResult();
            await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(copying.IsCompleted);

            // Act
            clock.Advance(TimeSpan.FromSeconds(1));
            failure = await Record.ExceptionAsync(async () =>
                _ = await copying.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            _ = continueCancelNegotiation.TrySetResult();

            if (!copying.IsCompleted)
            {
                connection.AbortTransport();
            }


            try
            {
                _ = await copying;
            }
            catch (OperationCanceledException)
            {
                // Preserve the original caller cancellation for the assertion below.
            }

            await peer.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        // Assert

        Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(failure).CancellationToken);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(clock.InitialDueTimes));
    }

    private static void AssertOriginalFailure(
        string scenario,
        Exception? failure,
        IOException inputFailure,
        CancellationToken caller
    )
    {
        switch (scenario)
        {
            case "input-failure":
                Assert.Same(inputFailure, failure);
                break;
            case "input-cancellation":
            case "output-cancellation":
                Assert.Equal(caller, Assert.IsType<OperationCanceledException>(failure).CancellationToken);
                break;
            case "output-timeout":
            case "output-timeout-then-cancellation":
                Assert.Contains("exceeded 1 seconds", Assert.IsType<TimeoutException>(failure).Message,
                    StringComparison.Ordinal);
                break;
            case "wrong-direction":
                _ = Assert.IsType<InvalidOperationException>(failure);
                break;
            case "reject-input":
                _ = Assert.IsType<NotSupportedException>(failure);
                break;
            default:
                Assert.Equal("23505", Assert.IsType<CassieException>(failure).SqlState);
                break;
        }
    }

    private static byte[] ServerError(string sqlState)
    {
        return Encoding.UTF8.GetBytes($"SERROR\0C{sqlState}\0Mcopy failed\0\0");
    }

    private sealed class InvalidCountStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(buffer.Length + 1);
        }
    }

    private sealed class PendingOutputStream(TaskCompletionSource writeStarted) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = writeStarted.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class PendingInputStream(TaskCompletionSource readStarted) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = readStarted.TrySetResult();
            return new ValueTask<int>(ReadPendingAsync(cancellationToken));
        }

        private static async Task<int> ReadPendingAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailedInputStream(IOException failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(failure);
        }
    }
}
