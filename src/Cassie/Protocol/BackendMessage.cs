namespace Cntryl.Cassie.Protocol
{
    /// <summary>Message tags the server sends to the client.</summary>
    internal static class BackendMessage
    {
        public const byte Authentication = (byte)'R';
        public const byte BackendKeyData = (byte)'K';
        public const byte ParameterStatus = (byte)'S';
        public const byte ReadyForQuery = (byte)'Z';
        public const byte RowDescription = (byte)'T';
        public const byte DataRow = (byte)'D';
        public const byte CommandComplete = (byte)'C';
        public const byte EmptyQueryResponse = (byte)'I';
        public const byte ErrorResponse = (byte)'E';
        public const byte CopyInResponse = (byte)'G';
        public const byte CopyOutResponse = (byte)'H';
        public const byte CopyData = (byte)'d';
        public const byte CopyDone = (byte)'c';
        public const byte PortalSuspended = (byte)'s';
    }
}
