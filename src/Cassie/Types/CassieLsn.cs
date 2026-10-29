using System.Globalization;

namespace Cntryl.Cassie.Types
{
    /// <summary>A PostgreSQL write-ahead log position.</summary>
    public readonly record struct CassieLsn(ulong Value)
    {
        /// <summary>Gets the upper 32-bit hexadecimal word.</summary>
        public uint High => (uint)(Value >> 32);

        /// <summary>Gets the lower 32-bit hexadecimal word.</summary>
        public uint Low => (uint)Value;

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Concat(High.ToString("X", CultureInfo.InvariantCulture), "/", Low.ToString("X", CultureInfo.InvariantCulture));
        }
    }
}
