---
id: D-008
status: implemented
date: 2026-08-27
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Automatic generation of safe table and index migration source from the canonical EF model"
supersedes: []
superseded-by: []
amends: [D-002]
amended-by: [D-009]
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-008 -- Scaffold source-frozen safe table migrations through EF design services

## Context and Problem Statement

D-002 established an explicit granular convergence contract for heterogeneous
legacy schemas. Requiring an author to copy EF's generated `CreateTable` body
into a hand-built `ExpectedTableDefinition`, and then manually translate
indexes, is error-prone and makes the safe path less usable than ordinary EF
migrations. It can also lose provider facets such as identity generation.

The decision is how SafeMigrations can generate reviewable strict or legacy
convergence source from the canonical EF model without forking EF Core's C#
generator, adding runtime discovery, or silently assigning policies to every
ordinary migration operation.

## Decision Drivers

- Normal `dotnet ef migrations add` must produce the safe table baseline.
- Strict table creation must be the default; heterogeneous legacy adoption must
  be an explicit design-time selection.
- Existing generated migrations must never change meaning when options change.
- Legacy repair must be an explicit source-frozen choice with a conservative,
  provider-verified allowlist.
- Provider arguments and column annotations must survive generation, catalog
  comparison, fingerprints, and baseline DDL.
- Unsupported generator shapes and unmodeled annotations must fail closed.
- Runtime service-provider caching must not be fragmented by a design-only
  option.
- Generated migration bodies must satisfy the repository's analyzers without
  suppressing diagnostics or hiding reviewable source as generated code.
- The implementation must use EF Core and NuGet extension contracts rather
  than reflection-based discovery or copied provider generators.

## Considered Options

- Compose EF Core design-time generators and substitute reviewed operation calls
- Require manual expected-definition construction in every migration
- Rewrite every EF migration operation automatically
- Fork the EF Core and provider C# migration generators

## Decision Outcome

Chosen option: "Compose EF Core design-time generators and substitute reviewed
operation calls", because it preserves EF/provider source generation while
making the established safe table contract the normal authoring path.

Each provider registration accepts an optional `SafeMigrationOptionsBuilder`.
Its default `Strict` mode writes `CreateTableIfNotExists`, safe single- or
multi-column index helpers, and `DropTableIfExists`. `LegacyConvergence` writes
`ConvergeTableFromModel` and the same safe index helpers. The legacy `Down`
method throws before DDL because the migration cannot prove which objects
predated it. The selected calls are literal C# source, so later option changes
affect only future migrations.

`UseLegacyConvergencePolicy` selects the policy written into every newly
generated `ConvergeTableFromModel` call. `ThrowIfDifferent` remains the default.
`RepairIfSafe` is accepted only with `LegacyConvergence`; `ExistenceOnly` is not
a valid generated child policy because it would hide definition drift.

Automatic column repair is limited to nullability, default, and comment on an
ordinary column whose invariant catalog shape already matches. Store type,
collation, generated/identity state, and row-version state are invariants.
Provider-neutral Core does not repair annotation-bearing definitions; the
MySQL/MariaDB adapter may authorize one only when Doka's typed public contract
recognizes every annotation and proves it consistent with the column shape.
Existing nulls block nullability tightening. MySQL and MariaDB use Doka's
complete-definition `MODIFY COLUMN` rendering; PostgreSQL uses Npgsql's
facet-specific `ALTER TABLE` rendering. Apply and repair remain separate
guarded branches with a shared target postcondition.

SafeMigrations registers `IDesignTimeServices` through EF's
`DesignTimeServicesReferenceAttribute`. Provider-package `buildTransitive`
assets add that attribute when the startup project directly references
`Microsoft.EntityFrameworkCore.Design` or `Microsoft.EntityFrameworkCore.Tools`;
the latter supplies Design transitively. Runtime-only projects with neither
package receive no design-time attribute. EF Core invokes referenced services
before provider and default design-time services, so SafeMigrations registers a
deferred `IMigrationsCodeGeneratorSelector`. At selection time it preserves EF
Core's legacy precedence and case-insensitive last-language match, then decorates
the selected provider `IMigrationsCodeGenerator`. Migration metadata and
snapshots pass through unchanged, preserving provider-owned model namespace
discovery and rendering. SafeMigrations asks EF Core to render each supported
migration operation and replaces only one validated leading method token. Any
missing, repeated, or non-leading token stops scaffolding. Disabled
SafeMigrations scaffolding delegates provider languages unchanged. Enabled
scaffolding accepts C# only and rejects an unsupported language before applying
the C# decorator. A genuinely missing generator always fails at selection.

SafeMigrations also converts the validated outer migration namespace to the
repository's file-scoped form and rewrites only known one-dimensional array
arguments under its table/index boundary to collection expressions. This keeps
generated migrations analyzer-compatible without changing provider values or
disabling analysis. Every insertion preserves the LF or CRLF convention found
in provider-generated source; mixed line endings and standalone carriage
returns fail closed.

Automatic rewriting covers `CreateTable`, `CreateIndex`, `DropIndex`, and
`DropTable`.
Add/alter/drop column, constraint, rename, and schema operations remain ordinary
EF operations unless the author chooses the corresponding explicit
SafeMigrations API. This keeps policy selection explicit where ownership,
repair, or destructive semantics cannot be inferred from the current model.

The typed table callback captures EF's column and constraint operations into
immutable definitions. Provider column annotations use a closed snapshot value
set, deterministic order, and fingerprints. MySQL/MariaDB compares Doka's
`None` and auto-increment strategies with `INFORMATION_SCHEMA.COLUMNS.EXTRA`.
It accepts native Guid-format annotations only for Doka's defined
`Binary16`/`binary(16)` and `Char36`/`char(36)` pairs on Guid CLR columns.
PostgreSQL compares `None`, identity-always, and identity-by-default with
`pg_attribute.attidentity`. Unknown value types fail capture. Undefined,
contradictory, or unmodeled operation annotations classify unsupported before
target DDL.

MySQL/MariaDB design services register one provider-owned create-index
projector. It consumes Doka's typed prefix snapshot, copies the EF operation
without the consumed provider annotation, and emits one explicit non-negative
prefix entry per key through a prefix-aware `*FromModel` method. Zero means the
complete key. Multiple projectors, unknown annotations, malformed counts, or
negative values stop scaffolding. PostgreSQL registers no projector and keeps
the provider-neutral generated call.

Scaffolding mode and legacy policy are intentionally absent from runtime
service-provider hash and equality because they change no runtime service
registration. EF's design-time provider reads the active context options and
freezes both values into source.

### Consequences

- Good, because the standard EF command now creates the complete reviewed table
  baseline without duplicate hand-authored expected definitions.
- Good, because strict and legacy modes share one generator composition and the
  same provider-neutral runtime operation contracts.
- Good, because provider identity facets are proven end to end rather than
  preserved only as C# text.
- Good, because a reviewed legacy baseline can repair common mutable column
  drift without giving type or generated-value drift implicit authority.
- Bad, because migration authors must still select explicit safe APIs for later
  non-table operations that require catalog-aware behavior.
- Bad, because a newly emitted provider operation annotation remains blocked
  until its catalog equivalence has an explicit adapter implementation.

### Confirmation

Run the Core scaffolding tests for default/invalid mode handling, strict and
legacy method selection, unchanged delegation when disabled, single/composite
index selection, fail-closed rollback, immutable annotation capture, and
rejection of mutable annotation values.

Run `eng/verify-ef-tooling.sh` for every qualified MySQL, MariaDB, and
PostgreSQL cell. It must scaffold strict and legacy migrations with real
`dotnet ef`, inspect positive and negative source tokens, verify provider
identity annotations, and compile the generated migrations before continuing
through update, script, idempotent script, and bundle gates.

Run both provider integration suites. Missing identity tables must be created,
accept generated values, and remain matching on a second execution. Existing
non-identity columns must classify `Different`; unknown operation annotations
must classify `Unsupported`; neither negative case may execute target DDL.
Repair qualification must additionally prove mutable drift, matching rerun,
null-data blocking, and invariant type-drift rejection on every supported
MySQL, MariaDB, and PostgreSQL server cell.

Pack all three packages and run package-only consumers. Each provider package
must contain its `buildTransitive` asset, inject the correct EF design-service
attribute for direct-Design and Tools-only consumers, and restore without a
project reference or cross-provider package. A runtime-only consumer must build
without the attribute, resolve no EF design-time package, and be rejected by EF
tooling before migration source is written.

## Pros and Cons of the Options

### Compose EF Core design-time generators and substitute reviewed operation calls

- Good, because EF Core and each provider remain responsible for their exact C#
  arguments, annotations, namespaces, and formatting.
- Bad, because SafeMigrations must detect upstream generator-shape changes and
  requalify them on every dependency update.

### Require manual expected-definition construction in every migration

- Good, because every expected facet is visibly authored in SafeMigrations
  types from the beginning.
- Bad, because it duplicates the canonical EF model, invites transcription
  errors, and provides poor normal-tooling ergonomics.

### Rewrite every EF migration operation automatically

- Good, because generated migrations would use SafeMigrations names throughout.
- Bad, because the current model cannot infer the intended conflict policy,
  repair contract, or ownership semantics for every rename, alteration, or
  destructive operation.

### Fork the EF Core and provider C# migration generators

- Good, because a fork can control every emitted token directly.
- Bad, because it duplicates version-sensitive provider behavior and increases
  drift risk without improving the selected operation contract.

## More Information

D-002 remains authoritative for ownership, granular child operations, and the
canonical destination. This amendment changes how its explicit source is
created. D-003 continues to own runtime provider composition; the new design
service is intentionally separate from runtime SQL ownership.

The generator performs design-time string work proportional to the rendered
operation source. It adds no runtime feature registry, reflection scan, or
per-operation dispatch allocation. Runtime provider annotations are immutable
snapshots already required for catalog comparison and hashing.

### Re-evaluation Triggers

- EF Core changes or removes the public design-time generator contracts or its
  referenced-design-service discovery order.
- A supported provider emits a common table/index operation annotation that is
  not representable by the current expected definitions.
- Product requirements define deterministic policies for additional ordinary
  operation families that justify automatic rewriting.
- NuGet changes `buildTransitive` import or package build-asset behavior.

### Decision History

- 2026-08-27: Decision recorded with status proposed.
- 2026-08-27: Dominic Kalkbrenner selected automatic strict-by-default scaffolding with explicit legacy convergence and source-frozen behavior.
- 2026-08-27: Status changed from proposed to accepted.
- 2026-08-27: Status changed from accepted to implemented after Core, provider, tooling, and package qualification surfaces were added.
- 2026-08-29: Doka 10.1.1 `ClientGuid` annotations were classified as catalog-neutral while remaining part of immutable definitions, fingerprints, and provider replay; unsupported provider annotations remain fail-closed.
- 2026-08-29: Added source-frozen legacy `RepairIfSafe` selection and the
  provider-verified mutable column repair allowlist.
- 2026-08-29: Added exact native Doka `Binary16` and `Char36` Guid-format
  catalog contracts while retaining fail-closed rejection for contradictory or
  unknown provider annotations.
- 2026-08-31: Required every generated migration to contain an explicit `Doka.EntityFrameworkCore.SafeMigrations` using directive. The generator now fails closed on a missing EF namespace anchor or duplicate SafeMigrations directive, and package-only plus provider tooling gates verify the generated import without a masking global using.
- 2026-08-31: Changed the migrations-code generator integration from replacement to selection-time decoration. The deferred selector follows EF Core's referenced-before-provider composition order while provider metadata and snapshot generation remain byte-for-byte provider-owned, including provider-specific model namespace discovery.
- 2026-09-01: Added typed MySQL/MariaDB index-prefix projection through Doka 10.3.0. Generated safe calls now retain full-key and prefixed-key intent without leaking the consumed provider annotation onto the outer operation.
- 2026-09-01: Preserved disabled non-C# provider generation and made every
  generated-source rewrite retain one validated LF or CRLF convention.
- 2026-09-02: D-009 amended automatic scaffolding to decorate the provider
  model differ, pair forward/inverse model-managed rows, and emit guarded
  ensure/update/delete calls without changing structural mode selection.

### Implementation References

- [Scaffolding mode and options](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationScaffoldingMode.cs)
- [Design-time service registration](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationDesignTimeServices.cs)
- [C# operation generator composition](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationCSharpMigrationOperationGenerator.cs)
- [Deferred provider-generator selector](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationMigrationsCodeGeneratorSelector.cs)
- [Provider code-generator decoration](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationCSharpMigrationsGenerator.cs)
- [Typed table capture](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features/Tables/SafeMigrationBuilderExtensions.Tables.cs)
- [Provider index projector boundary](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/ISafeMigrationCreateIndexScaffoldingProjector.cs)
- [MySQL/MariaDB prefix projector](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Scaffolding/MySqlSafeMigrationCreateIndexScaffoldingProjector.cs)
- [Scaffolding unit tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Scaffolding/SafeMigrationScaffoldingTests.cs)
- [MySQL/MariaDB identity integration](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.Scaffolding.cs)
- [PostgreSQL identity integration](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Columns/PostgreSqlSafeMigrationIntegrationTests.Columns.Scaffolding.cs)
- [Real EF tooling qualification](../../eng/verify-ef-tooling.sh)
- [Package-only consumer qualification](../../eng/verify-package-consumer.sh)

### Sources

- [EF Core design-time services](https://learn.microsoft.com/en-us/ef/core/cli/services) (primary source; retrieved 2026-08-27)
- [Installing EF Core tools](https://learn.microsoft.com/en-us/ef/core/get-started/overview/install) (primary source; retrieved 2026-08-27)
- [EF Core design-time DbContext creation](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation) (primary source; retrieved 2026-08-27)
- [EF Core IMigrationsCodeGenerator API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.design.imigrationscodegenerator?view=efcore-10.0) (primary source; retrieved 2026-08-27)
- [EF Core 10.0.11 DesignTimeServicesBuilder ordering](https://github.com/dotnet/efcore/blob/v10.0.11/src/EFCore.Design/Design/Internal/DesignTimeServicesBuilder.cs) (primary source; retrieved 2026-08-31)
- [EF Core 10.0.11 IMigrationsCodeGeneratorSelector API](https://github.com/dotnet/efcore/blob/v10.0.11/src/EFCore.Design/Migrations/Design/IMigrationsCodeGeneratorSelector.cs) (primary source; retrieved 2026-08-31)
- [EF Core DesignTimeServicesReferenceAttribute API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.design.designtimeservicesreferenceattribute?view=efcore-10.0) (primary source; retrieved 2026-08-27)
- [NuGet MSBuild props and targets](https://learn.microsoft.com/en-us/nuget/concepts/msbuild-props-and-targets) (primary source; retrieved 2026-08-27)
- [NuGet package build assets](https://learn.microsoft.com/en-us/nuget/create-packages/creating-a-package) (primary source; retrieved 2026-08-27)
- [Microsoft.EntityFrameworkCore.Tools 10.0.11 package contract](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Tools/10.0.11) (primary package metadata; retrieved 2026-08-27)
- [Doka 10.1.1 MySQL value-generation strategies](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.1.1/src/Doka.EntityFrameworkCore.MySql/MySqlValueGenerationStrategy.cs) (primary source; retrieved 2026-08-29)
- [EF Core custom migration operations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/operations) (primary source; retrieved 2026-08-29)
- [MySQL 8.4 `ALTER TABLE`](https://dev.mysql.com/doc/refman/8.4/en/alter-table.html) (primary source; retrieved 2026-08-29)
- [MariaDB `ALTER TABLE`](https://mariadb.com/docs/server/reference/sql-statements/data-definition/alter/alter-table) (primary source; retrieved 2026-08-29)
- [PostgreSQL `ALTER TABLE`](https://www.postgresql.org/docs/current/sql-altertable.html) (primary source; retrieved 2026-08-29)
- [EF Core generated values](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties) (primary source; retrieved 2026-08-29)
- [MySQL `INFORMATION_SCHEMA.COLUMNS`](https://dev.mysql.com/doc/refman/en/information-schema-columns-table.html) (primary source; retrieved 2026-08-27)
- [MariaDB `INFORMATION_SCHEMA.COLUMNS`](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-columns-table) (primary source; retrieved 2026-08-27)
- [Npgsql value-generation strategies](https://www.npgsql.org/efcore/api/Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.html) (primary source; retrieved 2026-08-27)
- [PostgreSQL `pg_attribute`](https://www.postgresql.org/docs/18/catalog-pg-attribute.html) (primary source; retrieved 2026-08-27)
