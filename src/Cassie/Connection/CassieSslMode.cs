namespace Cntryl.Cassie.Connection
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
}
