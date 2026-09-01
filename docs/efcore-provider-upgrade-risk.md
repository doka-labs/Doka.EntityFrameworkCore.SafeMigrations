# EF Core and provider upgrade boundary

SafeMigrations integrates with two different public provider boundaries. An
upgrade is accepted only after the full locked restore, package, engine, and
tooling gates pass; compilation alone is insufficient.

## MySQL and MariaDB boundary

The `.MySql` package consumes the public
`Doka.EntityFrameworkCore.MySql` operation-handler SPI. Its types use the root
namespace, not a `.Migrations` namespace:

- `IMySqlMigrationOperationHandler`;
- `MySqlMigrationOperationContext`;
- `RenderStandardOperation`;
- immutable handler result and command contracts;
- canonical engine feature projection.

SafeMigrations registers one exact handler for `SafeMigrationOperation`. It
does not replace or derive from the Doka migrations generator. This removes the
former Pomelo/internal-generator coupling, but the SPI remains a versioned
binary and behavioral dependency. A Doka update must preserve exact dispatch,
baseline rendering, command ordering, feature projection, diagnostics, and
failure behavior.

The read-only analyzer captures SafeMigrations' typed runtime plan while Doka
invokes the registered handler with the real server-version, feature, and
operation-ordinal context. It does not parse generated commands. The locked
Doka 10.3.0 contract exposes provider-validated `Setup`, `Body`, and `Cleanup`
fragments and a bounded `CreateScoped` command contract. SafeMigrations uses
those fragments directly and returns one provider-executed scope per guarded
operation. Doka runs cleanup after success, failure, or cancellation with an
independent cancellation token; a cleanup failure closes the connection and
evicts its physical session from the MySqlConnector pool.

Doka 10.3.0 supports two distinct model-level Guid contracts. Application-owned converters
are preserved through relationship chains; their generated key columns retain
the provider CLR type, carry no provider-owned `GuidFormat`, and may carry
`ClientGuid`. Native Doka Guid mappings instead emit
`Doka:MySql:GuidFormat` with `Binary16` plus `binary(16)` or `Char36` plus
`char(36)`. SafeMigrations accepts only those exact Guid CLR/store-type pairs.
Undefined enum values, contradictory store types, non-Guid CLR types, HiLo,
and unknown provider facets remain unsupported before target DDL. Both accepted
Guid formats remain part of immutable operation snapshots, fingerprints,
provider DDL replay, and catalog-shape comparison.

Doka 10.3.0 additionally centralizes three connector invariants across
provider-owned strings, caller-owned connections, and caller-owned data
sources. SafeMigrations declares `RequireUserVariables()` during registration.
Doka supplies an omitted `AllowUserVariables=true` option only on the owned
string path and validates borrowed inputs without mutation. Doka also requires
`UseAffectedRows=false` and `GuidFormat=Binary16` on every connection path.
The latter is a low-level wire contract and does not replace the independently
configurable model-level `Binary16` or `Char36` storage contract.

Doka 10.3.0 also exposes an immutable typed migration-operation metadata
snapshot for Guid format, value-generation strategy, and ordered index prefix
lengths. SafeMigrations uses that public contract at both runtime and design
time and verifies that it accounts for every annotation on the owned
operation. Adding a provider annotation without extending the typed snapshot,
changing zero-prefix semantics, or relaxing shape validation is therefore a
behavioral SPI change even when the handler interface remains binary-compatible.

## PostgreSQL boundary

The `.PostgreSql` package composes Npgsql's
`NpgsqlMigrationsSqlGenerator` behind `IMigrationsSqlGenerator`. Ordinary EF
operations are delegated unchanged. Safe operations are classified and wrapped
by SafeMigrations before provider-rendered commands are returned.

Npgsql generator constructor changes, migration command semantics, catalog
representation, or SQL normalization can affect the adapter even when the
project still compiles. PostgreSQL major upgrades can also change catalog
shape. PostgreSQL 18, for example, exposes `NOT NULL` constraints as catalog
constraint rows, so tests must select owned constraint families rather than
count every `pg_constraint` row.

PostgreSQL performs identifier quoting through `pg_catalog.quote_ident`, but
SafeMigrations still predicts version-sensitive decompiled forms returned by
`pg_catalog.pg_get_expr` through
`PostgreSqlSafeMigrationSqlExpressionRenderer.RenderCatalogDeparsedCandidateSql`:
`::` casts, `IN` rendered as `= ANY (ARRAY[...])`,
`NOT IN` rendered as `<> ALL (ARRAY[...])`, expanded `BETWEEN` predicates, and
binary-expression parentheses. `pg_get_expr` decompiles PostgreSQL's internal
expression tree; it does not preserve the originally submitted SQL text. Every
supported PostgreSQL major must therefore converge through the expression
matrix. A previously unseen form fails closed and must not be admitted by
unrestricted text normalization.

Applications with a custom Npgsql migrations generator select it through the
typed SafeMigrations registration overload. Upgrade tests must prove that
ordinary operations, standard baselines inside safe operations, custom index
SQL, scripts, and transaction-suppression boundaries continue through that
selected generator.

## Design-time C# generation boundary

SafeMigrations composes EF Core's public design-time service contracts, but the
exact C# text emitted by EF's migration generators is not a compatibility
contract. SafeMigrations validates the leading operation call, controlled array
literals, outer namespace block, and indentation before substituting safe calls
or file-scoped source. A missing, duplicated, or newly formatted shape stops
scaffolding instead of emitting ambiguous migration code.

The active provider `IMigrationsCodeGenerator` is selected and decorated, not
replaced. EF Core loads referenced design-time services before provider and
default services, so SafeMigrations defers decoration through
`IMigrationsCodeGeneratorSelector` until the full generator set exists. The
selector preserves EF Core's legacy precedence and case-insensitive last-match
language behavior. Provider metadata and snapshot source pass through unchanged
so model namespaces, typed literals, and provider-specific snapshot rendering
remain under provider ownership. SafeMigrations post-processes only migration
source after the provider has rendered it. When SafeMigrations scaffolding is
disabled, the selector delegates every provider language unchanged. When it is
enabled, an unsupported non-C# request fails closed before a C# decorator can
touch that source. A genuinely missing generator always fails at selection.

Inserted arguments and outer-source rewrites derive LF or CRLF from the
provider-generated source instead of assuming the build host's convention.
Mixed line endings and standalone carriage returns fail closed before source is
returned.

The finalized migration source contains exactly one explicit
`using Doka.EntityFrameworkCore.SafeMigrations;` directive. Unit tests reject a
missing EF namespace anchor and duplicate directives; EF tooling and package-
only consumer gates verify that generated source compiles without a masking
global SafeMigrations using.

Provider `buildTransitive` assets recognize both official EF tooling package
layouts: a direct `Microsoft.EntityFrameworkCore.Design` reference and a direct
`Microsoft.EntityFrameworkCore.Tools` reference whose package contract supplies
Design transitively. A runtime-only project with neither package remains free
of design-time attributes and warnings. Package qualification must prove all
three layouts for both providers; the first two must scaffold safe source, and
the runtime-only layout must be rejected by EF tooling before source is written.

Every EF Core or EF Tools update must therefore rerun strict and legacy
scaffolding, generated-source compilation, and the package-only dependency
matrix. Compilation of SafeMigrations itself is not sufficient evidence because
an upstream generator can retain the public service API while changing emitted
source shape.

## Model fingerprint boundary

The persisted fingerprint uses the versioned
`safe-relational-model:v1:<provider-contract>:sha256:<digest>` format over
canonical relational metadata. It does not use `IModel.ToDebugString`, whose
format EF documents as debugging-only and unstable. Provider upgrades must
test every migration-relevant annotation: unknown value shapes fail closed
rather than disappearing from the digest. EF Core JSON container columns have
no scalar property mappings; fingerprinting therefore consumes their public
`IColumn` facets and `TryGetDefaultValue` contract instead of indexing the
property-mapping collection. Ordinary scalar columns retain the direct mapping
path so this compatibility fix does not multiply allocations across large
models.

Facet-isolation tests and provider-specific golden digests run against the
committed locked dependency graph. The exact canonical snapshot initialization,
`IMigrationsModelDiffer`, and fingerprint path used by the runner is covered by
provider duration/allocation budgets.

## Qualified dependency ranges

Central package declarations use bounded ranges. Lockfiles establish the exact
qualified graph. Dependabot updates the declarations and lockfiles through a
reviewed pull request, where the complete provider, tooling, package, coverage,
and performance workflow runs against the proposed graph.

The declared range must never be widened beyond the profiles actually tested.
Publishable SafeMigrations candidates and stable packages must depend on a
stable Doka package; the package content gate rejects a prerelease Doka
dependency during every release qualification.

The Doka boundary therefore stops at the next minor rather than the next major.
Patch releases within the qualified minor line follow Doka's compatible-fix
contract, while a new minor may revise the binary or behavioral SPI and requires
an explicit range change plus the complete upgrade evidence below. Committed
lockfiles keep repository builds reproducible at the exact qualified patch.

## Required upgrade evidence

Every EF, Doka, Npgsql, or supported database update requires:

1. locked restore and warning-free Release build;
2. review of the proposed declarations and exact lockfile resolutions;
3. core planner, fingerprint, definition, report, and model-guard tests;
4. all supported MySQL/MariaDB and PostgreSQL engine endpoints;
5. missing, matching, different, unsupported, and data-blocked states;
6. `Database.MigrateAsync`, `IMigrator`, history, missing/conflicting adapter,
   parallel migrator, least-privilege, and recovery tests;
7. normal, idempotent, and no-transaction script generation;
8. strict and legacy scaffolding with no prefix, a full-key zero prefix, and a
   positive single/composite prefix, followed by `dotnet ef database update`
   and Migration Bundle;
9. deterministic pack, exact contents, package-only consumer, and Public API
   validation;
10. performance/allocation budgets, pooled clean/noisy live p95 evidence, and
    SPDX SBOM validation.

If any behavior changes, update expected definitions or provider logic only
after determining whether the new behavior is semantically correct for every
supported engine. Do not normalize a failing comparison until quoted literals,
identifier case, ordering, and provider semantics have been checked.

## Version research

Version pins and support statements are fast-stale. Update them from primary
sources at author time. The following references were rechecked on 2026-08-27
unless the entry records a later date:

- [.NET and .NET SDK support policy](https://dotnet.microsoft.com/platform/support/policy)
- [EF Core releases and planning](https://learn.microsoft.com/ef/core/what-is-new/)
- [EF Core design-time tools architecture](https://learn.microsoft.com/en-us/ef/core/miscellaneous/internals/tools)
- [EF Core `DesignTimeServicesReferenceAttribute`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.design.designtimeservicesreferenceattribute?view=efcore-10.0)
- [NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning)
- [Npgsql EF Core 10.0 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
- [PostgreSQL system information functions](https://www.postgresql.org/docs/current/functions-info.html)
- [PostgreSQL 14 `pg_constraint` catalog](https://www.postgresql.org/docs/14/catalog-pg-constraint.html)
  (rechecked 2026-09-01)
- [PostgreSQL 18 `pg_constraint` catalog](https://www.postgresql.org/docs/18/catalog-pg-constraint.html)
  (rechecked 2026-09-01)
- [PostgreSQL 18 `pg_index` catalog](https://www.postgresql.org/docs/18/catalog-pg-index.html),
  [`pg_class` catalog](https://www.postgresql.org/docs/18/catalog-pg-class.html), and
  [`pg_inherits` catalog](https://www.postgresql.org/docs/18/catalog-pg-inherits.html)
  (rechecked 2026-09-01)
- [PostgreSQL 15 constraint syntax and semantics](https://www.postgresql.org/docs/15/sql-createtable.html)
  (rechecked 2026-09-01)
- [MySQL 8.4 check constraints](https://dev.mysql.com/doc/refman/8.4/en/create-table-check-constraints.html)
  and [invisible indexes](https://dev.mysql.com/doc/refman/8.4/en/invisible-indexes.html)
  (rechecked 2026-09-01)
- [MariaDB ignored indexes](https://mariadb.com/docs/server/ha-and-performance/optimization-and-tuning/optimization-and-indexes/ignored-indexes)
  and [Information Schema `STATISTICS`](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-statistics-table)
  (rechecked 2026-09-01)
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html)
- [MariaDB release criteria](https://mariadb.com/docs/release-notes/mariadb-release-criteria)
- [Doka.EntityFrameworkCore.MySql 10.2.0](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.2.0)
  (rechecked 2026-08-31)
- [Doka 10.2.0 migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/migration-operation-handlers.md)
  (rechecked 2026-08-31)
- [Doka 10.2.0 provider configuration](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/provider-configuration.md)
  (rechecked 2026-08-31)
- [Doka D-029 ownership-aware connection invariants](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/decisions/D-029-ownership-aware-connection-invariants.md)
  (rechecked 2026-08-31)
- [Doka 10.2.0 Guid-format contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/src/Doka.EntityFrameworkCore.MySql/MySqlGuidFormat.cs)
  (rechecked 2026-08-31)
- [Doka 10.2.0 Guid property configuration](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/src/Doka.EntityFrameworkCore.MySql/MySqlPropertyBuilderExtensions.cs)
  (rechecked 2026-08-31)
- [Doka.EntityFrameworkCore.MySql 10.3.0](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.3.0)
  (rechecked 2026-09-01)
- [Doka 10.3.0 migration-operation metadata](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/src/Doka.EntityFrameworkCore.MySql/Migrations/MySqlMigrationOperationMetadata.cs)
  (rechecked 2026-09-01)
- [Doka 10.3.0 signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.3.0)
  (rechecked 2026-09-01)

The exact release evidence belongs in the workflow run, lockfiles, package
SBOM, and final plan-to-ship reconciliation, not in an unchecked comment.
