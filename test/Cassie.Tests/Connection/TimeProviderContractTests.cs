using System.Data;
using System.Net;
using System.Net.Sockets;
using Cntryl.Cassie.Connection;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class TimeProviderContractTests
{
    [Fact]
    public async Task ShouldTimeOutGivenSilentStartupWhenDataSourceClockAdvances()
    {
        // Arrange
        using var clock = new DeadlineClock();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var source = new CassieDataSource(
            $"Host=127.0.0.1;Port={port};Username=app;SSL Mode=Disable;Timeout=120;Pooling=false", clock);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opening = source.OpenConnectionAsync(lifetime.Token).AsTask();
        using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);

        // Act
        clock.Advance(TimeSpan.FromSeconds(120));
        var failure = await Record.ExceptionAsync(() => opening);

        // Assert
        _ = Assert.IsType<TimeoutException>(failure);
        Assert.Equal([TimeSpan.FromSeconds(120)], clock.InitialDueTimes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveCancellationCauseGivenSilentCommandWhenConnectionClockAdvances(bool cancelCaller)
    {
        // Arrange
        using var clock = new DeadlineClock();
        TaskCompletionSource dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedServer server = new(async session =>
        {
            _ = await session.ReadFrameAsync();
            dispatched.SetResult();
            await session.StayVerySilentAsync();
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using CassieConnection connection = new(clock)
        {
            ConnectionString = server.ConnectionString("Pooling=false;Timeout=0;Command Timeout=120")
        };
        await connection.OpenAsync(lifetime.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var executing = command.ExecuteScalarAsync(caller.Token);
        await dispatched.Task.WaitAsync(lifetime.Token);

        // Act
        clock.Advance(TimeSpan.FromSeconds(120));

        if (cancelCaller)
        {
            await caller.CancelAsync();
        }

        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), lifetime.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        var failure = await Record.ExceptionAsync(() => executing.WaitAsync(lifetime.Token));

        // Assert
        if (cancelCaller)
        {
            Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(failure, exactMatch: false).CancellationToken);
        }
        else
        {
            _ = Assert.IsType<TimeoutException>(failure);
        }

        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
