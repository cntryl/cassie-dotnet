# Live qualification

This console harness checks binary scalar, exact NUMERIC (including `UInt64`),
`DbType.Date` with `DateTime`, wall-clock `timestamp` parameters in text and
binary formats, typed `GetDateTime` and `GetFieldValue<DateTime>` reads of
`date`, `timestamp`, and `timestamptz` in text and binary result formats,
`TIME` with `TimeSpan`, inet/cidr, MAC,
bit-string, LSN, vector, OID/`reg*` parameter/result codecs, and explicit text
parameter formats for network, MAC, bit strings, LSN, interval, `timetz`, and
vector values. Its opt-in one-dimensional array probes also cover `UInt64[]`
as `numeric[]` and `TimeSpan[]` as `time[]`. It checks OID 705 `unknown` metadata and SQL NULL results,
optional `void` text and prepared binary results plus `record` and `record[]`
metadata and text values,
optional registered-array workflows including text `inet[]` input, empty-result `HasRows`,
`GetSchema` table/column discovery, binary and text `name`/`name[]` parameters,
parameterized `CommandBehavior.SchemaOnly` metadata with a no-write DML probe,
server-side `PrepareAsync` with repeated execution and changed parameter values,
`DEALLOCATE ALL`, reconnect/reprepare, and connection reuse after a prepare error,
the Stable one-statement `CALL` workflow through SQL text and
`CommandType.StoredProcedure` with bound positional input, including server
preparation, repeated execution, and changed parameter values,
`CommandBehavior.SingleRow`/`SingleResult` limits and connection reuse,
`CommandBehavior.CloseConnection` reader ownership,
`GetSchemaTable`/`GetColumnSchema` field metadata,
`GetStream`/`GetTextReader` access to bytea and text values,
parameterized extended-query portal paging through a 260-row result,
`CassieDataAdapter.Fill` into `DataTable` and `DataSet`, `CassieDataAdapter.Update`
with a parameterized insert command, one-dimensional array result/parameter
round trips (opt-in), multidimensional/lower-bound array round trips in binary
and text input formats (opt-in), streaming CSV COPY, database-image backup and
restore, transaction rollback,
pool reset, pool-wait cancellation, pool separation across different startup
`Options`, and active query cancellation with session reuse against a live
Cassie revision. Use a fresh disposable server for each
run: the harness drops and recreates `cassie_dotnet_live_probe`, and the image
round trip creates a unique restored database that the current stable server
cannot drop while it contains restored catalog objects.

Set `CASSIE_DOTNET_TEST_ARRAYS=true` to run one-dimensional array result and
parameter round trips. Set `CASSIE_DOTNET_TEST_ARRAY_SHAPES=true` to run the
array-shape round trips.
The pinned feature revision passes the one-dimensional array probes. Current
mainline accepts the text `inet[]` bind probe but fails the `int4[]` binary
result probe with `cannot cast scalar value to ARRAY`; keep the broader array
matrix opt-in until that server path is repaired.
The pinned Cassie revision `db028e02b850c05b5294abe28783b2ace799d59e` currently
fails its own multidimensional binary and text array pgwire tests with SQLSTATE
`08P01` (`invalid binary/text array parameter`), so this live slice is opt-in
until the server accepts those documented forms. Provider frame-level tests
cover both encodings independently.

Set `CASSIE_DOTNET_TEST_NAME_TYPE=true` to run the `name`/`name[]` probes. They
require a Cassie revision that supports `name`; the current `main` type contract
still lists that type as pending, while the pinned feature revision supports it.

Set `CASSIE_DOTNET_TEST_JSONPATH_TYPE=true` to run the JSONPath type probe. The
current mainline revision does not register JSONPath; the pinned feature revision
does.

Set `CASSIE_DOTNET_TEST_JSONB_TYPE=true` to run the distinct JSONB OID 3802
binary parameter/result probe. The current mainline treats JSONB as the JSON
OID 114 alias, while the pinned feature revision registers distinct JSONB.

Set `CASSIE_DOTNET_TEST_RECORD_TYPES=true` to verify the selected experimental
`pg_sleep` `void` result in text format and as a prepared binary result, plus
CTE `SEARCH`/`CYCLE` `record`/`record[]` metadata and text values.

Set `CASSIE_DOTNET_TEST_PARAMETER_STATUS_UPDATES=true` to verify that a
`DateStyle` change hidden inside a stored view is applied to the next query.
This probe requires a server revision that emits changed `ParameterStatus`
messages after statement results.

Set `CASSIE_DOTNET_TEST_CONNECTION_STRING` to the server's connection string,
then run:

```sh
dotnet run --project test/Cassie.LiveQualification/Cassie.LiveQualification.csproj
```

To verify startup `Options` against the live server, include
`Options=-c application_name=options_probe` in that connection string and set
`CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME=options_probe`.

Record the Cassie commit used for each qualification run. A passing run only
qualifies the workflows exercised here; it does not establish full product,
release, or production qualification.

## Recorded pinned runs

On 2026-10-27, the managed harness and the Native AOT executable both passed
against mainline `33244d6f9b00f09aecb4e603e520eff7525fc3a7` with the core probes
enabled. These include active cancellation with session reuse and database-image
backup/restore. The managed and Native AOT runs against feature revision
`db028e02b850c05b5294abe28783b2ace799d59e` also passed with `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE` enabled.

These runs used the provider working tree based on `d45abd1` after its async
disposal, cancellation, query activity, and connection-state event changes. The
provider tree was dirty, so record this as working-tree qualification rather
than an immutable provider commit result. Each managed or Native AOT run used a
fresh in-memory server process. The full Release test suite also passed on
2026-10-27: 122 passed, 0 failed, 0 skipped.

The same harness was also built against the local `Cassie.1.0.0.nupkg` instead
of a project reference. Managed and Native AOT core probes passed against
mainline; managed and Native AOT runs passed against the feature revision with
`ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE` enabled. Each run used
a fresh in-memory server. This package was built from the dirty provider tree
based on `d45abd1`, and its nuspec records that base commit. These runs validate
local package consumption, not an immutable release package.

On 2026-10-27, after cancellation reads were changed to finish the current
pgwire frame before draining, `SingleResult` drains were taught to propagate
cancellation, and canceled transaction starts gained rollback recovery, the
local package was repacked from the working tree based on `d45abd1`. The managed
package consumer passed against mainline; the Native AOT consumer passed
against the feature revision with `ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, and
`JSONB_TYPE` enabled. Both consumer processes exited successfully, and each
used a fresh server data directory. These runs qualify the updated dirty
package artifact, not an immutable provider commit. The final Release suite
passed with 125 tests, no failures, and no skips.

The additional array probes remain limited by the pinned server behavior:

- Mainline accepts the text `inet[]` parameter probe, then fails the `int4[]`
  binary result probe with `cannot cast scalar value to ARRAY`.
- The feature revision rejects the multidimensional text-array operation with
  `invalid text array parameter`; the full shape round trip does not pass.

Each recorded run used a fresh disposable in-memory server because restore
leaves a unique database that the stable server cannot currently drop.

On 2026-10-28, the updated project-reference harness and the refreshed local
`Cassie.1.0.0.nupkg` consumer passed in managed and Native AOT modes against
feature revision `db028e02b850c05b5294abe28783b2ace799d59e`. Both runs enabled
the core probes plus `ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE`;
the procedure section exercised SQL-text `CALL` and
`CommandType.StoredProcedure` with positional input binding, server preparation,
and repeated execution after changing the parameter value. Each mode used a
fresh in-memory server. The package was built from the dirty provider tree
based on `d45abd1`, so this qualifies that local artifact rather than an
immutable release package. The full Release suite passed with 131 tests, no
failures, and no skips.

After adding a scripted regression and provider conversion for
`GetFieldValueAsync<decimal>` over NUMERIC, the Release suite passed with 132
tests, no failures, and no skips. The managed project-reference harness then
passed against feature revision `db028e02b850c05b5294abe28783b2ace799d59e`
with `ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE` enabled; its
binary NUMERIC probe now also reads the value through `GetFieldValueAsync<decimal>`.
This validates the dirty provider working tree based on `d45abd1`, not a
packaged or immutable provider revision.

The reader audit added `CommandBehavior.SequentialAccess` column-order checks
to the live harness: backward access fails after moving to a later field, and
the next row starts again at its first field. Scripted coverage also verifies
the protocol-independent reader contract. Reader fields remain buffered within
the server's pgwire frame limit; `SequentialAccess` does not stream individual
field bytes directly from the socket.

On 2026-10-28, the managed project-reference harness and its macOS ARM64 Native
AOT executable passed against feature revision
`db028e02b850c05b5294abe28783b2ace799d59e` with `ARRAYS`, `NAME_TYPE`,
`JSONPATH_TYPE`, and `JSONB_TYPE` enabled. Both runs included the NUMERIC
`GetFieldValue<decimal>` and `SequentialAccess` probes and used a fresh
in-memory server. The provider working tree was dirty and based on `d45abd1`,
so this records local working-tree qualification, not an immutable provider
revision or release package.

The same provider source state also passed the managed and macOS ARM64 Native
AOT core harness against mainline snapshot
`33244d6f9b00f09aecb4e603e520eff7525fc3a7`. Both runs included the NUMERIC
generic-decimal and `SequentialAccess` probes. The feature-only `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE` probes were disabled; each run
used a fresh in-memory server. This qualifies the dirty provider working tree
based on `d45abd1`, not an immutable provider revision or release package.

The provider was then changed to fetch extended-query results in 128-row portal
pages. The managed and macOS ARM64 Native AOT harnesses passed against mainline
snapshot `33244d6f9b00f09aecb4e603e520eff7525fc3a7`, and against feature
revision `db028e02b850c05b5294abe28783b2ace799d59e` with `ARRAYS`, `NAME_TYPE`,
`JSONPATH_TYPE`, and `JSONB_TYPE` enabled. Each run used a fresh in-memory
server and exercised the 260-row portal paging probe. The Release suite passed
with 136 tests, no failures, and no skips. These runs qualify the dirty provider
working tree based on `d45abd1`, not an immutable provider revision or release
package.

To qualify TLS, run the same harness against a TLS-enabled listener and use a
connection string with `SSL Mode=Require;SSL Root Certificate=/path/to/ca.pem`.
The host in the connection string must match the certificate name. This runs
the full harness through the TLS transport without modifying the machine trust
store. Set `CASSIE_DOTNET_TEST_REJECTED_TLS_CONNECTION_STRING` to the same
endpoint with a mismatched host name to verify that peer identity validation
rejects it.

To publish the same consumer as a Native AOT executable on macOS ARM64:

```sh
dotnet publish test/Cassie.LiveQualification/Cassie.LiveQualification.csproj \
  --configuration Release --runtime osx-arm64 -p:PublishAot=true -p:StripSymbols=true
```

Run the published executable with the same environment variable to qualify the
trimmed native consumer against that server revision.

On 2026-10-28, after adding the `void` (OID 2278), `record` (OID 2249), and
`record[]` (OID 2287) data-type-name mappings and their live probes, the managed
project-reference harness and freshly published macOS ARM64 Native AOT
executable passed against clean mainline revision
`33244d6f9b00f09aecb4e603e520eff7525fc3a7` with `RECORD_TYPES` enabled. Both
also passed against clean feature revision
`db028e02b850c05b5294abe28783b2ace799d59e` with `ARRAYS`, `NAME_TYPE`,
`JSONPATH_TYPE`, `JSONB_TYPE`, and `RECORD_TYPES` enabled. Each run used a fresh
in-memory server and exited successfully. The full Release suite passed with
141 tests, no failures, and no skips. These results qualify the dirty provider
source based on `d45abd1`; they do not qualify multidimensional arrays, an
immutable provider revision or package, or a release.

On 2026-10-28, the refreshed `Cassie.1.0.0.nupkg` was restored from an isolated
NuGet cache into a consumer and published as a macOS ARM64 Native AOT executable.
That packaged consumer passed against mainline revision
`33244d6f9b00f09aecb4e603e520eff7525fc3a7` with `RECORD_TYPES` enabled and
against feature revision `db028e02b850c05b5294abe28783b2ace799d59e` with
`ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, `JSONB_TYPE`, and `RECORD_TYPES` enabled.
Each run used a fresh in-memory server. The package was built from the dirty
provider tree based on `d45abd1`, so this qualifies that local package artifact,
not an immutable provider commit or release.

After adding support for Cassie's prepared binary `void` result, a freshly
packed `Cassie.1.0.0.nupkg` consumer published as a macOS ARM64 Native AOT
executable passed against both pinned revisions with the binary `void` probe
enabled. The feature run also enabled `ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`,
and `JSONB_TYPE`. The full Release suite passed with 143 tests, no failures, and
no skips. This remains qualification of the dirty provider source based on
`d45abd1` and its local package artifact.

On 2026-10-28, a .NET type-mapping audit added binary encodings for `byte`,
`sbyte`, `ushort`, and the full `ulong` range as Cassie integer/OID/NUMERIC
inputs; CLR arrays now infer `int2[]`, `int4[]`, and `numeric[]` for the matching
narrow and unsigned integer carriers. `TimeSpan` binds to `TIME` in text or
binary format when it is nonnegative and below 24 hours, with 100-nanosecond
ticks truncated to the wire's microsecond precision; `GetFieldValue<TimeSpan>`
converts returned `TIME` values. `DbType.Date` accepts `DateTime` and sends only
the calendar date. Red-green scripted regressions cover these paths. The
Release suite passed with 156 tests, no failures, and no skips; the live harness
built with zero warnings; and `dotnet format --verify-no-changes` passed.
Managed and Native AOT harnesses passed against mainline
`33244d6f9b00f09aecb4e603e520eff7525fc3a7` with `RECORD_TYPES`, and against
feature revision `db028e02b850c05b5294abe28783b2ace799d59e` with `RECORD_TYPES`,
`ARRAYS`, `NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE`. Both used fresh
in-memory servers. This qualifies the dirty provider source based on `d45abd1`;
immutable provider, release, multidimensional array, and production performance
qualification remain open.

A follow-on NUMERIC reader regression added `GetFieldValue<ulong>` conversion
for nonnegative integral values through `ulong.MaxValue`, while keeping
`GetValue` lossless as `CassieNumeric`; fractional and out-of-range values are
rejected. Text and binary result regressions cover the maximum value; binary
tests also cover fractional and overflow boundaries. The full Release suite
passed with 160 tests, no failures, and no skips. After republishing the Native
AOT consumer, managed and Native AOT harnesses both passed against mainline
`33244d6f9b00f09aecb4e603e520eff7525fc3a7`
with `RECORD_TYPES`, and against feature revision
`db028e02b850c05b5294abe28783b2ace799d59e` with `RECORD_TYPES`, `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, and `JSONB_TYPE`. Each run used a fresh in-memory
server. This qualifies the dirty provider source based on `d45abd1`; immutable
provider, release, multidimensional array, and production performance
qualification remain open.

On 2026-10-28, after the array shape and upper-bound corrections, the current
provider source was packed locally as `Cassie.1.0.1.nupkg` to force a fresh
package-reference restore. The package consumer built with warnings as errors
and published as macOS ARM64 Native AOT. It passed the full mainline harness
against `33244d6f9b00f09aecb4e603e520eff7525fc3a7` with `RECORD_TYPES` and
startup `Options` probes, and the full feature harness against
`db028e02b850c05b5294abe28783b2ace799d59e` with one-dimensional `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, `JSONB_TYPE`, `RECORD_TYPES`, and startup `Options`
probes. Each used a fresh in-memory server. Multidimensional array probes were
disabled because the pinned Cassie server rejects its own documented pgwire
forms; the provider's shape and bound edge cases remain covered by scripted
tests. The Release suite passed with 184 tests, no failures, and no skips.
This qualifies the dirty provider source based on `d45abd1` and its local
package, not an immutable provider commit or release.

The temporal-value qualification now probes infinity sentinels, a finite date
and timestamp outside the .NET calendar range, and `24:00` for `time` and
`timetz`. The provider preserves ordinary CLR mappings, exposes tagged
`CassieTemporalValue` results for values they cannot represent, and decodes
text `timetz` as `CassieTimeWithTimeZone`. The managed and macOS ARM64 Native
AOT project-reference harnesses both passed against the clean feature snapshot
`db028e02b850c05b5294abe28783b2ace799d59e`, with one-dimensional `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, `JSONB_TYPE`, and `RECORD_TYPES` probes enabled.
Each run used a fresh in-memory server. The updated tree was packed as local
`Cassie.1.0.2.nupkg`, restored into a warnings-as-errors consumer, published
for macOS ARM64 Native AOT, and passed the same feature harness against another
fresh in-memory server. The same package AOT consumer also passed against clean
mainline `33244d6f9b00f09aecb4e603e520eff7525fc3a7` with temporal probes and
`RECORD_TYPES` enabled, using fresh in-memory storage. These runs qualify the
dirty provider source based on `d45abd1` and that local package, not an
immutable provider commit or release.

On 2026-10-28, the harness added a live `SET DateStyle = 'SQL, DMY'` probe and
verified text `date` and `timestamp` results after the provider refreshed the
setting with `SHOW DateStyle`. Managed and macOS ARM64 Native AOT
project-reference runs passed against clean feature revision
`db028e02b850c05b5294abe28783b2ace799d59e`, with one-dimensional `ARRAYS`,
`NAME_TYPE`, `JSONPATH_TYPE`, `JSONB_TYPE`, `RECORD_TYPES`, and startup `Options`
probes enabled. Each run used a fresh in-memory server. This qualifies the
dirty provider working tree based on `d45abd1`, not a package, immutable
provider commit, or release.

On 2026-10-28, the updated provider tree's managed harness and macOS ARM64
Native AOT executable passed against the local server build from the dirty
Cassie tree based on `db028e02b850c05b5294abe28783b2ace799d59e`. Both runs
enabled `CASSIE_DOTNET_TEST_PARAMETER_STATUS_UPDATES=true` and passed the
stored-view `DateStyle` update probe along with the core workflows. Each run
used a fresh in-memory server. The provider tree was dirty and based on
`d45abd1ddda8d4609a7cc414cdc4ea5cdc2de1ae`; this qualifies the working trees,
not an immutable provider package or release.

On 2026-10-28, the final provider working tree was rechecked against clean,
pinned server worktrees. The managed harness passed against mainline
`33244d6f9b00f09aecb4e603e520eff7525fc3a7` with `RECORD_TYPES` and startup
`Options` enabled. It passed against feature revision
`db028e02b850c05b5294abe28783b2ace799d59e` with `ARRAYS`, `NAME_TYPE`,
`JSONPATH_TYPE`, `JSONB_TYPE`, `RECORD_TYPES`, and startup `Options` enabled.
The current macOS ARM64 Native AOT consumer also passed that feature matrix.
Every run used a fresh in-memory server. The opt-in post-statement
`ParameterStatus` probe remains qualified against the adjacent dirty Cassie
tree only, because these pinned server revisions do not emit those updates.
This evidence qualifies the dirty provider source based on
`d45abd1ddda8d4609a7cc414cdc4ea5cdc2de1ae`, not an immutable provider package
or release. The server's own multidimensional-array pgwire failures and
unqualified temporal-array semantics remain upstream limits.
