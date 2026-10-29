namespace Cntryl.Cassie.Protocol
{
    /// <summary>Transaction status bytes carried by ReadyForQuery.</summary>
    internal static class TransactionStatus
    {
        public const byte Idle = (byte)'I';
        public const byte InTransaction = (byte)'T';
        public const byte Failed = (byte)'E';

        public static bool IsValid(byte status)
        {
            return status is Idle or InTransaction or Failed;
        }
    }
}
