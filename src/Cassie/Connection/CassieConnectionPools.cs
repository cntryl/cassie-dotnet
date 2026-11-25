using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Cassie.Connection;

internal static class CassieConnectionPools
{
    // A pool may compare only timestamps from the same clock. Custom clocks and their
    // pools can be collected when callers no longer hold them.
    private static readonly ConditionalWeakTable<TimeProvider, ConcurrentDictionary<string, CassieConnectionPool>> Pools = [];

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string Hash)> TrustFingerprints =
        new(StringComparer.Ordinal);

    internal static (string Key, CassieConnectionPool Pool) Get(
        CassieConnectionStringBuilder settings,
        TimeProvider clock
    )
    {
        var key = GetKey(settings);
        var partition = Pools.GetValue(clock, static _ => new ConcurrentDictionary<string, CassieConnectionPool>(
            StringComparer.Ordinal));
        return (key,
            partition.GetOrAdd(key,
                static (_, configuration) => new CassieConnectionPool(configuration.Settings.MaxPoolSize,
                    TimeSpan.FromSeconds(configuration.Settings.ConnectionIdleLifetime),
                    TimeSpan.FromSeconds(configuration.Settings.ConnectionLifetime), configuration.Clock),
                (Settings: settings, Clock: clock)));
    }

    internal static string GetKey(CassieConnectionStringBuilder settings)
    {
        return CreateKey(settings);
    }

    internal static async Task ClearAsync(string key, TimeProvider clock)
    {
        if (Pools.TryGetValue(clock, out var partition) && partition.TryRemove(key, out var pool))
        {
            await pool.ClearAsync().ConfigureAwait(false);
        }
    }

    internal static async Task ClearAllAsync()
    {
        foreach (var (_, partition) in Pools)
        {
            foreach (var pair in partition)
            {
                if (partition.TryRemove(pair.Key, out var pool))
                {
                    await pool.ClearAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static string GetTrustBundleFingerprint(string path)
    {
        FileInfo file = new(path);
        var stamp = file.LastWriteTimeUtc;
        var length = file.Length;

        if (TrustFingerprints.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.Length == length)
        {
            return cached.Hash;
        }


        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        TrustFingerprints[path] = (stamp, length, hash);
        return hash;
    }

    private static string CreateKey(CassieConnectionStringBuilder settings)
    {
        var database = string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;
        var trustBundleFingerprint = settings.SslRootCertificate.Length == 0
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

        foreach (var component in components)
        {
            _ = material.Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(component);
        }


        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }
}
