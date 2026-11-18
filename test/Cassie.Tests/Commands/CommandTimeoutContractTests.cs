using System.Data;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands;

public sealed class SyncCommandTimeoutContractTests
{
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
