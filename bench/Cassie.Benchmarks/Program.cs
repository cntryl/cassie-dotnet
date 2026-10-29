using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using System.Diagnostics;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

string connectionString = Environment.GetEnvironmentVariable("CASSIE_DOTNET_BENCHMARK_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set CASSIE_DOTNET_BENCHMARK_CONNECTION_STRING to a Cassie connection string.");
string serverRevision = Environment.GetEnvironmentVariable("CASSIE_SERVER_REVISION")
    ?? throw new InvalidOperationException("Set CASSIE_SERVER_REVISION to the exact 40-character Cassie commit being measured.");
if (serverRevision.Length != 40 || !serverRevision.All(Uri.IsHexDigit))
{
    throw new InvalidOperationException("CASSIE_SERVER_REVISION must be a 40-character hexadecimal commit.");
}

int iterations = ReadPositiveInteger("CASSIE_DOTNET_BENCHMARK_ITERATIONS", 500);
int warmupIterations = ReadPositiveInteger("CASSIE_DOTNET_BENCHMARK_WARMUP", 50);
string providerRevision = Environment.GetEnvironmentVariable("CASSIE_DOTNET_PROVIDER_REVISION") ?? "unspecified";
string providerTreeState = Environment.GetEnvironmentVariable("CASSIE_DOTNET_PROVIDER_TREE_STATE") ?? "unspecified";
string serverProfile = Environment.GetEnvironmentVariable("CASSIE_DOTNET_BENCHMARK_PROFILE") ?? "unspecified";
CassieConnectionStringBuilder unpooledSettings = new(connectionString) { Pooling = false };
CassieConnectionStringBuilder pooledSettings = new(connectionString) { Pooling = true, MaxPoolSize = 4 };

Console.WriteLine(JsonSerializer.Serialize(new
{
    record = "manifest",
    recorded_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    server_revision = serverRevision.ToUpperInvariant(),
    server_profile = serverProfile,
    provider_revision = providerRevision,
    provider_tree_state = providerTreeState,
    runtime = Environment.Version.ToString(),
    framework = AppContext.TargetFrameworkName,
    host_os = RuntimeInformation.OSDescription,
    os_architecture = RuntimeInformation.OSArchitecture.ToString(),
    process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    processor_count = Environment.ProcessorCount,
    ssl_mode = unpooledSettings.SslMode.ToString(),
    connection_timeout_seconds = unpooledSettings.Timeout,
    iterations,
    warmup_iterations = warmupIterations,
    pooling = "connection-reuse; disabled and enabled open-close measured separately",
    pooled_maximum_size = pooledSettings.MaxPoolSize,
    measurements = "wall-clock latency and process-wide managed allocation delta"
}));

using CassieConnection connection = new() { ConnectionString = unpooledSettings.ConnectionString };
await connection.OpenAsync().ConfigureAwait(false);

await MeasureAsync("simple_query_scalar", iterations, warmupIterations, async () =>
{
    using DbCommand command = connection.CreateCommand();
    command.CommandText = "SELECT 42::int4";
    using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 42)
    {
        throw new InvalidOperationException("The simple-query benchmark returned an unexpected result.");
    }
}).ConfigureAwait(false);

await MeasureAsync("extended_binary_parameter", iterations, warmupIterations, async () =>
{
    using CassieCommand command = (CassieCommand)connection.CreateCommand();
    command.BinaryResults = true;
    command.CommandText = "SELECT @value::int4";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 42 });
    using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 42)
    {
        throw new InvalidOperationException("The extended-protocol benchmark returned an unexpected result.");
    }
}).ConfigureAwait(false);

await MeasureAsync("multi_result_query", iterations, warmupIterations, async () =>
{
    using DbCommand command = connection.CreateCommand();
    command.CommandText = "SELECT 41::int4; SELECT 42::int4";
    using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 41
        || !await reader.NextResultAsync().ConfigureAwait(false)
        || !await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 42
        || await reader.NextResultAsync().ConfigureAwait(false))
    {
        throw new InvalidOperationException("The multi-result benchmark returned unexpected results.");
    }
}).ConfigureAwait(false);

await MeasureAsync("unpooled_open_close", Math.Max(10, iterations / 5), Math.Max(2, warmupIterations / 5), async () =>
{
    using CassieConnection fresh = new() { ConnectionString = unpooledSettings.ConnectionString };
    await fresh.OpenAsync().ConfigureAwait(false);
    await fresh.CloseAsync().ConfigureAwait(false);
}).ConfigureAwait(false);

await MeasureAsync("pooled_open_close", Math.Max(10, iterations / 5), Math.Max(2, warmupIterations / 5), async () =>
{
    using CassieConnection pooled = new() { ConnectionString = pooledSettings.ConnectionString };
    await pooled.OpenAsync().ConfigureAwait(false);
    await pooled.CloseAsync().ConfigureAwait(false);
}).ConfigureAwait(false);

await connection.CloseAsync().ConfigureAwait(false);
CassieConnection.ClearAllPools();

static int ReadPositiveInteger(string name, int fallback)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return value is null
        ? fallback
        : int.TryParse(value, out int parsed) && parsed is > 0 and <= 1_000_000
            ? parsed
            : throw new ArgumentException($"{name} must be between 1 and 1000000.", name);
}

static async Task MeasureAsync(string name, int count, int warmupCount, Func<Task> operation)
{
    for (int i = 0; i < warmupCount; i++)
    {
        await operation().ConfigureAwait(false);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    double[] samples = new double[count];
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    long started = Stopwatch.GetTimestamp();
    for (int i = 0; i < count; i++)
    {
        long operationStarted = Stopwatch.GetTimestamp();
        await operation().ConfigureAwait(false);
        samples[i] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
    }

    double elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Array.Sort(samples);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        record = "result",
        benchmark = name,
        iterations = count,
        mean_ms = samples.Average(),
        p50_ms = Percentile(samples, 0.50),
        p95_ms = Percentile(samples, 0.95),
        p99_ms = Percentile(samples, 0.99),
        operations_per_second = count / elapsedSeconds,
        managed_alloc_bytes_per_operation = (double)allocatedBytes / count
    }));
}

static double Percentile(double[] sortedSamples, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(percentile * sortedSamples.Length) - 1, 0, sortedSamples.Length - 1);
    return sortedSamples[index];
}
