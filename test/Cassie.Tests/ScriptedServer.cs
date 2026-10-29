using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cntryl.Cassie.Tests
{
    /// <summary>A scripted pgwire peer that completes startup and then hands each session to a test handler.</summary>
    internal sealed class ScriptedServer : IDisposable
    {
        private const int CancelRequestCode = 80877102;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<ScriptedSession, Task> _handler;
        private int _accepted;
        private int _terminated;
        private int _cancelRequests;

        internal ScriptedServer(Func<ScriptedSession, Task> handler)
        {
            _handler = handler;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        internal int Port { get; }

        /// <summary>Gets the number of regular (non-cancel) sessions accepted.</summary>
        internal int Accepted => Volatile.Read(ref _accepted);

        /// <summary>Gets the number of Terminate messages received.</summary>
        internal int Terminated => Volatile.Read(ref _terminated);

        /// <summary>Gets the number of cancel requests received.</summary>
        internal int CancelRequests => Volatile.Read(ref _cancelRequests);

        internal string ConnectionString(string extra = "")
        {
            return $"Host=127.0.0.1;Port={Port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;{extra}";
        }

        internal void StopAccepting()
        {
            _listener.Stop();
        }

        internal static bool WaitUntil(Func<bool> condition)
        {
            return SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5));
        }

        internal void CountTerminate()
        {
            _ = Interlocked.Increment(ref _terminated);
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener was stopped by the test.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    ScriptedSession session = new(this, client);
                    bool regular = await session.StartupAsync();
                    if (!regular)
                    {
                        _ = Interlocked.Increment(ref _cancelRequests);
                        return;
                    }

                    _ = Interlocked.Increment(ref _accepted);
                    await _handler(session);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
                {
                    // The provider closed or aborted the connection.
                }
            }
        }

        internal static int CancelCode => CancelRequestCode;
    }

    internal sealed class ScriptedSession(ScriptedServer server, TcpClient client)
    {
        internal NetworkStream Stream { get; } = client.GetStream();

        /// <summary>Completes startup; returns false when the peer sent a cancel request instead.</summary>
        internal async Task<bool> StartupAsync()
        {
            byte[] prefix = new byte[8];
            await Stream.ReadExactlyAsync(prefix);
            int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
            int code = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(4));
            await Stream.ReadExactlyAsync(new byte[length - prefix.Length]);
            if (code == ScriptedServer.CancelCode)
            {
                return false;
            }

            await WriteAsync((byte)'R', [0, 0, 0, 0]);
            byte[] key = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(key, 42);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(4), 7);
            await WriteAsync((byte)'K', key);
            await WriteReadyAsync();
            return true;
        }

        /// <summary>Reads one frontend frame; the tag is followed by the payload.</summary>
        internal async Task<(byte Tag, byte[] Payload)> ReadFrameAsync()
        {
            byte[] header = new byte[5];
            await Stream.ReadExactlyAsync(header);
            byte[] payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
            await Stream.ReadExactlyAsync(payload);
            if (header[0] == (byte)'X')
            {
                server.CountTerminate();
            }

            return (header[0], payload);
        }

        internal async Task WriteAsync(byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
            payload.CopyTo(frame, 5);
            await Stream.WriteAsync(frame);
        }

        internal Task WriteReadyAsync(char status = 'I')
        {
            return WriteAsync((byte)'Z', [(byte)status]);
        }

        internal Task WriteCommandCompleteAsync(string tag)
        {
            return WriteAsync((byte)'C', [.. Encoding.UTF8.GetBytes(tag), 0]);
        }

        /// <summary>Writes a one-column int4 text RowDescription.</summary>
        internal Task WriteIntRowDescriptionAsync(string name = "value")
        {
            List<byte> payload = [0, 1, .. Encoding.UTF8.GetBytes(name), 0];
            payload.AddRange(new byte[4]);
            payload.AddRange(new byte[2]);
            payload.AddRange(IntBytes(23));
            payload.AddRange([0, 4]);
            payload.AddRange(IntBytes(-1));
            payload.AddRange(new byte[2]);
            return WriteAsync((byte)'T', [.. payload]);
        }

        internal Task WriteTextRowAsync(string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            return WriteAsync((byte)'D', [0, 1, .. IntBytes(text.Length), .. text]);
        }

        /// <summary>Answers simple queries with the frames produced by <paramref name="reply"/> until the client terminates.</summary>
        internal async Task ServeQueriesAsync(Func<string, Task> reply)
        {
            while (true)
            {
                (byte tag, byte[] payload) = await ReadFrameAsync();
                if (tag == (byte)'X')
                {
                    return;
                }

                if (tag == (byte)'Q')
                {
                    await reply(Encoding.UTF8.GetString(payload, 0, payload.Length - 1));
                    await WriteReadyAsync();
                }
            }
        }

        /// <summary>Reads frames until the client closes or terminates, never replying.</summary>
        internal async Task StayVerySilentAsync()
        {
            while (true)
            {
                (byte tag, _) = await ReadFrameAsync();
                if (tag == (byte)'X')
                {
                    return;
                }
            }
        }

        internal void Close()
        {
            client.Close();
        }

        internal static byte[] IntBytes(int value)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }
    }
}
