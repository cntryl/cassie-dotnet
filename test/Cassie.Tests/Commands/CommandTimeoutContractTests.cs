using System.Data;
using System.Diagnostics.CodeAnalysis;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Protocol;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands;

public sealed class SyncCommandTimeoutContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Security", "CA2100", Justification = "The SQL is generated from fixed test data.")]
    public async Task ShouldDiscardSessionGivenStalledPeerWhenDeadlineInterruptsRequestWrite(bool synchronous)
    {
        // Arrange
        TaskCompletionSource releasePeer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(_ => releasePeer.Task);
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 /*" + new string('x', 8 * 1024 * 1024) + "*/";
        command.CommandTimeout = 1;

        try
        {
            // Act
            var execution = synchronous
                ? Task.Run(command.ExecuteScalar, TestContext.Current.CancellationToken)
                : command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            _ = await Assert.ThrowsAsync<TimeoutException>(
                () => execution.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken));
            // Assert
            Assert.True(execution.IsCompleted);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.CancelRequests);
        }
        finally
        {
            releasePeer.SetResult();
        }
    }

    [Fact]
    public void ShouldThrowTimeoutExceptionGivenSilentServerWhenSyncCommandTimesOut()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        connection.Open();
        using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1", CommandTimeout = 1 };

        // Act
        var exception = Assert.Throws<TimeoutException>(command.ExecuteScalar);

        // Assert
        Assert.Contains("CommandTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}

public sealed class AsyncCommandTimeoutContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ShouldShareBudgetGivenAutomaticPrepareAndPagingWhenCommandExecutes(int timeout)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        ScriptedServer? peer = null;
        using ScriptedServer server = new(async session =>
        {
            Assert.Equal(FrontendMessage.Parse, (await session.ReadFrameAsync()).Tag);
            Assert.Equal(FrontendMessage.Describe, (await session.ReadFrameAsync()).Tag);
            Assert.Equal(FrontendMessage.Sync, (await session.ReadFrameAsync()).Tag);
            await Task.Delay(400, token);
            await session.WriteAsync((byte)'1', []);
            await session.WriteAsync((byte)'n', []);
            await session.WriteReadyAsync();

            foreach (var tag in "BDEH")
            {
                Assert.Equal((byte)tag, (await session.ReadFrameAsync()).Tag);
            }

            await session.WriteIntRowDescriptionAsync();
            await session.WriteTextRowAsync("1");
            await session.WriteAsync((byte)'s', []);
            Assert.Equal((byte)'E', (await session.ReadFrameAsync()).Tag);
            Assert.Equal((byte)'H', (await session.ReadFrameAsync()).Tag);
            await Task.Delay(750, token);

            if (peer!.CancelRequests == 0)
            {
                await session.WriteTextRowAsync("2");
                await session.WriteCommandCompleteAsync("SELECT 2");
            }
            else
            {
                await BatchPeer.ReplyWithErrorAsync(session, "57014", "query canceled");
            }


            Assert.Equal(FrontendMessage.Sync, (await session.ReadFrameAsync()).Tag);
            await session.WriteReadyAsync();
            Assert.Equal(FrontendMessage.Close, (await session.ReadFrameAsync()).Tag);
            Assert.Equal(FrontendMessage.Sync, (await session.ReadFrameAsync()).Tag);
            await session.WriteAsync(BackendMessage.CloseComplete, []);
            await session.WriteReadyAsync();
            await session.ServeQueriesAsync(session.WriteCommandCompleteAsync);
        });
        peer = server;
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false;Fetch Size=1") };
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = timeout;
        await command.PrepareAsync(token);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));

        // Act
        var secondPage = await Record.ExceptionAsync(async () => _ = await reader.ReadAsync(token));

        // Assert
        if (timeout == 0)
        {
            Assert.Null(secondPage);
            Assert.Equal(2, reader.GetInt32(0));
        }
        else
        {
            _ = Assert.IsType<TimeoutException>(secondPage);
        }

        await reader.CloseAsync();
        await command.DisposeAsync();
        using var followup = connection.CreateCommand();
        followup.CommandText = "SELECT 3";
        _ = await followup.ExecuteNonQueryAsync(token);
        Assert.Equal(timeout, server.CancelRequests);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ShouldThrowTimeoutExceptionGivenSilentServerWhenAsyncCommandHasNoToken()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT 1", CommandTimeout = 1 };

        // Act
        var exception =
            await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteScalarAsync(CancellationToken.None));

        // Assert
        Assert.Contains("CommandTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}

public sealed class TokenCommandTimeoutContractTests
{
    [Fact]
    public async Task ShouldThrowTimeoutExceptionGivenSilentServerWhenAsyncCommandHasLiveToken()
    {
        // Arrange
        using ScriptedServer server = new(session => session.StayVerySilentAsync());
        await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using CassieCommand command = new()
        { Connection = connection, CommandText = "SELECT 1", CommandTimeout = 1 };
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteScalarAsync(source.Token));

        // Assert
        Assert.Contains("CommandTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
