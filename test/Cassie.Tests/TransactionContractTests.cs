using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class TransactionContractTests
    {
        [Fact]
        public async Task ShouldExecuteSavepointLifecycleGivenReadCommittedTransactionWhenCompleted()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeTransactionAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // Act
            await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
            using DbCommand unassignedCommand = connection.CreateCommand();
            unassignedCommand.CommandText = "SELECT 1";
            int implicitlyEnlistedResult = await unassignedCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await transaction.SaveAsync("safe name", TestContext.Current.CancellationToken);
            await transaction.RollbackAsync("safe name", TestContext.Current.CancellationToken);
            await transaction.ReleaseAsync("safe name", TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
            Assert.Equal(-1, implicitlyEnlistedResult);
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public void ShouldRejectUnsupportedIsolationGivenIsolationLevelWhenTransactionBegins()
        {
            // Arrange
            using CassieConnection connection = new();

            // Act
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(IsolationLevel.Chaos));

            // Assert
            Assert.Contains("Isolation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ShouldInvalidateTransactionGivenConnectionClosesWhenTransactionIsActive()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeBeginThenCloseAsync(listener), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);

            // Act
            await connection.CloseAsync();
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("no longer active", exception.Message, StringComparison.Ordinal);
            await peer;
        }

        [Fact]
        public async Task ShouldCancelTransactionStartGivenServerWaitsForBeginWhenCancellationIsRequested()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> beginReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeCancelableBeginAsync(listener, beginReceived), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellation = new();
            Task<DbTransaction> begin = Task.Run(async () => await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token));
            _ = await beginReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // Act
            await cancellation.CancelAsync();
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            ConnectionState state = connection.State;

            // Assert
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(ConnectionState.Closed, state);
            Assert.NotNull(exception);
            await peer;
        }

        [Fact]
        public async Task ShouldReturnPendingDisposalGivenServerWaitsForRollbackWhenTransactionIsDisposedAsync()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> rollbackReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () => await ServeDelayedRollbackAsync(listener, rollbackReceived, releaseRollback), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);

            // Act
            Task<ValueTask> disposeCall = Task.Run(transaction.DisposeAsync);
            _ = await rollbackReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            ValueTask disposal;
            try
            {
                disposal = await disposeCall.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
            catch
            {
                _ = releaseRollback.TrySetResult(true);
                _ = await disposeCall.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                throw;
            }

            _ = releaseRollback.TrySetResult(true);
            bool disposalCompletedBeforeServerResponse = disposal.IsCompleted;
            await disposal;
            await connection.CloseAsync();

            // Assert
            Assert.False(disposalCompletedBeforeServerResponse);
            await peer;
        }

        private static async Task ServeTransactionAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            string[] expected = ["BEGIN ISOLATION LEVEL READ COMMITTED", "SELECT 1", "SAVEPOINT \"safe name\"", "ROLLBACK TO SAVEPOINT \"safe name\"", "RELEASE SAVEPOINT \"safe name\"", "COMMIT"];
            foreach (string sql in expected)
            {
                byte[] query = await ReadFrontendFrameAsync(stream);
                Assert.Equal(sql + "\0", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes(sql.Split(' ')[0]), 0]);
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            }
        }

        private static async Task ServeBeginThenCloseAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] begin = await ReadFrontendFrameAsync(stream);
            Assert.Equal("BEGIN ISOLATION LEVEL READ COMMITTED\0", Encoding.UTF8.GetString(begin, 1, begin.Length - 1));
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("BEGIN"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'T']);
            byte[] terminate = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'X', terminate[0]);
        }

        private static async Task ServeCancelableBeginAsync(TcpListener listener, TaskCompletionSource<bool> beginReceived)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] begin = await ReadFrontendFrameAsync(stream);
            Assert.Equal("BEGIN ISOLATION LEVEL READ COMMITTED\0", Encoding.UTF8.GetString(begin, 1, begin.Length - 1));
            _ = beginReceived.TrySetResult(true);

            byte[] buffer = new byte[1];
            try
            {
                int received = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
                Assert.Equal(0, received);
                return;
            }
            catch (TimeoutException)
            {
                await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("BEGIN"), 0]);
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'T']);
                byte[] terminate = await ReadFrontendFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal((byte)'X', terminate[0]);
            }
            catch (IOException)
            {
            }
        }

        private static async Task ServeDelayedRollbackAsync(TcpListener listener, TaskCompletionSource<bool> rollbackReceived, TaskCompletionSource<bool> releaseRollback)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

            byte[] begin = await ReadFrontendFrameAsync(stream);
            Assert.Equal("BEGIN ISOLATION LEVEL READ COMMITTED\0", Encoding.UTF8.GetString(begin, 1, begin.Length - 1));
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("BEGIN"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'T']);

            byte[] rollback = await ReadFrontendFrameAsync(stream);
            Assert.Equal("ROLLBACK\0", Encoding.UTF8.GetString(rollback, 1, rollback.Length - 1));
            _ = rollbackReceived.TrySetResult(true);
            _ = await releaseRollback.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("ROLLBACK"), 0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] terminate = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'X', terminate[0]);
        }

        private static async Task ReadStartupPacketAsync(NetworkStream stream, byte[] prefix)
        {
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
            await stream.ReadExactlyAsync(new byte[length - prefix.Length]);
        }

        private static async Task<byte[]> ReadFrontendFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header);
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1));
            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload);
            return [header[0], .. payload];
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 4)).CopyTo(frame, 1);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame);
        }
    }
}
