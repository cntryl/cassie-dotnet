using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie
{
    internal static partial class CassieLogging
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Cassie connection opened.")]
        private static partial void WriteConnectionOpened(ILogger logger);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Cassie connection closed.")]
        private static partial void WriteConnectionClosed(ILogger logger);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Cassie operation {Operation} failed with {ExceptionType}.")]
        private static partial void WriteOperationFailed(ILogger logger, string operation, string exceptionType);

        internal static void ConnectionOpened(ILogger? logger)
        {
            if (logger is not null)
            {
                WriteConnectionOpened(logger);
            }
        }

        internal static void ConnectionClosed(ILogger? logger)
        {
            if (logger is not null)
            {
                WriteConnectionClosed(logger);
            }
        }

        internal static void OperationFailed(ILogger? logger, string operation, string exceptionType)
        {
            if (logger is not null)
            {
                WriteOperationFailed(logger, operation, exceptionType);
            }
        }
    }
}
