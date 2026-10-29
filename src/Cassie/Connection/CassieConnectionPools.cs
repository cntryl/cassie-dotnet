using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Cassie.Connection
{
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
