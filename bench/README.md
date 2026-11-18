# Cassie provider benchmarks

The benchmark executable has no runtime benchmark-framework dependency. It
measures simple and extended queries, multiple results, pooled and unpooled
connection open and close, command and batch creation, 1,024 row reads, COPY
input, and deferred prepared statement cleanup. Every workload checks its
result before recording a sample. Set
`CASSIE_DOTNET_BENCHMARK_REST_BASE_ADDRESS` to include a REST admin query with
256 response rows. The REST login uses the connection string credentials.

Run it against an isolated Cassie server with a recorded immutable server
revision:

```sh
CASSIE_DOTNET_BENCHMARK_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Username=root;Password=...;Database=postgres;SSL Mode=Disable' \
CASSIE_SERVER_REVISION='<40-character Cassie commit>' \
CASSIE_DOTNET_PROVIDER_REVISION='<provider commit>' \
CASSIE_DOTNET_PROVIDER_TREE_STATE='clean' \
CASSIE_DOTNET_BENCHMARK_PROFILE='memory-loopback' \
dotnet run --project bench/Cassie.Benchmarks/Cassie.Benchmarks.csproj --configuration Release
```

Do not include the connection string or credentials in retained output. The
executable emits newline-delimited JSON: one manifest followed by one result
record per workload. The manifest records UTC time, server and provider revisions,
the server profile label, runtime, host OS and architectures, processor count,
TLS mode, connection timeout, and sample counts. `CASSIE_DOTNET_BENCHMARK_PROFILE`
is a non-secret label such as `memory-loopback` or a named disk-backed profile.
Set `CASSIE_DOTNET_BENCHMARK_ITERATIONS` and
`CASSIE_DOTNET_BENCHMARK_WARMUP` to adjust sample counts. Defaults are 500
measured and 50 warmup operations; connection lifecycle workloads use one fifth
of those counts, with a minimum of 10 measured and 2 warmup operations. The
command and batch creation workload measures at least 10,000 operations.
The deferred statement cleanup workload uses one tenth of the query counts,
with a minimum of three measured and one warmup operation. COPY table
truncation and prepared statement setup occur outside the samples. Run against
a disposable database where the benchmark user can create a table.

Results include mean, p50, p95 and p99 latency, throughput, and process-wide
managed allocation delta per operation. Allocation includes the measurement
loop and provider process activity, so compare runs using the same runtime,
host, server revision, provider revision, iteration counts, TLS mode, and
connection settings. A single run is an observation, not a production
performance qualification; retain repeated runs and the full manifest.

The [2026-10-28 UTC loopback observation](results/2026-10-27-mainline-memory-loopback.jsonl)
passed all five workload result checks against mainline `33244d6f9b00f09aecb4e603e520eff7525fc3a7`.
It used an in-memory server, 500 query samples and 100 connection-lifecycle samples on
macOS ARM64 with TLS disabled. The provider tree was dirty at `d45abd1`; this is a
smoke measurement and does not establish a reproducible or production baseline.

Three additional consecutive runs using the same server revision, provider tree,
host, profile, and sample counts are retained in
[`2026-10-28-mainline-memory-loopback-repeats.jsonl`](results/2026-10-28-mainline-memory-loopback-repeats.jsonl).
All five result checks passed in every run. Mean latency ranges were 1.33–1.43 ms
for simple scalar queries, 1.07–1.14 ms for extended binary parameters,
2.46–2.50 ms for multi-result queries, 141.88–142.52 ms for unpooled open/close,
and 0.14–0.15 ms for pooled open/close. This repeated loopback observation still
has no production target or comparative baseline and does not establish
production performance.

On 2026-10-28, three more consecutive runs repeated the same host, server and
provider revisions, profile, 3-second connection timeout, TLS mode and sample
counts. All five result checks passed each time. Mean latencies ranged from
1.30–1.33 ms for simple scalar queries, 1.03–1.04 ms for extended binary
parameters, 2.41–2.48 ms for multi-result queries, 140.61–159.05 ms for
unpooled open/close, and 0.14–0.18 ms for pooled open/close. The unpooled
open/close medians stayed within 140.36–144.91 ms, but two runs had long-tail
outliers; the raw records are appended to the repeat JSONL file. These are
still memory-loopback observations and do not establish production
performance.

### Provider allocation optimization (2026-10-28)

Three baseline runs and three optimized runs used the same provider base
revision, Cassie server revision, .NET 11 runtime, macOS ARM64 host, in-memory
loopback profile, TLS setting, timeout, and sample counts. Every one of the
five workload result checks passed in every run. The optimized working tree
adds command parameter fast paths, direct query-read paths, and pooled small
outbound protocol frames. Mean managed allocations per operation were:

| Workload | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Simple scalar query | 5,714 B | 3,129 B | -45% |
| Extended binary parameter | 11,136 B | 7,250 B | -35% |
| Multi-result query | 8,513 B | 4,370 B | -49% |
| Unpooled open/close | 13,731 B | 13,709 B | -0.2% |
| Pooled open/close | 9,089 B | 7,912 B | -13% |

The final runs showed substantial unrelated CPU contention, and their latency
measurements were higher and noisier than baseline. Treat the allocation
reductions as the measured result; latency needs a quiet-host repeat before
claiming an improvement. These are loopback measurements, not production
performance qualification. Raw baseline and optimized manifests and results
are retained in
[`2026-10-28-provider-baseline-memory-loopback.jsonl`](results/2026-10-28-provider-baseline-memory-loopback.jsonl)
and
[`2026-10-28-provider-after-outbound-frame-pool-memory-loopback.jsonl`](results/2026-10-28-provider-after-outbound-frame-pool-memory-loopback.jsonl).

### Paired correctness and performance validation (2026-11-18)

Three alternating Release pairs compared provider `4e7b47b` with
`24ff7ea` using the same harness, .NET 11.0.0 on macOS ARM64, and one
in-memory Cassie server at revision `ccc96144afac024cbf4430b22425a0ef0d53db21`.
The server image digest was
`sha256:c8f531b4c8fec327ae1b2b9ad27c49a0fa1fbcf7867888b39d43794c1e2b2142`.
Each pass used 30 query samples and eight warmups, 10,000 command and batch
creation samples, and three measured groups of 64 deferred closes. The table
reports the median allocation per operation across the three passes.

| Workload | Style baseline | Updated provider | Change |
| --- | ---: | ---: | ---: |
| Create a command and batch | 6,824 B | 328 B | -95.2% |
| Read 1,024 rows | 47,406 B | 4,318 B | -90.9% |
| COPY 128 KiB | 331,113 B | 3,189 B | -99.0% |
| Close 64 deferred statements | 103,595 B | 50,904 B | -50.9% |
| Read a 256-row REST response | 178,943 B | 145,036 B | -18.9% |

The median of the per-pass p50s for closing 64 deferred statements went from
2,698.9 ms to 50.1 ms. A scripted peer also verified that
all pairs in a group arrive before it sends the first acknowledgement.
A REST response test used content that fails on buffering and succeeds
through the response stream. These measurements cover the full provider change
on the local Docker loopback connection.

[Raw benchmark manifests and measurements](results/2026-11-18-correctness-performance-paired.jsonl)
