using System.Data.Common;
using System.Globalization;

namespace Cntryl.Cassie
{
    /// <summary>Controls whether a Cassie connection negotiates TLS.</summary>
    public enum CassieSslMode
    {
        /// <summary>Require TLS and fail if it cannot be negotiated.</summary>
        Require,
        /// <summary>Use TLS when the server supports it.</summary>
        Prefer,
        /// <summary>Do not negotiate TLS.</summary>
        Disable
    }

    /// <summary>Builds and validates a Cassie connection string.</summary>
    public sealed class CassieConnectionStringBuilder : DbConnectionStringBuilder
    {
        /// <summary>Creates an empty connection string builder.</summary>
        public CassieConnectionStringBuilder()
        {
        }

        /// <summary>Creates a builder initialized from a connection string.</summary>
        /// <param name="connectionString">Connection string to parse.</param>
        public CassieConnectionStringBuilder(string connectionString)
        {
            ConnectionString = connectionString;
        }

        /// <summary>Gets or sets the server host.</summary>
        public string Host { get => GetString("Host", "localhost"); set => this["Host"] = value; }
        /// <summary>Gets or sets the server port.</summary>
        public int Port { get => GetInt32("Port", 5432); set => this["Port"] = value; }
        /// <summary>Gets or sets the database name.</summary>
        public string Database { get => GetString("Database", string.Empty); set => this["Database"] = value; }
        /// <summary>Gets or sets the user name.</summary>
        public string Username { get => GetString("Username", string.Empty); set => this["Username"] = value; }
        /// <summary>Gets or sets the password.</summary>
        public string Password { get => GetString("Password", string.Empty); set => this["Password"] = value; }
        /// <summary>Gets or sets the connection timeout in seconds.</summary>
        public int Timeout { get => GetInt32("Timeout", 15); set => this["Timeout"] = value; }
        /// <summary>Gets or sets the client application name.</summary>
        public string ApplicationName { get => GetString("Application Name", "Cassie .NET"); set => this["Application Name"] = value; }
        /// <summary>Gets or sets PostgreSQL-compatible startup options, such as <c>-c TimeZone=UTC</c>.</summary>
        public string Options
        {
            get
            {
                string options = GetString("Options", string.Empty);
                return options.IndexOf('\0', StringComparison.Ordinal) < 0
                    ? options
                    : throw new ArgumentException("Options cannot contain a NUL character.", nameof(Options));
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                this["Options"] = value.IndexOf('\0', StringComparison.Ordinal) < 0
                    ? value
                    : throw new ArgumentException("Options cannot contain a NUL character.", nameof(Options));
            }
        }
        /// <summary>Gets or sets the TLS mode. TLS is required by default.</summary>
        public CassieSslMode SslMode
        {
            get => Enum.TryParse(GetString("SSL Mode", nameof(CassieSslMode.Require)), true, out CassieSslMode mode) && Enum.IsDefined(mode)
                ? mode
                : throw new ArgumentException("SSL Mode must be Require, Prefer, or Disable.", nameof(SslMode));
            set => this["SSL Mode"] = Enum.IsDefined(value)
                ? value.ToString()
                : throw new ArgumentOutOfRangeException(nameof(value), value, "SSL Mode must be Require, Prefer, or Disable.");
        }
        /// <summary>Gets or sets a PEM bundle of trusted TLS root certificates. An empty value uses platform trust.</summary>
        public string SslRootCertificate { get => GetString("SSL Root Certificate", string.Empty); set => this["SSL Root Certificate"] = value; }
        /// <summary>Gets or sets whether physical connections are pooled. Pooling is enabled by default.</summary>
        public bool Pooling { get => GetBoolean("Pooling", true); set => this["Pooling"] = value; }
        /// <summary>Gets or sets the maximum number of physical connections in a pool.</summary>
        public int MaxPoolSize { get => GetInt32("Maximum Pool Size", 100); set => this["Maximum Pool Size"] = value; }

        /// <summary>Gets the number of rows requested per portal fetch for extended queries.</summary>
        public int FetchSize { get => GetInt32("Fetch Size", 128); set => this["Fetch Size"] = value; }
        /// <summary>Gets or sets the default command timeout in seconds; zero disables it.</summary>
        public int CommandTimeout { get => GetInt32("Command Timeout", 30); set => this["Command Timeout"] = value; }
        /// <summary>Gets or sets how many seconds an idle pooled session may wait before it is discarded; zero disables the limit.</summary>
        public int ConnectionIdleLifetime { get => GetInt32("Connection Idle Lifetime", 300); set => this["Connection Idle Lifetime"] = value; }
        /// <summary>Gets or sets the maximum age in seconds of a pooled session; zero disables the limit.</summary>
        public int ConnectionLifetime { get => GetInt32("Connection Lifetime", 0); set => this["Connection Lifetime"] = value; }

        private static readonly HashSet<string> KnownKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Port", "Database", "Username", "Password", "Timeout", "Application Name", "Options",
            "SSL Mode", "SSL Root Certificate", "Pooling", "Maximum Pool Size", "Fetch Size", "Command Timeout",
            "Connection Idle Lifetime", "Connection Lifetime"
        };

        /// <summary>Rejects connection string keywords this provider does not recognize.</summary>
        /// <exception cref="ArgumentException">A keyword is unknown, so it would otherwise be silently ignored.</exception>
        public void ValidateKeywords()
        {
            foreach (string key in Keys.Cast<string>())
            {
                if (!KnownKeywords.Contains(key))
                {
                    throw new ArgumentException($"Unknown connection string keyword '{key}'.", key);
                }
            }
        }

        private string GetString(string key, string fallback)
        {
            return TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;
        }

        private bool GetBoolean(string key, bool fallback)
        {
            return !TryGetValue(key, out object? value)
                ? fallback
                : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed)
                    ? parsed
                    : throw new ArgumentException($"{key} must be true or false.", key);
        }

        private int GetInt32(string key, int fallback)
        {
            return !TryGetValue(key, out object? value)
                ? fallback
                : int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                    (key switch
                    {
                        "Port" => parsed is > 0 and <= 65535,
                        "Maximum Pool Size" => parsed is > 0 and <= 1000,
                        "Fetch Size" => parsed > 0,
                        _ => parsed is >= 0 and <= 2147483
                    })
                ? parsed
                : throw new ArgumentException(key switch
                {
                    "Port" => "Port must be between 1 and 65535.",
                    "Maximum Pool Size" => "Maximum Pool Size must be between 1 and 1000.",
                    "Fetch Size" => "Fetch Size must be a positive integer.",
                    _ => $"{key} must be between 0 and 2147483 seconds."
                }, key);
        }
    }
}
