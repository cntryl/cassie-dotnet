# SOLID audit

Reviewed on 2026-10-29. The audit began against the provider working tree based on `f31a02f`; the four findings below have been remediated in the resulting changes. This document separates implemented ownership boundaries from the checks needed to qualify the final source, package, and hosted workflow.

## Compatibility boundary

The namespace reorganization is an intentional **source and binary breaking API change**. For example, `Cntryl.Cassie.CassieConnection` becomes `Cntryl.Cassie.Connection.CassieConnection`; commands, readers, diagnostics, and public value types likewise move into their corresponding namespaces. Existing consumers need updated imports and a rebuild because namespaces form part of CLR type identity. `CassieProviderFactory` and `CassieException` remain in `Cntryl.Cassie`.

Keeping the same project and package does not preserve the old type identities. These changes provide no compatibility shims. Publication, versioning, and downstream migration remain separate from implementing and merging the refactor.

Other intentional behavior differences are strict startup `ParameterStatus` parsing, affected-row count parsing with `NumberStyles.None`, revised COPY error wording, and the shared missing-message fallback (`Cassie returned an error without a message.`). Local parameter errors now preserve a healthy session and transaction; COPY recovery now has one five-second completion budget.

## Ownership boundaries

| Area | Owner | Responsibility and limit |
| --- | --- | --- |
| Wire format | `Protocol/PgFraming`, `PgPayload`, `PgWriter`, `SessionMessages`, `ExtendedQueryMessages`, `RowMessages`, `ServerErrorParser`, `CommandTag` | Frames, packet construction, payload parsing, and command tags can be verified without an ADO.NET connection. |
| Physical connection | `Connection/PgSession` and `PgSessionConnector` | Socket/stream ownership, authentication, backend identity, timestamps, and session settings travel together through pooling. |
| TLS and cancel transport | `Connection/TlsNegotiator` and `QueryCanceler` | TLS negotiation and the separate cancellation connection have explicit owners. The caller supplies the recovery deadline when canceling COPY. |
| Session settings | `Connection/DateStyleTracker` | Setting invalidation and transaction-state transitions are independent of the wire. SQL function detection shares the command lexer. |
| Reader dependency | `Data/IReaderSession` and `Connection/ConnectionReaderSession` | The reader consumes borrowed session operations; the connection retains physical-session and pool ownership. |
| Reader cancellation | `Data/ReaderCancellation` | Timer lifetime, cancel deduplication, frame-safe reads, and grace-period aborts have one owner. Completion disables future callbacks before releasing the operation. Already-started cancellation I/O is not joined. |
| Reader schema and errors | `Data/ReaderColumn`, `ReaderSchema`, and `CassieDataReader.ReadServerErrorAsync` | Schema projection and repeated server-error/drain/translation logic are separated from row consumption. |
| Command translation | `Commands/CommandParameterBinder`, `ProcedureName`, and `SqlLexicalTraversal` | Binding, procedure-name validation, marker discovery, rewriting, and function detection share lexical boundaries. The command retains ADO.NET orchestration and diagnostics. |
| Prepared statements | `Commands/PreparedStatementLease` | SQL/type/session identity, cache reuse, and release have one owner. The connection performs wire cleanup and defers it while a reader is active. |
| Type metadata | `Types/CassieTypeMap` | One explicit built-in catalog supplies names, scalar/array pairs, field types, inference aliases, and format capabilities. Codec algorithms remain explicit. |
| Request construction | `Connection/EncodedParameters` and `ParameterizedQueryBatch` | Encoded values and formats are validated once and reused across automatic preparation and execution; request construction precedes transport ownership. |
| Connection navigation | `CassieConnection` partial files | Session, query, prepare, COPY, transaction, and schema code are grouped for navigation. Partials remain one class and do not create ownership boundaries by themselves. |

The operation gate tracks busy ownership and cancellation serials; it does not promise support for simultaneous public operations on one connection. Static protocol helpers and the fixed built-in catalog do not require public extension interfaces or reflection-based registration.

## Remediated findings

### F1 — Separate local validation from transport failure

**Original priority:** high. **Principles:** single responsibility; request construction separated from transport recovery. **Status:** remediated.

Previously, parameter serialization ran inside the send delegate after `RunReaderAsync` acquired the operation gate. A local codec or format error therefore reached the transport-abort path despite no request bytes having been sent, closing a healthy connection and its transaction.

[`ParameterizedQueryBatch.EncodeParameters`](../src/Cassie/Connection/ParameterizedQueryBatch.cs) now produces a validated [`EncodedParameters`](../src/Cassie/Connection/EncodedParameters.cs) snapshot. When preparation is requested, `CassieCommand` creates that snapshot before automatic server preparation and passes it through execution, avoiding repeated encoding. [`ExecuteParameterizedQueryAsync`](../src/Cassie/Connection/CassieConnection.Query.cs) builds the request before acquiring the gate, refreshing DateStyle, or invalidating settings for the attempted SQL. Schema-only requests describe parameters without encoding unused values. Failed or canceled writes that may have sent bytes still abort the physical session.

Scripted contracts check invalid values and unsupported explicit binary formats with and without an active transaction, no invalid-request bytes, unchanged session identity, and successful same-session follow-up. A peer reset during batch transmission must still close the connection. The additional preparation case checks that local validation precedes Close/Parse/Describe/Sync traffic. A shared parameter-count guard also runs before explicit preparation and schema-only automatic preparation acquire the gate. Both 32,768-parameter regressions were red with `OverflowException`; they now return `NotSupportedException` while preserving the open connection. Partially built Bind and batch buffers are cleared on exit; caller-owned value buffers are not cleared.

### F2 — Bound the entire COPY recovery exchange

**Original priority:** high. **Principles:** single responsibility; explicit recovery-policy ownership. **Status:** remediated.

Previously, only the CopyFail write or separate cancel connection had a timeout; completion drains used `CancellationToken.None`. A peer could accept recovery and withhold `ReadyForQuery` indefinitely.

[`RecoverCopyAsync`](../src/Cassie/Connection/CassieConnection.Copy.cs) now creates one five-second deadline covering CopyFail or CancelRequest, TLS negotiation, and the drain. Expiry aborts the physical stream and releases its pool reservation. Start, output, and completion server errors use the same bounded recovery policy. Successful recovery reaches ReadyForQuery and permits same-session reuse.

The original input exception, server error, caller cancellation token, or command timeout remains the caller-visible outcome. Timeout-versus-caller classification is captured before awaiting recovery, so a caller cancellation arriving later cannot replace a timeout that already triggered recovery.

The silent-CopyFail regression was red before the fix: the operation exceeded its watchdog instead of returning the original input `IOException`. A second red/green regression reproduced the later-caller-cancellation bug, expecting `TimeoutException` but receiving `OperationCanceledException`; capturing the outcome before recovery fixed it. Seventeen focused COPY cases pass, including the three server-error phases, bounded cancellation/input failure, pool-slot release, and successful reuse. A manually advanced per-connection clock proves one deadline: four seconds consumed during cancel negotiation leave one second for draining, rather than starting a fresh timeout.

### F3 — Give command translation and prepared identity separate owners

**Original priority:** medium. **Principle:** single responsibility. **Status:** remediated.

Previously, `CassieCommand` combined binding, SQL rewriting, procedure-name parsing, type inference, prepared-statement identity, release, diagnostics, and execution. Discovery and rewriting had separate copies of the same lexical state machine.

[`CommandParameterBinder`](../src/Cassie/Commands/CommandParameterBinder.cs) and [`ProcedureName`](../src/Cassie/Commands/ProcedureName.cs) now own translation and procedure-name validation. [`SqlLexicalTraversal`](../src/Cassie/Commands/SqlLexicalTraversal.cs) supplies shared token spans to the public scanner, rewriting, and DateStyle function detection. Comments, quoted identifiers, ordinary and escape strings, dollar quotes, repeated markers, and marker-like operators are covered by adversarial cases. Unchanged SQL text is copied using original spans rather than reconstructed from tokens.

[`PreparedStatementLease`](../src/Cassie/Commands/PreparedStatementLease.cs) owns request state, cached SQL/type identity, and the owning connection's session identity. It reprepares when identity changes, skips release against a replacement session, and delegates deferred cleanup to the connection. Existing and new lifecycle contracts exercise synchronous/asynchronous preparation, reuse, SQL/type changes, reopen behavior, and disposal with an active reader.

The DateStyle substring false positive is also resolved: comments, literals, parameters, and longer identifiers containing `set_config` no longer cause an extra SHOW query. Real calls, including qualified/quoted names and comments before the opening parenthesis, still invalidate the cached setting.

### F4 — Declare built-in metadata once

**Original priority:** medium. **Principles:** single responsibility; reduced change amplification. **Status:** remediated.

Previously, scalar/array OID pairs, names, field types, and inference aliases appeared in separate switches across the command and type map. Adding one type required coordinated edits to basic metadata unrelated to its encoding algorithm.

[`CassieTypeMap`](../src/Cassie/Types/CassieTypeMap.cs) now declares each built-in type once with its optional array OID, SQL name, text/binary CLR field types, CLR/DbType inference aliases, and format capabilities. Reverse array lookup and names are derived from that declaration. Both scalar and array parameter inference use the same metadata; value-dependent network, MAC, bit-string, vector, and tagged temporal variants remain explicit. The parameter and result codecs consume shared metadata while retaining their concrete serialization/validation algorithms.

The catalog preserves intentional asymmetries: text arrays remain strings while binary arrays use `CassieArray`; record/record-array and unknown types retain text metadata without gaining a binary codec; null-parameter format selection is unchanged. Every CLR field-type property retains its trimming annotations and receives an explicit known Type. No reflection registry, public extensibility mechanism, or public API change was introduced by this finding's remediation.

All 89 metadata characterization cases passed before and after the refactor. They verify all 30 scalar/array pairs in both directions, names, 34 format-specific field-type cases, text-only binary rejections, null-format selection, DbType precedence, and a representative macaddr8 inference/codec path. The integrated Release build passes with zero warnings or errors, including AOT compatibility analysis; fresh package/Native AOT execution is qualified separately below.

## Additional corrections and limits

- Failed reader initialization stops its cancellation coordinator before abandoning the reader, preventing an orphan timeout from acting on a later session. Clearing columns also clears their parallel wire-format array. Removing that reset made the malformed-row regression fail; restoring it returned the test to green.
- Process-wide pool clearing reproduced the handoff's `Expected: 1, Actual: 2` idle-reuse failure when placed between returning the first session and opening the next connection. The same global-clear behavior existed in the baseline. This establishes a pre-existing isolation defect consistent with the symptom, without proving the cause of every historical failure. All three classes that globally clear pools now share the nonparallel collection defined on `PoolLifecycleContractTests`; discovery/results XML confirms all ten pool cases share it.
- The provider intentionally rejects unsupported ADO.NET operations, directions, isolation levels, and codecs. Unsupported functionality alone is not a Liskov-substitution violation. No additional broad public-interface segregation defect was established; the ADO.NET base classes define that surface.
- Live schema identifiers are fixed SQL constants to satisfy the SQL-construction analyzer. One serialized test process owns a disposable server. Cleanup drops only successfully recorded objects, in reverse creation order, through a separate original-database connection, and reports failures. Database-image restore uses its own fresh server because that operation affects subsequent catalog behavior.

## Verification boundary

Fresh final checks passed against immutable Cassie revision `19edf3a36fc9d2a65187d137370b6f164fcdfb2e`, built from a clean archive with Rust `1.99.0`. The executable's SHA-256 is `164f9509b7af2d1fc4b9d2befce4fcfc3ffcb33e90e53db379082cfcc3c2d7d3`.

| Check | Final result |
| --- | --- |
| Release solution build | Zero warnings and zero errors. |
| Formatting and whitespace | Format verification and working-tree/staged diff checks passed. |
| Workflow validation | `actionlint` passed. |
| Ordinary managed tests | 546 passed, zero failures, zero skips, in 1 minute 34.870 seconds; 470 non-live and 76 live cases. |
| Isolated database-image restore | One passed, zero failures, zero skips, in 2.269 seconds on a separate fresh server. |
| Combined disjoint managed coverage | **547 passed, zero failures, zero skips**, including all 77 live cases. |
| Fresh package consumer | Local `Cassie.1.0.0.nupkg` restored through an isolated NuGet cache, compiled as macOS ARM64 Native AOT with warnings treated as errors and zero warnings, and passed its runtime probes. |

All live feature flags were enabled, including array shapes, name, JSONB, JSONPath, record metadata, DateStyle status updates, and startup application-name options. The ordinary endpoint required TLS with a custom root certificate; matching-host and rejected-hostname cases passed. The standalone restore used a fresh server so its catalog effects could not contaminate ordinary cases.

The package-reference Native AOT consumer exercised field metadata, arrays, numeric and date values, local invalid-parameter rejection before prepared execution, transactions, and same-session prepared-statement reuse. This is current package-consumer runtime evidence, rather than only an AOT analyzer or publication check. The package was built locally; it has not been published by this work.

The final managed XML results are under `/var/folders/dg/rh2n13nx6cqgwpb8lkkxsgy80000gn/T/cassie-dotnet-solid-validation-hvy2qkax/ordinary/results.xml` and the sibling `restore/results.xml`. The earlier 407-case result predates the F1–F4 remediation and is superseded by these fresh checks.

The SOLID remediation CI built that immutable Cassie revision with the pinned Rust toolchain and separate ordinary/restore endpoints. Exact-head CI and CodeQL passed before [PR #1](https://github.com/cntryl/cassie-dotnet/pull/1) merged as `3f2ca8b09c6c444faa5807919bbe495c1a65dc32`. These results do not establish deployment, production performance, publication, or release qualification. Historical results in `design.md` apply to the earlier source that produced them.

The subsequent REST client change targets `/v1/...` and the unversioned
`/livez`, `/readyz`, `/startupz`, and `/targetz` probes. Its CI follows Cassie
`main` and records the resolved server SHA. See [REST client](rest-client.md) for
the explicitly authorized temporary live-contract failures during that migration.

## Dependency injection follow-up

The optional `Cassie.DependencyInjection` package adds service registrations while
the core `Cassie` package stays independent of dependency injection. Its
`CassieDataSource` derives from the existing ADO.NET `DbDataSource` contract,
creates caller-owned connections, borrows logging, and leaves shared pools intact
when disposed. Framework ADO.NET types provide the shared provider contracts;
there is no separate `Cassie.Abstractions` package.

Four new regression tests first reproduced aliases disposing externally supplied
instances or changing an existing service's lifetime. Alias registration now
preserves instance ownership and the winning typed registration's lifetime,
including the REST client's administration alias. Default REST scopes own
separate cookie containers. Authentication and server logout remain explicit.

Command timeout assignment also now uses the connection-string builder's supported
range, zero through 2,147,483 seconds. Three boundary regressions were red before
the fix; all seven timeout cases and ten data-source lifecycle cases pass.

The follow-up core run against the same immutable server passed 621 cases,
including the isolated restore, with zero skips. The six REST login failures
remain the explicitly accepted pending `/v1/...` server migration. Both local
packages were first published as `0.1.0`; the starter README and source mapping
are updated in the `0.1.1` patch. An isolated package-only consumer restored
`Cassie.DependencyInjection.0.1.0.nupkg` and its core dependency through a fresh
NuGet cache, compiled as macOS ARM64 Native AOT with warnings treated as errors,
and passed singleton/scoped ownership, REST client separation, and live pgwire
execution. This does not qualify the pending REST routes or publish either
package.

All 15 dedicated DI cases pass, including real HTTP cookie separation, synchronous
and asynchronous container disposal, existing-provider aliases, logger ownership,
and the four ownership/lifetime regressions. Combined with the disjoint core
runs, the follow-up result is 636 passes, six expected pending REST failures,
and zero skips across 642 cases. Release solution build, format verification,
whitespace checks, and workflow validation are clean.
