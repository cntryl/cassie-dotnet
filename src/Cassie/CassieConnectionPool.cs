using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie
{
    internal sealed class CassiePooledSession(TcpClient client, Stream stream, string serverVersion, CassieDateStyle? dateStyle, CassieDateStyle startupDateStyle, int backendProcessId, int backendSecretKey, long createdTimestamp) : IAsyncDisposable
    {
        internal TcpClient Client { get; } = client;
        internal Stream Stream { get; } = stream;
        internal string ServerVersion { get; } = serverVersion;
        internal CassieDateStyle? DateStyle { get; } = dateStyle;
        internal CassieDateStyle StartupDateStyle { get; } = startupDateStyle;
        internal int BackendProcessId { get; } = backendProcessId;
        internal int BackendSecretKey { get; } = backendSecretKey;
        internal long CreatedTimestamp { get; } = createdTimestamp;
        internal long ReturnedTimestamp { get; set; }

        /// <summary>Returns false when the peer has closed the socket while the session sat idle.</summary>
        internal bool IsTransportAlive()
        {
            try
            {
                Socket socket = Client.Client;
                return socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Client.Dispose();
            }
        }
    }

    [SuppressMessage("Reliability", "CA1001", Justification = "Pool semaphores remain live while any lease can be returned or waiting to rent.")]
    internal sealed class CassieConnectionPool(int maximumPoolSize, TimeSpan idleLifetime, TimeSpan maximumLifetime)
    {
        private readonly SemaphoreSlim _changed = new(0);
        private readonly Queue<CassiePooledSession> _idle = new();
        private readonly Lock _gate = new();
        private int _physicalCount;
        private int _waiters;
        private bool _cleared;

        internal async Task<(CassiePooledSession? Session, bool ReservedSlot)> RentAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                CassiePooledSession? stale = null;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_cleared, this);
                    if (_idle.TryDequeue(out CassiePooledSession? available))
                    {
                        if (!IsUnusable(available))
                        {
                            return (available, false);
                        }

                        _physicalCount--;
                        stale = available;
                    }
                    else if (_physicalCount < maximumPoolSize)
                    {
                        _physicalCount++;
                        return (null, true);
                    }
                    else
                    {
                        _waiters++;
                    }
                }

                if (stale is not null)
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    lock (_gate)
                    {
                        _waiters--;
                    }

                    throw;
                }

                lock (_gate)
                {
                    _waiters--;
                }
            }
        }

        private bool IsUnusable(CassiePooledSession session)
        {
            long now = Stopwatch.GetTimestamp();
            return (idleLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(session.ReturnedTimestamp, now) > idleLifetime)
                || (maximumLifetime > TimeSpan.Zero && Stopwatch.GetElapsedTime(session.CreatedTimestamp, now) > maximumLifetime)
                || !session.IsTransportAlive();
        }

        internal async ValueTask ReturnAsync(CassiePooledSession session)
        {
            session.ReturnedTimestamp = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (!_cleared)
                {
                    _idle.Enqueue(session);
                    if (_waiters > 0)
                    {
                        _ = _changed.Release();
                    }

                    return;
                }

                _physicalCount--;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                SignalWaiter();
            }
        }

        internal void ReleaseReservedSlot()
        {
            lock (_gate)
            {
                _physicalCount--;
            }

            SignalWaiter();
        }

        internal async Task ClearAsync()
        {
            List<CassiePooledSession> sessions;
            lock (_gate)
            {
                _cleared = true;
                sessions = [.. _idle];
                _idle.Clear();
                _physicalCount -= sessions.Count;
                if (_waiters > 0)
                {
                    _ = _changed.Release(_waiters);
                }
            }

            foreach (CassiePooledSession session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void SignalWaiter()
        {
            lock (_gate)
            {
                if (_waiters > 0)
                {
                    _ = _changed.Release();
                }
            }
        }
    }

    internal static class CassieConnectionPools
    {
        private static readonly ConcurrentDictionary<string, CassieConnectionPool> Pools = new(StringComparer.Ordinal);

        internal static (string Key, CassieConnectionPool Pool) Get(CassieConnectionStringBuilder settings)
        {
            string key = GetKey(settings);
            return (key, Pools.GetOrAdd(key, _ => new CassieConnectionPool(settings.MaxPoolSize, TimeSpan.FromSeconds(settings.ConnectionIdleLifetime), TimeSpan.FromSeconds(settings.ConnectionLifetime))));
        }

        internal static string GetKey(CassieConnectionStringBuilder settings)
        {
            return CreateKey(settings);
        }

        internal static async Task ClearAsync(string key)
        {
            if (Pools.TryRemove(key, out CassieConnectionPool? pool))
            {
                await pool.ClearAsync().ConfigureAwait(false);
            }
        }

        internal static async Task ClearAllAsync()
        {
            foreach (KeyValuePair<string, CassieConnectionPool> pair in Pools)
            {
                if (Pools.TryRemove(pair.Key, out CassieConnectionPool? pool))
                {
                    await pool.ClearAsync().ConfigureAwait(false);
                }
            }
        }

        private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string Hash)> TrustFingerprints = new(StringComparer.Ordinal);

        private static string GetTrustBundleFingerprint(string path)
        {
            FileInfo file = new(path);
            DateTime stamp = file.LastWriteTimeUtc;
            long length = file.Length;
            if (TrustFingerprints.TryGetValue(path, out (DateTime Stamp, long Length, string Hash) cached) && cached.Stamp == stamp && cached.Length == length)
            {
                return cached.Hash;
            }

            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            TrustFingerprints[path] = (stamp, length, hash);
            return hash;
        }

        private static string CreateKey(CassieConnectionStringBuilder settings)
        {
            string database = string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;
            string trustBundleFingerprint = settings.SslRootCertificate.Length == 0
                ? string.Empty
                : GetTrustBundleFingerprint(settings.SslRootCertificate);
            string[] components =
            [
                settings.Host,
                settings.Port.ToString(CultureInfo.InvariantCulture),
                database,
                settings.Username,
                settings.Password,
                settings.SslMode.ToString(),
                trustBundleFingerprint,
                settings.ApplicationName,
                settings.Options,
                settings.MaxPoolSize.ToString(CultureInfo.InvariantCulture),
                settings.ConnectionIdleLifetime.ToString(CultureInfo.InvariantCulture),
                settings.ConnectionLifetime.ToString(CultureInfo.InvariantCulture)
            ];
            StringBuilder material = new();
            foreach (string component in components)
            {
                _ = material.Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(component);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
        }
    }
}
