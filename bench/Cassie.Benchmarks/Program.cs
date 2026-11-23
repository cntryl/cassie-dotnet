using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Rest;

var connectionString = Environment.GetEnvironmentVariable("CASSIE_DOTNET_BENCHMARK_CONNECTION_STRING")
                       ?? throw new InvalidOperationException(
                           "Set CASSIE_DOTNET_BENCHMARK_CONNECTION_STRING to a Cassie connection string.");
var serverRevision = Environment.GetEnvironmentVariable("CASSIE_SERVER_REVISION")
                     ?? throw new InvalidOperationException(
                         "Set CASSIE_SERVER_REVISION to the exact 40-character Cassie commit being measured.");
if (serverRevision.Length != 40 || !serverRevision.All(Uri.IsHexDigit))
{
    throw new InvalidOperationException("CASSIE_SERVER_REVISION must be a 40-character hexadecimal commit.");
}

var iterations = ReadPositiveInteger("CASSIE_DOTNET_BENCHMARK_ITERATIONS", 500);
var warmupIterations = ReadPositiveInteger("CASSIE_DOTNET_BENCHMARK_WARMUP", 50);
var providerRevision = Environment.GetEnvironmentVariable("CASSIE_DOTNET_PROVIDER_REVISION") ?? "unspecified";
var providerTreeState = Environment.GetEnvironmentVariable("CASSIE_DOTNET_PROVIDER_TREE_STATE") ?? "unspecified";
var serverProfile = Environment.GetEnvironmentVariable("CASSIE_DOTNET_BENCHMARK_PROFILE") ?? "unspecified";
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
    using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 42)
    {
        throw new InvalidOperationException("The simple-query benchmark returned an unexpected result.");
    }
}).ConfigureAwait(false);

await MeasureAsync("extended_binary_parameter", iterations, warmupIterations, async () =>
{
    using var command = connection.CreateCommand();
    command.BinaryResults = true;
    command.CommandText = "SELECT @value::int4";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 42 });
    using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 42)
    {
        throw new InvalidOperationException("The extended-protocol benchmark returned an unexpected result.");
    }
}).ConfigureAwait(false);

await MeasureAsync("multi_result_query", iterations, warmupIterations, async () =>
{
    using DbCommand command = connection.CreateCommand();
    command.CommandText = "SELECT 41::int4; SELECT 42::int4";
    using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

    if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetInt32(0) != 41
                                                        || !await reader.NextResultAsync().ConfigureAwait(false)
                                                        || !await reader.ReadAsync().ConfigureAwait(false) ||
                                                        reader.GetInt32(0) != 42
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

await MeasureAsync("create_command_and_batch", Math.Max(iterations, 10_000), warmupIterations, () =>
{
    using var command = connection.CreateCommand();
    using var batch = connection.CreateBatch();
    return command.CommandTimeout == unpooledSettings.CommandTimeout && batch.Timeout == unpooledSettings.CommandTimeout
        ? Task.CompletedTask
        : throw new InvalidOperationException("The provider did not apply the configured timeout.");
}).ConfigureAwait(false);

await MeasureAsync("read_1024_rows", iterations, warmupIterations, async () =>
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT generate_series::int4 FROM generate_series(1, 1024)";
    var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    await using (reader.ConfigureAwait(false))
    {
        var count = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != ++count)
            {
                throw new InvalidOperationException("The row benchmark returned an unexpected value.");
            }
        }


        if (count != 1024)
        {
            throw new InvalidOperationException("The row benchmark returned an unexpected number of rows.");
        }
    }
}).ConfigureAwait(false);

using var copy = connection.CreateCommand();
using var copySetup = connection.CreateCommand();
copySetup.CommandText = "CREATE TABLE cassie_dotnet_bench_copy (value TEXT)";
_ = await copySetup.ExecuteNonQueryAsync().ConfigureAwait(false);
try
{
    copySetup.CommandText = "TRUNCATE cassie_dotnet_bench_copy";
    copy.CommandText = "COPY cassie_dotnet_bench_copy FROM STDIN";
    var line = new string('a', 32_768) + "\n";
    using MemoryStream input = new(Encoding.UTF8.GetBytes(line + line + line + line), false);
    await MeasureAsync("copy_from_128_kib", iterations, warmupIterations, async () =>
    {
        if (await copy.CopyFromAsync(input).ConfigureAwait(false) != 4)
        {
            throw new InvalidOperationException("The COPY benchmark returned an unexpected row count.");
        }
    }, async () =>
    {
        _ = await copySetup.ExecuteNonQueryAsync().ConfigureAwait(false);
        input.Position = 0;
    }).ConfigureAwait(false);
}
finally
{
    copySetup.CommandText = "DROP TABLE cassie_dotnet_bench_copy";
    _ = await copySetup.ExecuteNonQueryAsync().ConfigureAwait(false);
}

using var query = connection.CreateCommand();
query.CommandText = "SELECT 42::int4";
DbDataReader? activeReader = null;
await MeasureAsync("close_64_deferred_statements", Math.Max(3, iterations / 10),
    Math.Max(1, warmupIterations / 10), async () =>
    {
        var reader = activeReader ?? throw new InvalidOperationException("No benchmark reader is active.");
        activeReader = null;
        await reader.DisposeAsync().ConfigureAwait(false);
    }, async () =>
    {
        var prepared = new CassieCommand[64];

        for (var index = 0; index < prepared.Length; index++)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT 42::int4";
            prepared[index] = command;
            await command.PrepareAsync().ConfigureAwait(false);
        }


        activeReader = await query.ExecuteReaderAsync().ConfigureAwait(false);

        foreach (var command in prepared)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }


        if (!await activeReader.ReadAsync().ConfigureAwait(false) || activeReader.GetInt32(0) != 42)
        {
            throw new InvalidOperationException("The deferred cleanup benchmark returned an unexpected row.");
        }
    }).ConfigureAwait(false);

if (Environment.GetEnvironmentVariable("CASSIE_DOTNET_BENCHMARK_REST_BASE_ADDRESS") is { } restAddress)
{
    using CassieRestClient rest = new(new Uri(restAddress));
    _ = await rest.LoginAsync(unpooledSettings.Username, unpooledSettings.Password).ConfigureAwait(false);
    var request = new CassieAdminQueryRequest(unpooledSettings.Database,
        "SELECT repeat('x', 128) AS payload FROM generate_series(1, 256)");
    await MeasureAsync("rest_query_256_rows", iterations, warmupIterations, async () =>
    {
        var result = await rest.Admin.ExecuteQueryAsync(request).ConfigureAwait(false);

        if (result.Rows.Count != 256 || result.Rows[0][0].GetString()?.Length != 128)
        {
            throw new InvalidOperationException("The REST benchmark returned an unexpected result.");
        }
    }).ConfigureAwait(false);
}

await connection.CloseAsync().ConfigureAwait(false);
CassieConnection.ClearAllPools();

static int ReadPositiveInteger(string name, int fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return value is null
        ? fallback
        : int.TryParse(value, out var parsed) && parsed is > 0 and <= 1_000_000
            ? parsed
            : throw new ArgumentException($"{name} must be between 1 and 1000000.", name);
}

static async Task MeasureAsync(
    string name,
    int count,
    int warmupCount,
    Func<Task> operation,
    Func<Task>? setup = null
)
{
    for (var i = 0; i < warmupCount; i++)
    {
        if (setup is not null)
        {
            await setup().ConfigureAwait(false);
        }

        await operation().ConfigureAwait(false);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var samples = new double[count];
    var allocatedBytes = 0L;

    for (var i = 0; i < count; i++)
    {
        if (setup is not null)
        {
            await setup().ConfigureAwait(false);
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var operationStarted = Stopwatch.GetTimestamp();
        await operation().ConfigureAwait(false);
        samples[i] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        allocatedBytes += GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    }


    var elapsedSeconds = samples.Sum() / 1000;
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
    var index = Math.Clamp((int)Math.Ceiling(percentile * sortedSamples.Length) - 1, 0, sortedSamples.Length - 1);
    return sortedSamples[index];
}
