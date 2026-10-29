namespace Cntryl.Cassie.Protocol
{
    /// <summary>Message tags the client sends to the server.</summary>
    internal static class FrontendMessage
    {
        public const byte Query = (byte)'Q';
        public const byte Parse = (byte)'P';
        public const byte Bind = (byte)'B';
        public const byte Describe = (byte)'D';
        public const byte Close = (byte)'C';
        public const byte Sync = (byte)'S';
        public const byte PasswordMessage = (byte)'p';
        public const byte CopyData = (byte)'d';
        public const byte CopyDone = (byte)'c';
        public const byte CopyFail = (byte)'f';
        public const byte Terminate = (byte)'X';
    }
}
