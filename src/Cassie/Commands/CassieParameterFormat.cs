namespace Cntryl.Cassie.Commands
{
    /// <summary>Selects the pgwire format used for an input parameter.</summary>
    public enum CassieParameterFormat
    {
        /// <summary>Use binary when this provider has a codec; otherwise use text.</summary>
        Auto,
        /// <summary>Send the provider's PostgreSQL text representation.</summary>
        Text,
        /// <summary>Require and send a supported PostgreSQL binary representation.</summary>
        Binary
    }
}
