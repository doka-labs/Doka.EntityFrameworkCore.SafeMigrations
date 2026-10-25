# Support and qualification

## Support contract

Version 10.6.0 adds public CASE expressions, certified generated-column
and CHECK support, lossless signed-integer upgrades, ordered projection/DROP
corrections and stack-safe traversal. Measured allocation reductions do not
change migration safety or make hardware-dependent timing a qualification gate.
It retains the 10.5.0 diagnostics with canonical report schema 4, selected
view schema 3 and contextual owned runtime errors. Older schema
files remain packaged; update validators and rebuild references to the compiled
`CurrentSchemaVersion` constant.
Those diagnostics do not change migration decisions or the target framework.
The separate Doka upgrade below changes the MySQL/MariaDB dependency range.
MySQL/MariaDB callers must account for the owned-guard outer exception type;
see [diagnostic compatibility](diagnostics.md#runtime-provider-differences).
Native qualification still requires the complete blocking matrix; ARM SQL Server
skips and offline SQL-shape tests are not substitutes for its x64 cells.

## Upgrade to 10.6.0

Keep every installed SafeMigrations package on the same 10.6.0 version; install
only the provider adapters the application needs. Account for the dependency
floors below and review newly scaffolded implicit index-length changes without
rewriting historical migrations.

MySQL/MariaDB and PostgreSQL now qualify explicit lossless signed-integer
widening under `RepairIfSafe` when exact source, ancillary facets and native
storage/dependency requirements are proven. Accepted index removals and safe
sibling transitions preserve independently certified column facts, including
interrupted upgrade prefixes and replay; removed key/index or row proofs are
not restored. SQLite retains exact physical `INTEGER` matches without admitting
a store-type conversion. Runtime guards remain mandatory; unsupported
dependencies and unproven conversions do not become safe through this upgrade.
See the [MySQL/MariaDB](mysql-mariadb-ddl-behavior.md#lossless-signed-integer-alterations)
and [PostgreSQL](postgresql-behavior.md#lossless-signed-integer-alterations) boundaries.

The 10.6.0 generated-expression support conditionally changes approved
contract fingerprints: successfully parsed EF computed-column and CHECK SQL
now includes original source spelling and numeric-token provenance. Review
affected migration artifacts, regenerate stored
`SafeMigrationContractFingerprint` values and reapprove them before use.
Do not bypass mismatch checks or rewrite historical migrations. Ordinary
SQL-default parsing, opaque/unparsed SQL, explicit uncaptured definitions and
provider-owned operation fingerprint encoding are unchanged, as is the
relational model-fingerprint format. Parsed CHECK equality also retains exact
authored spelling; whitespace/parenthesis-only differences can be `Different`
without being `Unsupported`. Computed-column source was already exact and
does not gain automatic repair. See [expression compatibility](api-reference.md#sql-expressions-and-policy).

If an application stores no contract-fingerprint approval, this change does
not require inventing one or regenerating migration source. Existing approval
artifacts must be reviewed against the immutable migration with the selected
library version; do not reuse an older fingerprint as though it proved the new
contract. Valid native expressions can still be `Unsupported` when deterministic
binding, source independence or a native capability cannot be proved. A policy
change does not bypass that boundary. Review the intended dependency-removal
order, native permissions and affected provider guidance before execution.

Report schema 4 and view schema 3 are inherited from 10.5.0, not advanced again.
The complete hosted/native matrix and package/readback gates remain mandatory;
see the [publication runbook](operations/release-publication.md) for release
status and evidence requirements.

Repository release-line automation retains the same five packages, signed-tag
publisher and .NET 10 identity. Only main/10 is active; maintenance/10.x remains
inactive. This is not .NET 11 support or an application migration requirement;
see [release lines](operations/release-lines.md).

### Dependencies and package identity

All publishable projects target `net10.0`. The repository pins SDK 10.0.402
with roll-forward disabled. SafeMigrations supports EF Core 10 only.

| Package | Runtime dependency contract |
| --- | --- |
| Core | `Microsoft.EntityFrameworkCore.Relational` `[10.0.13,10.1.0)` |
| MySQL/MariaDB | `Doka.EntityFrameworkCore.MySql` `[10.6.2,10.7.0)` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` `[10.0.3,11.0.0)` |
| SQLite | EF Core SQLite Core `[10.0.13,10.1.0)`; application-owned native bundle |
| SQL Server (available since 10.4.6) | `Microsoft.EntityFrameworkCore.SqlServer` `[10.0.13,10.1.0)`; `System.Security.Cryptography.Pkcs` minimum `9.0.21` |

The EF Core and dependency-injection abstraction minimums are 10.0.13. These
are published package dependency floors, not CI-only updates; consumers on an
older 10.0 patch must update their resolved stack. EF Core SQL Server 10.0.13
also raises its transitive SqlClient minimum to 6.1.7, which resolves SNI
6.0.3. Its exact development/testing license exception is documented in
[secure development](security/secure-development.md#sql-server-dependency-license-exceptions).
The target framework and independently bounded Npgsql range remain unchanged.
The separately reviewed Doka minor-line update is described below.

The SQL Server provider centrally pins its existing transitive PKCS dependency
to the patched 9.0.21 minimum. NuGet promotes that minimum into the published
package, so consumers do not need the repository's central configuration or
lockfile. The other four packages do not acquire that dependency. This fixes
the package versions identified by Microsoft's published
[PKCS decode advisory](https://github.com/dotnet/announcements/issues/454) and
[signature-check advisory](https://github.com/dotnet/announcements/issues/468),
verified on 2026-10-14. The later EF Core 10.0.13 / SqlClient 6.1.7 upgrade
retains this patched 9.x minimum rather than introducing a separate PKCS
major-version upgrade.
The consumer owns runtime/SDK servicing and redeployment; a NuGet dependency
update does not update an installed or self-contained .NET runtime.
The minimum is not an exact version lock or a claim that every higher major
version is patched. Consumers selecting PKCS 10.x must use 10.0.13 or later
for these advisories; NuGet audit remains enabled for the actual resolved graph.

The current MySQL/MariaDB package requires Doka 10.6.2 or a compatible later 10.6 patch
release and rejects the next minor line. This boundary avoids an exact
transitive pin without claiming compatibility across an unqualified behavioral
SPI revision. CI does not build Doka and never uses a cross-repository
ProjectReference. The MySQL/MariaDB package lockfile resolves the public
Doka 10.6.2 package. Its verified signed release tag identifies source commit
`6e2618140d821bd6befdf46181fcb82dca323c4f`. The complete package, engine,
tooling, coverage, and performance matrix remains the SafeMigrations release
gate; a local Doka ProjectReference or locally packed candidate is not release
evidence.

Doka 10.6.1 introduced preservation of explicit GUID SQL defaults in its model and
migration metadata; an implicit client GUID strategy no longer overrides them.
The current 10.6.2 minimum additionally resolves SQL, computed and literal
defaults through actual property mappings, including GUID properties that
exist only in secondary Entity Splitting tables and shared secondary columns.
It retains the public migration SPI, dependency ranges and GUID storage defaults;
these metadata fixes do not require a schema migration.
Explicit application generation choices and historical snapshot annotations
remain authoritative. Review new scaffolded differences without rewriting
deployed history; see the [GUID SQL-default upgrade boundary](efcore-provider-upgrade-risk.md#guid-sql-defaults).

SafeMigrations 10.6.0 raises its Doka dependency floor to the 10.6 line.
Doka now budgets implicit indexed string/binary
lengths across every physical key part, overlapping indexes and compatible
principal-derived FK lengths. Review newly scaffolded column-length changes;
existing migrations and explicit application facets are not rewritten. Its
packed-DECIMAL correction agrees with SafeMigrations' existing projected and
native-catalog width formulas. EF and connector ranges remain unchanged;
Doka's System.IO.Hashing minimum advances from 10.0.12 to 10.0.13. See the
[versioned index contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/d1e40a7b211c10b354e5aa6df80d9fea710d8f58/docs/provider-configuration.md#index-key-byte-fidelity).
The full release matrix remains required; prior 10.5 qualification is not
automatically evidence for 10.6.

The earlier Doka 10.5 upgrade enabled explicit `MySqlGuidFormat.Uuid` on MariaDB, not an
automatic conversion of existing GUID columns. Doka requires MariaDB 10.7 or
later for this type; SafeMigrations qualification still uses the declared
engine matrix, not every server above that capability threshold. MySQL rejects
native UUID. Binary16 remains the default and Char36 remains supported.
Native UUID has 16-byte physical width and no text collation or index-prefix
contract. See [GUID storage and upgrade guidance](mysql-mariadb-ddl-behavior.md#guid-storage-and-native-uuid).

Doka 10.5.0 also maps implicit application-owned 16-byte binary properties to
`varbinary(17)`, independently of GUID defaults. This can create a model diff
and a newly scaffolded `AlterColumn`; review that authored migration and its
data contract. The dependency update does not rewrite existing migrations or
approve GUID representation changes.

The remaining declared dependency graph and .NET 10 release metadata were
rechecked on 2026-09-21. Bounded package ranges describe compatibility. The
five package-project lockfiles identify the dependency graph used to compile
and qualify the package artifacts in a particular revision; they do not
constrain a consumer's NuGet resolution or lock the
engineering projects.
The SQL Server project supplies the fifth lockfile. Releases 10.4.2 through
10.4.5 contain four package artifacts; their inventory remains unchanged.

The five-package set began with stable 10.4.6, including the optional SQL Server
provider. Version 10.6.0 retains that inventory and the SQL Server PKCS
minimum of 9.0.21. The Core API baseline promotes the additive CASE surface;
the existing diagnostic surface was introduced in 10.5.0. The current Doka
dependency contract is the 10.6 line above. Qualification requires the blocking
workflow, including all three native x86-64 SQL Server cells. The
[publication runbook](operations/release-publication.md) owns the five-package
readback procedure and release evidence.

The 10.4.11 changes qualify sparse live/projected constraints and indexes from
actual native metadata, accepted physical lifetimes and current row proofs.
MySQL/MariaDB carries native owner equality through transition/backfill catalogs
and guards unsafe FK supporting indexes. Accepted PRIMARY/FK removal and ordinary
nonunique BTREE creation preserve independently proven intrinsic column sources,
not candidate-key or row authorization; execution retains fresh key/source guards.
PostgreSQL qualifies native CHECK and
physical index-input bindings before data probes, preserving bounded metadata
and SQL transport. SQLite retains declared parent-key equality, strict capacity
and appended-FK order; SQL Server uses compatible live endpoint/key evidence
under permission-first catalog and independent row checks. Complete source
qualification remains mandatory; local ARM SQL Server skips do not qualify its
native x86-64 cells or satisfy the full merged coverage gate.

The earlier 10.4.10 changes correct ordered nullability/backfill and sparse physical
constraint/index proofs, cross-operation rename evidence and canonical
model-managed-key identity. MySQL/MariaDB required additions use owned atomic
stages with forward recovery; PostgreSQL deletes fence dependent writers and
retain verified schema ownership; SQL Server cleanup requires owned session
acquisition. Earlier provider repair and trigger-freshness contracts remain.
These are bounded path-specific changes, not a general engine-latency or
peak-memory guarantee. SQLite retains its model-owned rebuild contract.
Engineering dependencies inherit the reviewed Testcontainers 4.16.0 and pinned
Action updates on `main`. For the historical 10.5.0 release only the MySQL/MariaDB package lockfile
changes to resolve Doka 10.5.0; the other four package lockfiles are unchanged.
No engine, tooling, package or coverage gate is removed. Performance measurements
remain informational; invalid evidence and functional failures remain blocking.
The analysis and runtime optimizations introduced in 10.4.7 remain in place;
see [MySQL and MariaDB behavior](mysql-mariadb-ddl-behavior.md) and
[D-014](decisions/D-014-mariadb-catalog-snapshot-for-batched-classification.md)
for catalog-copy ownership and fallback.

## Engine matrix

Every repository-defined GitHub-hosted Linux job is pinned to the explicit x64 `ubuntu-26.04`
label, including qualification, benchmarks, publication, dependency review,
maintenance and Scorecard. GitHub lists this image as generally available;
the moving `ubuntu-latest` label is not used. The complete hosted qualification
workflow must establish this candidate's actual engine, tooling, package and
SBOM evidence on the new image; changing a YAML label or running local ARM
tests does not establish that result. Existing SDK selection through
`global.json`, engine image digests, timeouts and performance budgets remain
unchanged. The repository maintains no permanent second Ubuntu matrix.
GitHub-managed default-setup CodeQL and automatic dependency-submission runs
are separate platform workflows; these repository labels do not configure them.

The reusable workflow `.github/workflows/quality-gates.yml` is invoked by CI,
release candidates, and stable releases. It declares these exact qualification
images:

| Engine | Image |
| --- | --- |
| MySQL 8.4 | `mysql:8.4.13@sha256:76f0358884cb92d9a086d24e5671c38048d3fef40f2588703d73eb55392d6b69` |
| MySQL 9.7 | `mysql:9.7.4@sha256:8fb11c38b3551e22d50adbb688f027f8ff4ea2d2445aa277c1cc97b4af85ec54` |
| MariaDB 10.11 | `mariadb:10.11.19@sha256:0d0945c57f75b3a3b9ebe71aedaaad005fa5305054e8fca3d2e42a8c92b43b5e` |
| MariaDB 11.4 | `mariadb:11.4.13@sha256:250e1609e93c0e6aecf64bfbab2d84d5e89d2481540fe77447ff6c855c3b1873` |
| MariaDB 11.8 | `mariadb:11.8.9@sha256:f16d7a344413e519dff518f27bdb221388f84b0d274c97c4fb5f2cb0dc1b34fa` |
| MariaDB 12.3 | `mariadb:12.3.3@sha256:99c125f103f5d081ebbc9ffe064e63daf7f688ac33a5f7f29af2d7260a96debf` |
| PostgreSQL 14 | `postgres:14.24@sha256:8ab94f61668dc73bbab4dc13273ff2fc37e635536af62aef5306a49a9682a446` |
| PostgreSQL 15 | `postgres:15.19@sha256:baa22e6286bf832cb147b8bb5aa9d8bc999846d69c21d11bf63f58fbeed1fa48` |
| PostgreSQL 16 | `postgres:16.15@sha256:9e2cf6287f25c3151f4c9421ccce2b17f3e60061c111a29165e0d2cdd9ff0a97` |
| PostgreSQL 17 | `postgres:17.11@sha256:b8faeff7712663c2773a190181e1246e37e364b887611c04658c28b44e2408fe` |
| PostgreSQL 18 | `postgres:18.6@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6` |
| SQLite | 3.46.1 or later; application-owned native bundle |
| SQL Server 2019 | `mcr.microsoft.com/mssql/server:2019-latest@sha256:07d7dedecdbfcf6776035dc5eefb5bfd9aa1804b2f6b99f7dedb397d8a599069` |
| SQL Server 2022 | `mcr.microsoft.com/mssql/server:2022-latest@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718` |
| SQL Server 2025 | `mcr.microsoft.com/mssql/server:2025-latest@sha256:245f6b1c8903edae52cb20fa31419cefcde1a49938bb8c3447099011fc871ca3` |

The image patches and manifest digests above were refreshed against the official
[MySQL](https://hub.docker.com/_/mysql),
[MariaDB](https://hub.docker.com/_/mariadb),
[PostgreSQL](https://hub.docker.com/_/postgres) and
[Microsoft Container Registry](https://mcr.microsoft.com/v2/mssql/server/tags/list)
registries on 2026-10-25. PostgreSQL keeps the same server patch versions with
rebuilt image manifests. These declarations identify the configured matrix, not
completed qualification of every image; the required hosted matrix must pass.

A primary-source inventory on that date verified every current direct NuGet
dependency, SDK/tool and Action against its official stable releases. Updates
are [FsCheck.Xunit 3.6.0](https://www.nuget.org/packages/FsCheck.Xunit/3.6.0) and
[CodeQL Action 4.39.1](https://github.com/github/codeql-action/releases/tag/v4.39.1),
pinned to commit `9b3254085494b55a0f7c4c2b3d2d52cc712ce370`. SDK 10.0.402,
dotnet-ef 10.0.13, SBOM Tool 4.1.5 and Ryuk 0.14.0 remain
unchanged. The patched PKCS 9.0.21 minimum remains on its existing 9.x line;
this review does not impose a new transitive dependency major on consumers.

Local MySQL/MariaDB integration tests, design-time fallback contexts and the
shared CI coverage/tooling defaults use MariaDB 12.3.3. Explicit engine,
version and image inputs remain supported; every other declared server line
still has its independent matrix cell. This repository default does not change
the provider version selected by a consuming application.

MySQL/MariaDB support follows Doka's canonical feature profiles. PostgreSQL
support spans major versions 14 through 18; every supported major is an
independent release-gate cell. SQLite xUnit qualification uses isolated
in-process databases, including private-cache file-backed concurrent
`MigrateAsync` callers that exercise EF's migration lock. The EF tooling gate
applies Strict and legacy migrations and bundles twice to asserted file
databases from the centrally declared dependency graph. A new endpoint or
removed upstream version requires a reviewed support-contract change and fresh
evidence.

The 2026-08-27 source review established the declared PostgreSQL 14 through 18
support matrix; the current patch/digest refresh is dated above. PostgreSQL 14
reaches upstream end of life on 2026-11-12. Release
qualification performed after that boundary must re-evaluate the declared
matrix instead of inheriting this dated support conclusion.

The table records the matrix configured in source. Only a successful workflow
run for the exact commit/package version establishes executed qualification;
the presence of a tag or image digest in this document does not.
SQL Server's three independent cells use the Developer edition for tests,
not a hosted service. Microsoft supports its Linux containers only on x86-64;
Apple Silicon emulation is not a valid qualification substitute. The matrix
does not claim Azure SQL Database, Managed Instance, or Synapse compatibility.
See [Microsoft's container guide](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver17)
and [SQL Server behavior](sqlserver-behavior.md).

## Dependency qualification

Central package declarations define bounded compatible ranges. Only the five
publishable package projects commit lockfiles. Those files preserve the exact
compile-time graph and package content hashes used for the released artifacts;
they do not prescribe a consumer's resolved graph. Test, benchmark, sample,
and engineering projects resolve the same central declarations without
committed lockfiles. CI restores package projects in locked mode and resolves
each execution project's complete graph before building and testing it.
EF Migration Bundles are the sole exception: EF publishes them for the runner
RID, which the platform-neutral lockfiles cannot record, so those RID-specific
restores run unlocked inside isolated repository copies and never modify the
committed lockfiles.

This scope intentionally trades away lockfile content-hash validation and
transitive dependency diffs for the execution projects. The repository accepts
that limitation because a lockfile per project caused ordinary grouped
Dependabot updates to leave an inconsistent subset of 20 lockfiles. D-006
records the compensating controls and the conditions that require this decision
to be revisited.

Dependabot proposes dependency and lockfile updates through ordinary pull
requests. Every accepted update therefore runs the same complete CI workflow as
product code, including all provider cells, package consumers, coverage,
performance, and SBOM validation. Dependency Review compares explicitly submitted
base/head snapshots bound to the current pull-request commit. Contributor code
runs only in separate read-only, cache-denied PR jobs; the trusted receiver
validates their exact source-run/attempt and immutable data artifacts before
submitting snapshots and publishing the required check. Neither its automatic
nor manual entry executes contributor restore hooks. Forks
and Dependabot use the same validation path. Missing, stale, or incomplete
evidence fails closed; automatic GitHub submissions are not the authority for
this check. The canonical [secure-development guide](security/secure-development.md)
defines the trust boundaries, bootstrap path, license/vulnerability policy,
bounded retry, and required comparison-header validation.

## Behavioral evidence

The executable test inventory consists of five independent xUnit assemblies
and focused engineering checks:

- [provider-neutral Core tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests);
- [MySQL/MariaDB tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests);
- [PostgreSQL tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests);
- [SQLite tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests);
- [SQL Server tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests);
- FsCheck properties across the Core and server-provider assemblies for generated Core-contract,
  identifier-rendering, catalog-normalization, and provider-boundary inputs;
- [coverage verifier tests](../eng/tests/test_verify_coverage.py);
- package-content, package-only consumer, SBOM, EF tooling, and public NuGet
  readback scripts exercised by the reusable quality and release workflows.

Test results from the qualified commit are the authority for current case
counts. Counts are not duplicated here because parameterized cases and the
engineering suites evolve independently of the support contract.

Server-provider tests use real Docker servers. SQLite xUnit tests use isolated
in-process databases; the SQLite EF tooling gate owns asserted file-database
coverage. The provider-specific suites collectively cover:

- all 23 operation kinds;
- all 22 supported non-null CLR literal families plus literal `NULL` across the
  server providers, with provider-specific convergence or pre-DDL fail-closed
  classification;
- missing, matching, transition-ready, different, unsupported, data-blocked,
  and prerequisite-missing states;
- `ExistenceOnly`, `ThrowIfDifferent`, and `RepairIfSafe`;
- source-frozen legacy convergence policy selection, mutable column repair,
  matching rerun, null-data blocking, invariant-drift rejection, and
  MySQL/MariaDB preservation of unmodeled `EXTRA` modifiers;
- provider-proven ordinary `VARCHAR` widening, grouped and deduplicated
  live-data narrowing proofs, execution-time race rejection, complete
  postflight, idempotent replay, operational-impact evidence, and exact
  MySQL/MariaDB `BIT(1) -> TINYINT(1)` Boolean repair;
- typed Doka column-metadata acceptance and rejection, plus exact repair of
  historical CLR defaults without retaining a permanent model default;
- granular heterogeneous table convergence and pairwise legacy-state
  generation with a fixed seed;
- exact expected column, index, primary-key, unique, check, and foreign-key
  facets, including one-field drift matrices and strict-table embedding;
- native MySQL JSON and Doka's MariaDB `longtext`/`utf8mb4_bin`/`JSON_VALID`
  representation, including strict postconditions, inventory ownership,
  invalid-value enforcement, and independent user-check drift;
- bounded EF-generated SQL defaults such as `CURRENT_TIMESTAMP(6)`, plus
  fail-closed retention of unbounded default SQL;
- bounded generated-column and CHECK expressions, including ordered searched
  `CASE` and overflow-guarded signed-integer branches; see the provider-specific
  type, storage and non-NULL proofs in [generated columns](migration-authoring.md#generated-columns);
- provider-specific index capabilities, generated MySQL/MariaDB prefix
  roundtrips, physical InnoDB key-limit boundaries, and unsupported branches;
- ordered ordinary and safe index drop/create replacement, including preserved
  duplicate-data, prerequisite, physical-key, and semantic-conflict rejection;
- InnoDB foreign-key support indexes as semantic matches for equivalent index
  ensures, without weakening exact-name or physical-facet drift;
- differently named equivalent primary-key, foreign-key, unique-constraint,
  check-constraint, and index no-ops, including composite definitions and
  multiple physical aliases, plus exact-name precedence and different-shape
  creation;
- MySQL check enforcement, MySQL index visibility, MariaDB ignored indexes and
  table-scoped check names, plus PostgreSQL index health, partition ownership,
  constraint backing, and partitioned-parent indexes;
- quotes, backslashes, mixed case, Unicode, and maximum-length identifiers;
- PostgreSQL non-default schemas, cross-schema foreign keys, and
  same-named-object isolation;
- fail-closed schema qualification across every MySQL/MariaDB operation family;
- server-provider connection-disposal guard recovery, partial-command retry,
  least privilege, and provider migration locks;
- four concurrent migrators on one server database and parallel independent
  server databases, plus two concurrent SQLite migrators against an asserted
  private-cache file database;
- normal EF operations mixed with safe operations;
- safe table, source-frozen model-managed ensure/update/delete, raw typed
  seed/update/delete-data, and following non-unique-index ordering, plus
  fail-closed unique-index projection after unanalyzed data;
- existing-table legacy projection for primary, unique, check, foreign-key, and
  index prerequisites, including empty/null-preserving row proofs, composite-
  key order, incompatible storage, and proof invalidation after provider data;
- accepted-index invalidation after ordered drops and column mutations, plus
  MySQL/MariaDB physical revalidation for single, composite, unique, primary,
  over-limit, 16-part, missing-table, replacement, row-format, and page-size
  key shapes;
- MySQL/MariaDB cross-kind unique-constraint and unique-index identity, including
  both drop/recreate directions, alias invalidation, exact-name conflicts,
  `PRIMARY` separation, and duplicate-row controls;
- maximum-length 16-column primary and unique keys matched by ordinal catalog
  rows independently of `group_concat_max_len`, with reversed-order controls;
- MySQL/MariaDB composite-index candidates whose generated catalog-reference
  count remains constant as the number of key parts grows, with functional
  classification on both engines;
- physical-identity binding for semantic unique, check, foreign-key, and index
  aliases, including drop/rename invalidation and ambiguous fail-closed
  prerequisite projection;
- exact and semantic-alias `Drop -> Ensure` replacement for primary keys,
  unique constraints, checks, foreign keys, and indexes, including live
  execution/postflight and negative intervening-DML controls on both server
  provider families;
- model-managed single/composite keys, mixed absent/source/target batches,
  unique/check conflicts, source drift, retry, idempotent replay, trigger
  postconditions, cancellation, and compare-and-swap races;
- Strict and legacy initial migrations generated through the real provider
  design-time differ, compiled, preflighted against an empty database, executed
  twice as the identical operation stream, and verified through postflight;
- independent application and derived extended migration projects, snapshots,
  assemblies, and history tables, including exact excluded-table
  model-managed-data ownership and retained shared-to-extended relationships;
- incoming model-managed delete dependencies for every supported referential
  action, including accepted ordered child/parent removal and rejection of one
  remaining or concurrently inserted dependent row;
- EF history success/failure and derived-context model-snapshot guards;
- read-only preflight, unexpected-object inventory, positive and negative
  postflight, and cancellation before and during catalog access;
- PostgreSQL-owned and caller-owned analysis transactions, including accepted
  read-only `RepeatableRead`/`Serializable` scopes and fail-closed rejection of
  read-write or weaker-isolation caller transactions.
- SQLite `main` identity, attached-database rejection, catalog normalization,
  generated columns, semantic aliases, atomic provider-owned rebuilds, and
  fail-closed unmanaged triggers, views, virtual tables, expression or partial
  indexes, `STRICT`, and `WITHOUT ROWID` boundaries.
- SQLite runtime and bundle application with identical replay, plus fail-closed
  safe-operation script generation that leaves no partial output.

The provider-analyzer contract accepts the ordered safe-operation batch. Each
provider first classifies table and referenced-column prerequisites, then
executes classification in parameterized ADO.NET batches. Optimizer-visible
statements, statements per transport batch, parameters, and UTF-8 payload are
bounded independently. MySQL/MariaDB provider plans are captured in bounded
windows. The unexpected-object inventory remains scoped to the expected table
set for child objects while retaining complete table discovery and provider-
verified semantic-alias reconciliation. Projection applies global ordered
results without per-operation catalog roundtrips, and no partial report is
published after a later failure. Every server-provider engine profile also
qualifies 100,000 deterministically ordered mixed operations. That workload covers `Missing`,
`Matching`, `Different`, `Unsupported`, `DataBlocked`, and
`PrerequisiteMissing`, `TransitionReady`, all seven planned actions, and
heterogeneous table, column, index, primary-key, unique, check, foreign-key, and
model-managed-data operations. Native
`DbBatch` execution and the sequential fallback for connections with
`CanCreateBatch == false` share the same bounded command adapter; live fallback
tests verify command order, timeout propagation, and cancellation. Coverage
jobs exclude the large-scale test because every supported engine matrix cell
already executes it against a live server.

Every engine matrix cell also runs:

- `dotnet ef database update` twice;
- normal migration script;
- idempotent script;
- idempotent no-transaction script;
- Migration Bundle twice against a separate database;
- exact Core history verification.

SQLite runs the corresponding database update and Migration Bundle paths twice.
Safe-operation SQL scripts are a qualified negative boundary because SQLite has
no procedural language for the required catalog-dependent decisions.

## Qualified capability boundaries

The common intent model remains provider neutral, while execution follows the
active engine's proven metadata and DDL capabilities. The following boundaries
distinguish qualified supported behavior from explicit rejections. Unsupported
cases stop before target DDL instead of being compared or applied heuristically:

- PostgreSQL 14 rejects `NULLS NOT DISTINCT`; PostgreSQL introduced that
  `CREATE INDEX` clause in version 15.
- Doka 10.3.0 parenthesizes `DateOnly` and `TimeOnly` typed literals in column
  defaults. The complete MySQL and MariaDB matrix qualifies the resulting DDL
  and each engine's catalog display form.
- Doka 10.3.0 emits `ClientGuid` for client-generated Guid keys, including
  application-converted relationship chains. SafeMigrations retains that
  annotation in operation identity and provider replay while comparing its
  live column as non-`AUTO_INCREMENT`. HiLo and unknown column annotations
  remain unsupported because their complete database prerequisites are not
  represented by the column catalog contract.
- Doka 10.3.0 supplies an omitted `AllowUserVariables=true` option only for
  provider-owned strings when SafeMigrations declares the capability.
  Contradictory owned values and incompatible borrowed connections or data
  sources fail closed. The provider also enforces `UseAffectedRows=false` and
  connector transport `GuidFormat=Binary16`; model-level Guid storage remains
  independently configurable as `binary(16)` or `char(36)`.
- Doka 10.3.0 exposes Guid, value-generation, and index-prefix migration
  metadata through a typed public snapshot. SafeMigrations rejects any extra,
  malformed, contradictory, or unmodeled annotation instead of inferring its
  semantics from a string key.
- Missing MySQL/MariaDB BTREE indexes are checked against the live InnoDB row
  format, page size, column encodings, store families, and explicit prefixes.
  SafeMigrations rejects an unachievable or unprovable key before provider DDL
  and never relies on server-side non-strict prefix truncation.
- Existing MySQL checks must be enforced. MySQL invisible and MariaDB ignored
  indexes do not satisfy the visible index shape emitted by ordinary EF
  operations. MariaDB check clauses are correlated by table and constraint
  name so the table-scoped names available from MariaDB 12.1 remain isolated.
- MySQL schema-wide CHECK and foreign-key symbol collisions reject before DDL.
  MariaDB foreign-key symbols reject database-wide collisions through 11.x;
  MariaDB 12.1 and later retain their documented table-scoped behavior.
- PostgreSQL rejects a non-equivalent second primary key and any schema relation
  name that prevents index creation, index rename, or creation of a primary-key
  or unique-constraint backing index. These paths produce the controlled
  different-object guard instead of a raw duplicate-relation failure.
- MariaDB's Doka-emitted JSON alias is the physical triple `longtext`,
  `utf8mb4_bin`, and the exact inline `JSON_VALID` check. Only that
  provider-generated check is excluded from strict child-object ownership.
- EF-generated SQL defaults receive typed equivalence only when the bounded
  parser proves their structure. Arbitrary SQL strings remain unsupported.
- The same structural boundary applies to EF computed-column and CHECK
  metadata. A parsed expression alone does not establish native type binding,
  required-result semantics or index eligibility; each provider must prove
  those independently. SQL Server native qualification requires the x64 matrix.
- Existing PostgreSQL indexes must be valid, ready, live, and independently
  owned. Partitioned parent indexes are supported; attached child indexes and
  constraint-owned backing indexes reject independent ensure, drop, or rename
  operations.
- MySQL, MariaDB 10.11, and MariaDB 11.4 reject a `Guid` literal default stored
  as `BINARY` because `INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` does not
  preserve a complete value for repeatable semantic comparison. MariaDB 11.8
  and 12.3 preserve the complete expression and are qualified for missing,
  matching, different, retry, preflight, and EF-pipeline behavior.

These are complete fail-closed outcomes, not silent degradation. A provider or
server update may remove a boundary only after the same missing, matching,
different, retry, preflight, and EF-pipeline evidence passes for the changed
capability.

## Coverage gate

The release workflow runs all five test assemblies. Server adapters use pinned
MariaDB 11.8, PostgreSQL 18, and SQL Server 2022 images; SQLite uses its locked
in-process runtime with Microsoft's built-in code-coverage
collector. `eng/verify-coverage.py` conservatively merges Cobertura line and
branch evidence by product source line and excludes test and third-party
assemblies by exact package name.

`eng/coverage-thresholds.json` is a blocking floor, not a quality target:

| Product assembly | Line floor | Branch floor |
| --- | ---: | ---: |
| Core | 92% | 80% |
| MySQL/MariaDB adapter | 92% | 75% |
| PostgreSQL adapter | 94% | 84% |
| SQLite adapter | 90% | 75% |
| SQL Server adapter | 90% | 75% |

The behavioral and engine matrices remain mandatory even when the numeric
floor passes. A threshold reduction requires reviewed evidence and must not be
used to hide an uncovered regression.

## Performance and memory

`eng/performance-budgets.json` defines explicit Core, MySQL/MariaDB,
PostgreSQL, SQLite, and SQL Server benchmark sets with duration baselines,
coarse duration comparison limits, and allocation comparison limits at 1, 100, and
1000 operations.

All five benchmark sets run in `--report-only` mode in CI and release
qualification. Duration and allocation overruns remain visible in console output
as `EXCEEDED (informational)` and in JSON as `passed: false`, but do not reject
qualification on shared hardware. The complete measurements and unchanged limits
remain in `artifacts/performance`. Invalid configuration, missing/duplicate/unknown
workloads, execution exceptions and report-write failures still fail the run.
Build, tests, coverage, live-engine, tooling, package and SBOM gates remain blocking.
Omit `--report-only` for a deliberately strict manual budget comparison.

Prepared-input CPU regressions separately bound shared seed and diagnostic
evidence parsing, accepted-column projection, MySQL/MariaDB runtime generation
and SQL Server catalog-string assembly allocations. Runtime-generation SQL
fingerprints retain exact MySQL/MariaDB guards, statement order and cleanup;
PostgreSQL text and SQLite catalog regressions cover whitespace, comments,
quoted tokens and ordered keys. They exclude input construction and asynchronous
database latency. Parsed evidence owns its final arrays and field strings;
caller-owned evidence still receives defensive snapshots. These checks do not
measure disk flush latency, establish an end-to-end migration latency claim,
or replace live migration, recovery and native SQL Server qualification.

For source-labelled raw samples and exact output/transport counterprobes, use the
provider projects' [maintenance attribution mode](../CONTRIBUTING.md#maintenance-attribution).
These prepared client measurements do not establish native engine performance.

The SQL Server set declares eleven generation, analyzer-plan, model-managed
data, repair, and canonical-model cases. Its initial duration values are the
exact medians from the five-sample macOS ARM64 developer capture at
`2026-09-30T18:07:25.269536+00:00`, running .NET `10.0.12` on `Unix 26.5.2`.
The evidence file is `artifacts/performance/sqlserver-baseline-macos.json`,
SHA-256 `6803e8aa94a3b512b80f2f66d6de700f813a08d2467db0732dadcf7ff5c1ddde`.
Each duration retains the existing 200% regression tolerance. Allocation caps
are the captured median plus 25%, rounded upward to the next 65,536-byte block;
the block minimum gives very small cases more than 25% headroom.

This capture is explicitly measurement-only and is not GitHub-hosted x64
qualification. The quality workflow can retain a separate x64 capture in
`sqlserver-baseline.json`; its ordinary benchmark report is also retained.
SQL Server qualification requires the complete pinned x64 workflow.
SQL Server live workloads append timestamped stage markers
under `artifacts/performance/live/sqlserver-*-progress.log` before potentially
long-running phases. The existing always-upload artifact preserves these markers
even when a job does not produce its final TRX. They contain fixed workload/stage
identifiers, not SQL, database names or connection strings; they are diagnostics,
not successful qualification evidence.

The existing diagnostics ActivitySource optionally emits
`safe_migrations.analysis.stage` activities for provider baseline capture,
classification, bounded catalog batches, ordered projection and unexpected
inventory. Only fixed stage labels, operation counts, batch/parameter counts
and payload sizes are tagged, never SQL, object names or values. With no
listeners no stage activity is allocated. SQL Server stress workloads retain
start/completion markers and elapsed milliseconds for each stage; independent
workloads are isolated by trace identity. Missing completion evidence still
means the phase did not finish, not that qualification passed. Actual SQL
Server 2019/2022/2025 x86-64 durations must be established by the pinned live
cells; local ARM source tests cannot establish timeout closure.

The five independently restored benchmark projects
enforce the Core and provider dependency boundaries for:

- intent construction;
- decision planning;
- MySQL handler/generator output;
- PostgreSQL adapter output;
- SQL Server adapter output;
- canonical snapshot initialization, relational model differ, and fingerprint;
- report JSON serialization;
- blocker-view serialization across 50,000 assessments with a small selected
  result and no filtered collection;
- model-managed intent construction and contract fingerprinting at 384 row
  transitions;
- provider model-managed command generation and analyzer-plan construction at
  384 row transitions.

The SQL Server repair cases generate and build analyzer plans for 1,000 actual
`RepairIfSafe` alterations from nullable `nvarchar(64)`/maximum length 64 to
nullable `nvarchar(128)`/maximum length 128. Source and target definitions have
no defaults, comments, or provider annotations. This supported text-widening
workload does not substitute ordinary generation or an unsupported-operation
classification for the repair path; startup rejects a plan that is unsupported
or lacks safe repair capability.

Benchmark duration and allocation ceilings are informational comparison limits,
not blocking CI gates. Wall-clock measurements on shared GitHub-hosted runners
are not deterministic; their three-times-baseline comparisons are regression
evidence, not throughput claims. Budget evaluation remains unchanged in the
reports rather than redefining an overrun as a passed measurement.
Changes to a baseline or ceiling require captured before/after evidence on the
same pinned `ubuntu-26.04` runner label and a review of asymptotic behavior; a
budget must not be raised merely to make CI green.
Earlier host measurements keep their actual OS and architecture attribution;
do not relabel an Ubuntu 24.04 result as a 26.04 baseline or compare the hosts
as though they were the same environment.

The generation and analyzer-plan capture does not open a server connection,
so it can also run locally on ARM64 for diagnostic evidence. That output
records its architecture and is not x64 Linux qualification evidence. Run:

```sh
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks.csproj --configuration Release -- --capture-baseline --output artifacts/performance/sqlserver-baseline.json
```

The command records medians without a pass/fail verdict. Its output must not
be copied into `eng/performance-budgets.json` mechanically: review the
operation counts, allocation scaling, server-independent workload shape, and
runner identity first. The ordinary invocation without `--capture-baseline`
remains fail-closed while any declared budget is absent.

These initial budgets retain their macOS ARM64 developer-measurement
provenance. They do not establish a measured `ubuntu-26.04` x64 baseline or a
same-hardware hosted comparison; those remain separate performance evidence.

For capture on the pinned x64 Linux runner, dispatch the existing CI workflow
with `capture-sqlserver-baseline` enabled. The optional capture retains a
measurement-only `sqlserver-baseline.json` artifact. All blocking qualification
gates and the informational SQL Server budget report still run. The diagnostic
capture precedes budget reporting; neither a duration nor allocation overrun
in that report rejects the run.

The MySQL/MariaDB benchmark has no Npgsql or SQLite dependency, the PostgreSQL
benchmark has no Doka MySQL or SQLite dependency, and the SQLite benchmark has
no server-provider dependency. Shared measurement and workload source is linked
at compile time; no benchmark assembly introduces a cross-provider runtime edge.

Every server-provider engine cell additionally runs 20 complete pooled
`SafeMigrationRunner` invocations against 100 expected tables, then repeats them
after adding 1,000 foreign tables with child objects. The cell stores p50/p95
JSON evidence and still fails unless assessments are unchanged and foreign
child rows remain excluded by the expected-table scope. The unchanged noisy
p95 comparison `2 * clean p95 + 250 ms` is informational: JSON records
`evaluationMode: "informational"` and preserves `requirement.passed: false` when
the measured limit is exceeded. Invalid evidence, execution exceptions and
artifact failures remain fatal. Each invocation can include multiple database
roundtrips; same-runner timing context is neither a hardware-dependent CI gate
nor an absolute cross-machine latency promise.

Every server-provider engine cell also analyzes 100,000 ordered mixed operations whose
observations cover every state and planner action, including model-managed
ensure, update, and delete. The analysis workload also includes accepted
`VARCHAR` widening, accepted narrowing, data-blocked narrowing, and the
MySQL/MariaDB Boolean repair; it is not a matching/no-op-only benchmark.
Separate live execution tests apply and replay at least 50,000 mixed absent,
source, and already-target row transitions using the production
128-row/4,096-cell partitioning. Their JSON evidence records elapsed time,
managed allocations, command count, maximum batch rows/cells, and maximum
generated command bytes. These are scalability and allocation gates, not an
absolute wall-clock promise across different runner hardware.

SQLite has engine-appropriate gates instead of inheriting server-transport
claims. Its live suite applies and replays 50,000 mixed model-managed row
transitions. Its allocation and duration budgets analyze the same 384-row
operation set against both a 200-table catalog and a 1,000-table catalog whose
foreign tables carry indexes and foreign keys. The latter intentionally
measures whole-catalog discovery cost and prevents that cost from remaining
outside the release budget.

After restoring the locked publishable package graphs, the quality workflow
rejects warning-level Roslyn style violations and unnecessary imports.
Rider/ReSharper remains the repository
formatter for layout rules that Roslyn cannot represent.

## Package and supply-chain evidence

`eng/qualify-packages.sh`:

1. packs the same Release build twice;
2. compares all five `.nupkg` and five `.snupkg` files byte-for-byte;
3. verifies the exact file set, metadata, dependency shape, assemblies, XML,
   symbols, README, license, and report schemas;
4. builds and runs an isolated consumer using packages only;
5. emits sorted SHA-256 checksums.

The four provider package-consumer fixtures, plus the split MySQL package
topology, are normal Solution projects in local `Source` mode so Rider loads
their complete C# and MSBuild models. That mode
uses the matching provider `ProjectReference` and participates in locked
Solution restore, formatting, and build gates. Package qualification copies
the fixtures into a temporary root and explicitly selects `Package` mode. That
mode removes every `ProjectReference`, consumes the generated packages, and
fails if the restored asset graph contains a project dependency. The local IDE
path therefore remains maintainable without weakening package-boundary
evidence.

The Microsoft SBOM Tool binary is downloaded at version 4.1.5 and verified
against the platform-specific release digest before execution. The generated
SPDX 2.2 manifest must validate all ten packages plus the checksum file and
contain the required resolved package graph.

Every future release adds GitHub/Sigstore build provenance and SBOM
attestations, a portable `release-provenance.intoto.jsonl` asset, NuGet Trusted
Publishing, NuGet repository-signature verification, and a content readback
that differs from the qualified package only by NuGet's `.signature.p7s`
entry. Publication validates the exact SLSA subject inventory and verifies the
portable bundle against the release workflow and qualified commit before the
protected environment obtains a short-lived NuGet credential. The final
thirteen-asset immutable GitHub Release is then covered by GitHub's native Release
and release-asset verification.

## Primary references

- [GitHub-hosted runner Ubuntu 26.04 migration announcement](https://github.com/actions/runner-images/issues/14748),
  retrieved 2026-09-21 and rechecked 2026-10-25.
- [GitHub-hosted runner labels and x64 resources](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
  and [Ubuntu 26.04 image software](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2604-Readme.md),
  retrieved 2026-10-25. The image's preinstalled SDK is not the repository's
  SDK selection; `setup-dotnet` continues to install the exact `global.json` version.
- [.NET 10 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json),
  retrieved 2026-10-20; SDK 10.0.402 carries runtime 10.0.13.
- [EF Core Relational 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational/10.0.13),
  [Dependency Injection Abstractions 10.0.13 package](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.13),
  [Npgsql EF Core 10.0.3 package](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
  [EF Core SQLite Core 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite.Core/10.0.13),
  [EF Core SQL Server 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.13),
  and [EF Core custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions),
  retrieved 2026-10-20.
- [Doka 10.2.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.2.0),
  [release notes](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/CHANGELOG.md),
  [provider configuration](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/provider-configuration.md),
  [ownership-aware connection decision](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/decisions/D-029-ownership-aware-connection-invariants.md),
  [value-generation strategies](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/src/Doka.EntityFrameworkCore.MySql/MySqlValueGenerationStrategy.cs),
  and [migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/migration-operation-handlers.md),
  retrieved 2026-08-31.
- [Doka 10.3.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.3.0),
  [release notes](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/CHANGELOG.md),
  [migration-operation metadata](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/src/Doka.EntityFrameworkCore.MySql/Migrations/MySqlMigrationOperationMetadata.cs),
  and [signed release tag](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.3.0),
  retrieved 2026-09-01.
- [Doka 10.4.2 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.4.2),
  [migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.4.2/docs/migration-operation-handlers.md),
  and [signed release tag](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.4.2),
  retrieved 2026-09-21.
- [Doka 10.5.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.5.0),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.5.0),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.14.23.33.52/doka.entityframeworkcore.mysql.10.5.0.json),
  and [GUID migration contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/27a8d3cce3ce2bc64f6efd1e9034b1cd66eb1ff0/docs/operations/migrations.md#guid-representation-changes),
  retrieved 2026-10-15.
- [Doka 10.6.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.0),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.0),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.20.06.45.24/doka.entityframeworkcore.mysql.10.6.0.json),
  and [physical index-budget contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/d1e40a7b211c10b354e5aa6df80d9fea710d8f58/docs/provider-configuration.md#index-key-byte-fidelity),
  retrieved 2026-10-20.
- [Doka 10.6.1 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.1),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.1),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.22.06.05.55/doka.entityframeworkcore.mysql.10.6.1.json),
  and [GUID SQL-default contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/53c8fbd7ae8987b5c376250fe4d6296d729669cf/docs/operations/migrations.md#guid-sql-defaults),
  retrieved 2026-10-22.
- [Doka 10.6.2 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.2),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.2),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.25.00.38.58/doka.entityframeworkcore.mysql.10.6.2.json),
  and [GUID SQL-default contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/6e2618140d821bd6befdf46181fcb82dca323c4f/docs/operations/migrations.md#guid-sql-defaults),
  retrieved 2026-10-25.
- [MySQL 8.4 InnoDB limits](https://dev.mysql.com/doc/refman/8.4/en/innodb-limits.html),
  [MySQL 8.4 CREATE INDEX](https://dev.mysql.com/doc/refman/8.4/en/create-index.html),
  [MySQL 8.4 invisible indexes](https://dev.mysql.com/doc/refman/8.4/en/invisible-indexes.html),
  [MySQL 8.4 CHECK constraints](https://dev.mysql.com/doc/refman/8.4/en/create-table-check-constraints.html),
  [MySQL 8.4 foreign keys](https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html),
  [MariaDB InnoDB limitations](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-limitations),
  [MariaDB InnoDB row formats](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-row-formats/innodb-row-formats-overview),
  and [MariaDB ignored indexes](https://mariadb.com/docs/server/ha-and-performance/optimization-and-tuning/optimization-and-indexes/ignored-indexes),
  retrieved 2026-09-01.
- [MariaDB 12.1 changes](https://mariadb.com/docs/release-notes/community-server/12.1/changes-and-improvements-in-mariadb-12.1),
  retrieved 2026-09-02.
- [PostgreSQL 17 `pg_constraint`](https://www.postgresql.org/docs/17/catalog-pg-constraint.html),
  and [PostgreSQL 18 `CREATE INDEX`](https://www.postgresql.org/docs/18/sql-createindex.html),
  retrieved 2026-09-02.
- [PostgreSQL 18 `pg_index`](https://www.postgresql.org/docs/18/catalog-pg-index.html),
  [`pg_class`](https://www.postgresql.org/docs/18/catalog-pg-class.html), and
  [`pg_inherits`](https://www.postgresql.org/docs/18/catalog-pg-inherits.html),
  retrieved 2026-09-01.
- [EF Core generated values](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties),
  retrieved 2026-08-29.
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
  and [18.6/17.11/16.15/15.19/14.24 announcement](https://www.postgresql.org/about/news/postgresql-186-1711-1615-1519-1424-and-19-beta-3-released-3365/),
  retrieved 2026-08-27.
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html),
  [MySQL 8.4.11 release notes](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/news-8-4-11.html),
  and [MySQL 9.7.2 release notes](https://dev.mysql.com/doc/relnotes/mysql/9.7/en/news-9-7-2.html),
  retrieved 2026-08-27.
- [MariaDB 10.11.18, 11.4.12, and 11.8.8 release announcement](https://mariadb.com/resources/blog/mariadb-community-server-q2-2026-corrective-releases/),
  [MariaDB 12.3 release inventory](https://mariadb.org/mariadb/all-releases/),
  and [MariaDB release model](https://mariadb.com/docs/release-notes/community-server/about/release-model),
  retrieved 2026-08-27.
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds),
  [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
  and [Microsoft SBOM Tool](https://github.com/microsoft/sbom-tool), retrieved
  2026-08-27.
