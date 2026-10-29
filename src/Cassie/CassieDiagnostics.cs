using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie
{
    /// <summary>OpenTelemetry-compatible diagnostics for Cassie provider operations.</summary>
    /// <remarks>Instrumentation records operation names and outcomes. It never records SQL, parameter values, or credentials.</remarks>
    public static class CassieDiagnostics
    {
        /// <summary>The name of the provider activity source.</summary>
        public const string Name = "Cntryl.Cassie";

        /// <summary>Activities emitted for provider operations.</summary>
        public static ActivitySource ActivitySource { get; } = new(Name);

        /// <summary>Metrics emitted for provider operations.</summary>
        public static Meter Meter { get; } = new(Name);

        private static readonly Counter<long> Operations = Meter.CreateCounter<long>("cassie.client.operations");
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("cassie.client.operation.duration", "ms");

        internal static OperationScope StartOperation(string operation)
        {
            return new(operation);
        }

        internal static async Task<T> ObserveAsync<T>(string operation, Func<Task<T>> action, ILogger? logger = null)
        {
            using OperationScope scope = StartOperation(operation);
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                scope.Fail(exception, logger);
                throw;
            }
        }

        internal static async Task ObserveAsync(string operation, Func<Task> action, ILogger? logger = null)
        {
            using OperationScope scope = StartOperation(operation);
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                scope.Fail(exception, logger);
                throw;
            }
        }

        internal sealed class OperationScope : IDisposable
        {
            private readonly string _operation;
            private readonly long _startedAt = Stopwatch.GetTimestamp();
            private readonly Activity? _activity;
            private readonly Lock _gate = new();
            private bool _failed;
            private bool _disposed;
            private bool _failureLogged;

            internal OperationScope(string operation)
            {
                _operation = operation;
                _activity = ActivitySource.StartActivity($"cassie.{operation}", ActivityKind.Client);
                _ = _activity?.SetTag("db.system", "cassie");
                _ = _activity?.SetTag("db.operation", operation);
            }

            internal void Fail(Exception exception, ILogger? logger = null)
            {
                bool shouldLog = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _failed = true;
                    _ = _activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                    if (logger is not null && !_failureLogged)
                    {
                        _failureLogged = true;
                        shouldLog = true;
                    }
                }

                if (shouldLog)
                {
                    CassieLogging.OperationFailed(logger, _operation, exception.GetType().Name);
                }
            }

            public void Dispose()
            {
                string outcome;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    outcome = _failed ? "error" : "success";
                }

                KeyValuePair<string, object?>[] tags =
                [
                    new("db.system", "cassie"),
                    new("db.operation", _operation),
                    new("outcome", outcome)
                ];
                Operations.Add(1, tags);
                Duration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds, tags);
                _activity?.Dispose();
            }
        }
    }
}
