using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class DiagnosticsContractTests
    {
        [Fact]
        public async Task ShouldEmitSanitizedActivitiesAndLowCardinalityMetricsGivenProviderOperationsWhenObserved()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                await ReadStartupAsync(stream);
                await WriteFrameAsync(stream, (byte)'R', Int32(0));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

                (byte queryTag, byte[] query) = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'Q', queryTag);
                Assert.Equal("SELECT 1 AS value", CString(query));
                await WriteFrameAsync(stream, (byte)'T', RowDescription("value", 23, 4));
                await WriteFrameAsync(stream, (byte)'D', DataRow(Encoding.UTF8.GetBytes("1")));
                await WriteFrameAsync(stream, (byte)'C', CStringBytes("SELECT 1"));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

                (byte copyTag, byte[] copySql) = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'Q', copyTag);
                Assert.Equal("COPY ledger FROM STDIN WITH (FORMAT csv)", CString(copySql));
                await WriteFrameAsync(stream, (byte)'G', [0, 0, 0]);
                using MemoryStream copiedData = new();
                while (true)
                {
                    (byte dataTag, byte[] payload) = await ReadFrontendFrameAsync(stream);
                    if (dataTag == (byte)'c')
                    {
                        break;
                    }

                    Assert.Equal((byte)'d', dataTag);
                    copiedData.Write(payload);
                }

                Assert.Equal("1,alpha\n", Encoding.UTF8.GetString(copiedData.ToArray()));
                await WriteFrameAsync(stream, (byte)'C', CStringBytes("COPY 1"));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                (byte terminateTag, _) = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'X', terminateTag);
            }, TestContext.Current.CancellationToken);

            ConcurrentQueue<(string Name, ActivityStatusCode Status, KeyValuePair<string, object?>[] Tags)> activities = new();
            using ActivityListener activityListener = new()
            {
                ShouldListenTo = static source => source.Name == CassieDiagnostics.Name,
                Sample = SampleActivity,
                ActivityStopped = activity => activities.Enqueue((activity.OperationName, activity.Status, activity.TagObjects.ToArray()))
            };
            ActivitySource.AddActivityListener(activityListener);

            ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> measurements = new();
            using MeterListener meterListener = new();
            meterListener.InstrumentPublished = static (instrument, measurementListener) =>
            {
                if (instrument.Meter.Name == CassieDiagnostics.Name)
                {
                    measurementListener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            meterListener.Start();

            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret-value;SSL Mode=Disable;Timeout=3;Pooling=false"
            };

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using (CassieCommand queryCommand = (CassieCommand)connection.CreateCommand())
            {
                queryCommand.CommandText = "SELECT 1 AS value";
                Assert.Equal(1, await queryCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            using (CassieCommand copyCommand = (CassieCommand)connection.CreateCommand())
            {
                copyCommand.CommandText = "COPY ledger FROM STDIN WITH (FORMAT csv)";
                await using MemoryStream source = new("1,alpha\n"u8.ToArray());
                Assert.Equal(1, await copyCommand.CopyFromAsync(source, TestContext.Current.CancellationToken));
            }

            await connection.CloseAsync();
            await peer;

            // Assert
            string[] expectedActivities =
            [
                "cassie.connection.open",
                "cassie.query",
                "cassie.copy.from",
                "cassie.connection.close"
            ];
            foreach (string expectedActivity in expectedActivities)
            {
                string expectedOperation = expectedActivity["cassie.".Length..];
                Assert.Contains(
                    activities,
                    activity => activity.Name == expectedActivity
                        && activity.Status == ActivityStatusCode.Unset
                        && activity.Tags.Any(tag => tag.Key == "db.operation" && Equals(tag.Value, expectedOperation)));
            }

            Assert.Contains(measurements, measurement => measurement.Name == "cassie.client.operations");
            Assert.Contains(measurements, measurement => measurement.Name == "cassie.client.operation.duration");
            foreach (string expectedOperation in expectedActivities.Select(static name => name["cassie.".Length..]))
            {
                Assert.Contains(measurements, measurement => measurement.Tags.Any(tag => tag.Key == "db.operation" && Equals(tag.Value, expectedOperation)));
            }

            string diagnostics = string.Join(
                Environment.NewLine,
                activities.SelectMany(activity => new[] { activity.Name }.Concat(activity.Tags.Select(static tag => $"{tag.Key}={tag.Value}")))
                    .Concat(measurements.Select(measurement => $"{measurement.Name}:{measurement.Value}:{string.Join(',', measurement.Tags.Select(static tag => $"{tag.Key}={tag.Value}"))}")));
            Assert.DoesNotContain("SELECT 1", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("COPY ledger", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("ledger", diagnostics, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShouldEmitSanitizedLifecycleLogsGivenLoggerFactoryWhenConnectionOpensAndCloses()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                byte[] startupHeader = new byte[8];
                await stream.ReadExactlyAsync(startupHeader);
                int startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
                await stream.ReadExactlyAsync(new byte[startupLength - startupHeader.Length]);
                await WriteFrameAsync(stream, (byte)'R', Int32(0));
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                (byte tag, _) = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'X', tag);
            }, TestContext.Current.CancellationToken);
            using CapturingLoggerFactory loggerFactory = new();
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret-value;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            PropertyInfo loggerFactoryProperty = typeof(CassieConnection).GetProperty("LoggerFactory")
                ?? throw new InvalidOperationException("CassieConnection.LoggerFactory is not exposed.");
            loggerFactoryProperty.SetValue(connection, loggerFactory);

            // Act
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await connection.CloseAsync();
            await peer;

            // Assert
            string messages = string.Join(Environment.NewLine, loggerFactory.Messages);
            Assert.Contains("Cassie connection opened.", loggerFactory.Messages);
            Assert.Contains("Cassie connection closed.", loggerFactory.Messages);
            Assert.DoesNotContain("secret-value", messages, StringComparison.Ordinal);
            Assert.DoesNotContain("ledger", messages, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShouldLogFailureCategoryWithoutServerDetailGivenRejectedPasswordWhenConnectionOpens()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                byte[] startupHeader = new byte[8];
                await stream.ReadExactlyAsync(startupHeader);
                int startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
                await stream.ReadExactlyAsync(new byte[startupLength - startupHeader.Length]);
                await WriteFrameAsync(stream, (byte)'R', Int32(3));
                (byte passwordTag, _) = await ReadFrontendFrameAsync(stream);
                Assert.Equal((byte)'p', passwordTag);
                byte[] error = [(byte)'S', .. Encoding.UTF8.GetBytes("FATAL"), 0, (byte)'C', .. Encoding.UTF8.GetBytes("28P01"), 0, (byte)'M', .. Encoding.UTF8.GetBytes("server-detail-secret"), 0, 0];
                await WriteFrameAsync(stream, (byte)'E', error);
            }, TestContext.Current.CancellationToken);
            using CapturingLoggerFactory loggerFactory = new();
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret-value;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            PropertyInfo loggerFactoryProperty = typeof(CassieConnection).GetProperty("LoggerFactory")
                ?? throw new InvalidOperationException("CassieConnection.LoggerFactory is not exposed.");
            loggerFactoryProperty.SetValue(connection, loggerFactory);

            // Act
            CassieException exception = await Assert.ThrowsAsync<CassieException>(connection.OpenAsync);
            await peer;

            // Assert
            string messages = string.Join(Environment.NewLine, loggerFactory.Messages);
            Assert.Equal("28P01", exception.SqlState);
            Assert.Contains("connection.open", messages, StringComparison.Ordinal);
            Assert.Contains(nameof(CassieException), messages, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", messages, StringComparison.Ordinal);
            Assert.DoesNotContain("server-detail-secret", messages, StringComparison.Ordinal);
        }

        private static byte[] Int32(int value)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        private static async Task ReadStartupAsync(NetworkStream stream)
        {
            byte[] length = new byte[4];
            await stream.ReadExactlyAsync(length);
            int messageLength = BinaryPrimitives.ReadInt32BigEndian(length);
            await stream.ReadExactlyAsync(new byte[messageLength - length.Length]);
        }

        private static ActivitySamplingResult SampleActivity(ref ActivityCreationOptions<ActivityContext> options)
        {
            return ActivitySamplingResult.AllDataAndRecorded;
        }

        private static byte[] Int16(short value)
        {
            byte[] bytes = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] RowDescription(string name, int oid, short typeSize)
        {
            using MemoryStream payload = new();
            payload.Write(Int16(1));
            payload.Write(CStringBytes(name));
            payload.Write(Int32(0));
            payload.Write(Int16(0));
            payload.Write(Int32(oid));
            payload.Write(Int16(typeSize));
            payload.Write(Int32(-1));
            payload.Write(Int16(0));
            return payload.ToArray();
        }

        private static byte[] DataRow(byte[] value)
        {
            using MemoryStream payload = new();
            payload.Write(Int16(1));
            payload.Write(Int32(value.Length));
            payload.Write(value);
            return payload.ToArray();
        }

        private static byte[] CStringBytes(string value)
        {
            return [.. Encoding.UTF8.GetBytes(value), 0];
        }

        private static string CString(byte[] value)
        {
            int terminator = Array.IndexOf(value, (byte)0);
            return Encoding.UTF8.GetString(value, 0, terminator < 0 ? value.Length : terminator);
        }

        private static async Task<(byte Tag, byte[] Payload)> ReadFrontendFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload);
            return (header[0], payload);
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame);
        }

        private sealed class CapturingLoggerFactory : ILoggerFactory
        {
            private readonly ConcurrentQueue<string> _messages = new();

            internal string[] Messages => [.. _messages];

            public ILogger CreateLogger(string categoryName)
            {
                return new CapturingLogger(_messages);
            }

            public void AddProvider(ILoggerProvider provider)
            {
                ArgumentNullException.ThrowIfNull(provider);
            }

            public void Dispose()
            {
            }
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
