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
operation-ordinal context. It does not parse generated commands. The exact
Doka 10.0.0 package exposes provider-validated `Setup`, `Body`, and `Cleanup`
fragments and a bounded `CreateScoped` command contract. SafeMigrations uses
those fragments directly and returns one provider-executed scope per guarded
operation. Doka runs cleanup after success, failure, or cancellation with an
independent cancellation token; a cleanup failure closes the connection and
evicts its physical session from the MySqlConnector pool.

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
rather than disappearing from the digest.

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
8. `dotnet ef database update` and Migration Bundle;
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
sources at author time:

- [.NET and .NET SDK support policy](https://dotnet.microsoft.com/platform/support/policy)
- [EF Core releases and planning](https://learn.microsoft.com/ef/core/what-is-new/)
- [NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning)
- [Npgsql EF Core release notes](https://www.npgsql.org/efcore/release-notes/)
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
- [PostgreSQL system information functions](https://www.postgresql.org/docs/current/functions-info.html)
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html)
- [MariaDB release criteria](https://mariadb.org/about/release-criteria/)
- [Doka.EntityFrameworkCore.MySql repository](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql)

The exact release evidence belongs in the workflow run, lockfiles, package
SBOM, and final plan-to-ship reconciliation, not in an unchecked comment.
