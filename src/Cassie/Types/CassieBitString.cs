namespace Cntryl.Cassie.Types
{
    /// <summary>A PostgreSQL bit or varbit value, retaining its exact bit length.</summary>
    public readonly record struct CassieBitString
    {
        /// <summary>Creates a bit string.</summary>
        public CassieBitString(string value, bool isVarying)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.All(static bit => bit is '0' or '1'))
            {
                throw new ArgumentException("A bit string can contain only '0' and '1'.", nameof(value));
            }

            Value = value;
            IsVarying = isVarying;
        }

        /// <summary>Gets the bits in network order.</summary>
        public string Value { get; }

        /// <summary>Gets whether the source type is VARBIT rather than BIT.</summary>
        public bool IsVarying { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Value ?? string.Empty;
        }
    }
}
