using System.Net;

namespace Cntryl.Cassie.Types
{
    /// <summary>A Cassie inet or cidr value with its original prefix length.</summary>
    public readonly record struct CassieNetworkAddress
    {
        /// <summary>Creates a network address value.</summary>
        public CassieNetworkAddress(IPAddress address, int prefixLength, bool isCidr)
        {
            ArgumentNullException.ThrowIfNull(address);
            int width = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32
                : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128
                : throw new ArgumentException("Only IPv4 and IPv6 addresses are supported.", nameof(address));
            if ((uint)prefixLength > (uint)width)
            {
                throw new ArgumentOutOfRangeException(nameof(prefixLength));
            }

            Address = address;
            PrefixLength = prefixLength;
            IsCidr = isCidr;
        }

        /// <summary>Gets the IPv4 or IPv6 address.</summary>
        public IPAddress Address { get; }

        /// <summary>Gets the prefix length.</summary>
        public int PrefixLength { get; }

        /// <summary>Gets whether the value has the PostgreSQL cidr identity.</summary>
        public bool IsCidr { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Address}/{PrefixLength}";
        }
    }
}
