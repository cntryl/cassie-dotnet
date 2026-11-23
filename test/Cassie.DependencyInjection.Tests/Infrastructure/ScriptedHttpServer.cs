using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure;

/// <summary>An owned HTTP/1.1 peer that accepts actual HttpClient cookies and JSON requests.</summary>
internal sealed class ScriptedHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<ScriptedHttpRequest, Task<ScriptedHttpReply>> _respond;
    private readonly Task _serving;
    private readonly CancellationTokenSource _shutdown = new();
    private int _requests;

    internal ScriptedHttpServer(Func<ScriptedHttpRequest, Task<ScriptedHttpReply>> respond)
    {
        _respond = respond;
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _serving = ServeAsync();
    }

    internal Uri BaseAddress { get; }
    internal int Requests => Volatile.Read(ref _requests);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _serving;
        }
        catch (Exception exception) when (_shutdown.IsCancellationRequested &&
                                          exception is OperationCanceledException or SocketException
                                              or ObjectDisposedException)
        {
            // Disposing the owned listener ends an outstanding accept or read.
        }
        finally
        {
            _listener.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            TcpClient accepted;

            try
            {
                accepted = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (InvalidOperationException) when (_shutdown.IsCancellationRequested)
            {
                // The listener was stopped between two connections; there is nothing left to serve.
                return;
            }


            using var client = accepted;
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _shutdown.Token);
            _ = Interlocked.Increment(ref _requests);
            var reply = await _respond(request);
            var body = Encoding.UTF8.GetBytes(reply.Body);
            StringBuilder headers =
                new(
                    $"HTTP/1.1 {reply.StatusCode} Test\r\nConnection: close\r\nContent-Length: {body.Length}\r\nContent-Type: application/json\r\n");

            if (reply.Headers is not null)
            {
                foreach (var (name, value) in reply.Headers)
                {
                    _ = headers.Append(name).Append(": ").Append(value).Append("\r\n");
                }
            }


            _ = headers.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _shutdown.Token);
            await stream.WriteAsync(body, _shutdown.Token);
        }
    }

    private static async Task<ScriptedHttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken
    )
    {
        var requestLine = await ReadLineAsync(stream, cancellationToken);
        var parts = requestLine.Split(' ', 3);

        if (parts.Length != 3)
        {
            throw new IOException("Invalid HTTP request line.");
        }


        Dictionary<string, string> headers = [with(StringComparer.OrdinalIgnoreCase)];

        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken);

            if (line.Length == 0)
            {
                break;
            }


            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon < 1)
            {
                throw new IOException("Invalid HTTP request header.");
            }


            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }


        using MemoryStream body = new();

        if (headers.TryGetValue("Transfer-Encoding", out var transfer) &&
            transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var line = await ReadLineAsync(stream, cancellationToken);
                var size = int.Parse(line.Split(';', 2)[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                if (size == 0)
                {
                    while ((await ReadLineAsync(stream, cancellationToken)).Length != 0)
                    {
                    }

                    break;
                }


                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, cancellationToken);
                await body.WriteAsync(chunk, cancellationToken);

                if ((await ReadLineAsync(stream, cancellationToken)).Length != 0)
                {
                    throw new IOException("Invalid HTTP chunk terminator.");
                }
            }
        }
        else if (headers.TryGetValue("Content-Length", out var length))
        {
            var bytes = new byte[int.Parse(length, NumberStyles.None, CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            await body.WriteAsync(bytes, cancellationToken);
        }


        return new ScriptedHttpRequest(parts[0], parts[1], headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using MemoryStream line = new();
        var next = new byte[1];

        while (true)
        {
            await stream.ReadExactlyAsync(next, cancellationToken);

            if (next[0] == '\n')
            {
                var bytes = line.ToArray();
                return bytes.Length > 0 && bytes[^1] == '\r'
                    ? Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1)
                    : throw new IOException("Invalid HTTP line terminator.");
            }


            line.WriteByte(next[0]);

            if (line.Length > 64 * 1024)
            {
                throw new IOException("Scripted HTTP request header exceeds the fixture limit.");
            }
        }
    }
}
