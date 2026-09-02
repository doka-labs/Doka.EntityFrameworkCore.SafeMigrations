---
id: D-005
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Catalog-query resource bounds, immutable reports, fingerprints, and diagnostic privacy"
supersedes: []
superseded-by: []
amends: []
amended-by: [D-009]
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-005 -- Bound catalog work and separate deployment evidence from telemetry

## Context and Problem Statement

A migration can contain many operations and run against a noisy legacy
catalog. One unbounded combined query risks payload, parameter, packet, and
planner-memory limits. One query per operation increases round trips and
makes snapshot consistency harder to reason about.

The resulting report must bind the intended model and operations to ordered
observations. It also contains information that is inappropriate for broadly
exported metrics. Debug strings, mutable inputs, or unrestricted diagnostic
payloads would undermine evidence stability and expose deployment details.

The decision is how to bound database requests and intermediate work while
retaining useful immutable evidence and a deliberately narrower telemetry
surface.

## Decision Drivers

- Explicit operation limits per optimizer-visible statement and explicit
  statement, parameter, and UTF-8 payload limits per transport batch.
- Stable global ordering and all-or-failure publication of classifications.
- Expected definitions cannot change after caller-owned collections mutate.
- Fingerprints represent semantic contracts, not unstable debug output.
- Report serialization avoids reflection and an additional intermediate graph.
- Telemetry uses bounded classifications, not object names, credentials, or
  exception payloads.
- Performance claims must be tied to measured budgets, not folder layout or
  an unsupported constant-memory promise.

## Considered Options

- Bounded batches with immutable reports and separate telemetry
- One query and one materialized diagnostic payload for the whole run
- Per-operation catalog calls with verbose trace events

## Decision Outcome

Chosen option: "Bounded batches with immutable reports and separate telemetry",
because it limits each database request while preserving ordered evidence and
a distinct privacy boundary for diagnostics.

The current classification limits are 32 operations per optimizer-visible
statement, eight statements per ADO.NET transport batch, 16,000 parameters,
and 4 MiB of UTF-8 SQL plus parameter payload across the batch.
MySQL/MariaDB also cap payload at half the observed `max_allowed_packet` and
capture provider runtime plans in 512-operation windows while retaining the
complete migration-level unique-index catalog. These are explicit repository
constants and qualified budgets, not universal database limits.

A single operation that cannot fit is rejected before executing its
classification statement. Repeated typed parameter values are shared within a
statement. Global ordinals span statements, transport batches, and provider-
plan capture windows. A report is not published with only the successful
prefix of a failed analysis. The configured EF command timeout is propagated
to every raw catalog command and `DbBatch`; provider analysis ownership in
D-004 preserves the applicable consistency window. Native transport batching
is selected only when `DbConnection.CanCreateBatch` is true. Otherwise the
same bounded statements execute sequentially through ordinary `DbCommand`
instances, preserving compatibility without parsing or concatenating provider
SQL.

Definitions snapshot enumerable inputs into owned read-only collections.
Model fingerprints stream length-prefixed canonical relational metadata into
a provider-bound, versioned SHA-256 representation. Operation-contract
fingerprints preserve operation order and safe-operation definitions and
policy. Ordinary provider operations contribute only their CLR type marker,
not their properties or SQL; the immutable artifact digest and independent
review cover that separate boundary. Unknown migration-relevant annotation
value shapes fail closed instead of silently disappearing from identity.
Relational column fingerprinting distinguishes EF Core's two public shapes.
Ordinary scalar columns use their existing property mapping so established
digests and allocation budgets remain stable. Property-less `ToJson` container
columns use the public `IColumn` facets instead of assuming that a scalar
mapping exists.

A fingerprint detects drift relative to a supplied expectation. It does not
authenticate the report, its producer, or a deployment caller. Protected
artifact storage, access control, and provenance remain operator concerns.

Reports retain detailed assessments, object identities, instance identity, and
unexpected-object inventory. They are protected deployment artifacts, not
safe generic log messages. The JSON contract supports writing to a
caller-owned Utf8JsonWriter without reflection or a second DTO graph.

Telemetry emits bounded mode, status, provider, engine, and failure-category
tags. It omits instance IDs, migration IDs, object names, raw SQL, connection
strings, and exception text. Successful/blocked report recording, exception
recording, cancellation, and connection cleanup have distinct boundaries; a
metric count must not be interpreted as a complete record of every attempted
runner invocation.

### Consequences

- Good, because each catalog request has explicit bounds and a failed later
  chunk cannot masquerade as a complete successful analysis.
- Good, because stable evidence can be retained without exporting its sensitive
  detail into high-cardinality telemetry.
- Bad, because complete inputs, assessments, canonicalization collections, and
  unexpected-object inventories still scale with their size.
- Bad, because consumers must securely correlate reports and telemetry and
  distinguish an evidence fingerprint from an authenticated artifact.

### Confirmation

Run the Core catalog-limit, definition, fingerprint, and run-contract tests.
Require exact-boundary success, oversized parameter/payload rejection,
single enumeration, immutable snapshots, stable ordering, and invalid
annotation rejection. Provider tests must exercise `DbBatchCommand`
parameterization, multiple result sets, cancellation, configured command
timeouts, and 100,000 deterministically ordered mixed live operations on every
qualified engine profile. The large-scale workload must exercise every
observed state and planned action rather than repeating one matching object.
Serialization and telemetry tests must prove that changes to protected report
detail do not add it to telemetry tags.

Run both provider fingerprint suites and PostgreSQL facet-isolation cases
under the qualified dependency profiles. A stable digest in one provider or
runtime process does not prove all relational facets are represented.

Run all three benchmark projects using the commands in CONTRIBUTING and
the versioned performance budgets. Require complete, non-duplicate, known
measurements for construction, planning, SQL generation, model comparison,
fingerprinting, and serialization as applicable.

The live provider suites also verify multi-chunk ordering and noisy catalog
behavior. The provider tests use the shared evidence collector for 20 pooled
runner samples with 100 expected tables and 1,000 additional foreign tables in
the noisy case. The tests assert counts and the relative p95 rule of
noisy <= 2 * clean + 250 ms; the collector persists the measurements.
These measured test conditions are not a global production latency or
catalog-size guarantee.

## Pros and Cons of the Options

### Bounded batches with immutable reports and separate telemetry

- Good, because the query protocol, evidence identity, and diagnostic exposure
  have explicit and independently testable boundaries.
- Good, because batching avoids one round trip per operation while limiting
  individual request growth.
- Bad, because the complete run still has input- and catalog-proportional
  memory cost and requires intentional retention policy.

### One query and one materialized diagnostic payload for the whole run

- Good, because one database request and one rich object can be convenient for
  a small, known catalog and interactive debugging.
- Bad, because parameter/payload growth and a second serialization graph are
  uncontrolled, and broad diagnostic export exposes detailed schema data.

### Per-operation catalog calls with verbose trace events

- Good, because each query is small and an individual operation can be traced
  directly without a batching protocol.
- Bad, because round trips grow with operation count, consistency still needs
  a shared scope, and detailed events create privacy/cardinality risks.

## More Information

The resource contract bounds requests, not the number of objects a database
can contain. No global catalog cache or cross-instance mutable state is
introduced. Input snapshotting and immutable report construction deliberately
trade bounded per-item allocations for stable ownership.

D-002 defines what the fingerprinted target means. D-004 defines the observed
window. Neither a hash nor a report proves the database server is honest.

### Re-evaluation Triggers

- Supported engine/protocol changes alter packet, parameter, or planner-memory
  behavior, or qualification measurements exceed the current budgets.
- New model annotations, expression facets, or report fields affect canonical
  identity, serialization compatibility, or privacy.
- A real consumer requires incremental evidence publication or streaming
  inventory and can define its ordering, failure, and retention semantics.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing resource and evidence contracts documented retrospectively without reconstructing historical approval.
- 2026-08-26: Doka-format revision adds numerical boundaries, measurable confirmation, privacy distinctions, and non-constant-memory limits.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. Bounded catalog work, immutable definitions, fingerprints, reports, and telemetry privacy are implemented with the referenced regression coverage and measurement boundaries.
- 2026-08-26: Clarified the existing fingerprint boundary for ordinary provider operations; their content identity requires the deployment artifact digest and separate review.
- 2026-08-31: Added the public `IColumn` facet path for property-less JSON container columns while retaining the established scalar property-mapping path and its allocation profile.
- 2026-09-02: Bounded optimizer-visible statements independently from ADO.NET transport batches, propagated EF command timeouts to raw catalog work, and added 100,000-operation mixed-state live qualification.
- 2026-09-02: Capability-gated native batching and added the bounded sequential command fallback for compatible ADO.NET wrappers that do not implement `DbBatch`.
- 2026-09-02: D-009 added 128-row/4,096-cell model-managed operation bounds,
  compact row evidence, canonical value hashing, and explicit non-disclosure of
  keys and managed values in reports, telemetry, and exceptions.

### Implementation References

- [Catalog query limits](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationCatalogQueryLimits.cs)
- [Catalog batch adapter](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationCatalogBatch.cs)
- [Sequential fallback integration tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/SafeMigrationCatalogBatchIntegrationTests.cs)
- [Catalog-limit tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Analysis/SafeMigrationCatalogQueryLimitsTests.cs)
- [Definition ownership and validation tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationDefinitionTests.Lifecycle.cs)
- [Model fingerprint implementation](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationModelFingerprint.cs)
- [Annotation fingerprint handling](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationModelFingerprint.Annotations.cs)
- [Operation-order and policy fingerprint tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationContractFingerprintTests.Lifecycle.cs)
- [Column-facet fingerprint tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Columns/SafeMigrationContractFingerprintTests.Columns.cs)
- [MySQL/MariaDB model-fingerprint tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Infrastructure/MySqlModelFingerprintTests.cs)
- [PostgreSQL model-fingerprint tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Infrastructure/PostgreSqlModelFingerprintTests.cs)
- [PostgreSQL model-facet tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Infrastructure/PostgreSqlModelFingerprintFacetTests.cs)
- [Report contract](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationRunReport.cs)
- [JSON writer](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationReportJson.cs)
- [Run-contract tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationRunContractTests.cs)
- [Telemetry implementation](../../src/Doka.EntityFrameworkCore.SafeMigrations/Diagnostics/SafeMigrationTelemetry.cs)
- [Live performance evidence](../../tests/Shared/LivePerformanceEvidence.cs)
- [Performance budgets](../../eng/performance-budgets.json)
- [Observability and measurement boundaries](../runbooks/observability.md)

### Sources

- [.NET 10 `DbBatch`](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbbatch?view=net-10.0) (standard batch, command, timeout, and provider-specific execution contract; retrieved 2026-09-02)
- [.NET 10 `DbConnection.CanCreateBatch`](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbconnection.cancreatebatch?view=net-10.0) (default false and provider capability contract; retrieved 2026-09-02)
- [Npgsql batching](https://www.npgsql.org/doc/basic-usage.html#batching) (parameterized multi-command batching and result-set behavior; retrieved 2026-09-02)
- [MySqlConnector `MySqlBatch`](https://mysqlconnector.net/api/mysqlconnector/mysqlbatchtype/) (MariaDB batching behavior, timeout, and multi-result reader contract; retrieved 2026-09-02)
- [EF Core 10 `GetCommandTimeout`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.relationaldatabasefacadeextensions.getcommandtimeout?view=efcore-10.0) (configured context command-timeout contract; retrieved 2026-09-02)
