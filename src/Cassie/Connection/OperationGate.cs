namespace Cntryl.Cassie.Connection
{
    /// <summary>
    /// Marks the connection as busy with one server round trip. Each <see cref="Begin"/> bumps a serial so a
    /// delayed cancel watchdog can tell that the operation it was started for has already finished.
    /// </summary>
    internal sealed class OperationGate
    {
        private int _active;
        private long _serial;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public long Serial => Interlocked.Read(ref _serial);

        public void Begin()
        {
            _ = Interlocked.Increment(ref _serial);
            Volatile.Write(ref _active, 1);
        }

        public void End()
        {
            Volatile.Write(ref _active, 0);
        }
    }
}
