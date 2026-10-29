using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure
{
    internal sealed class TrackingLoggerFactory : ILoggerFactory
    {
        internal bool IsDisposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
            throw new NotSupportedException("The tracking factory does not collect providers.");
        }

        public void Dispose()
        {
            IsDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
