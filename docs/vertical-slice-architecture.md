# Vertical-slice architecture

## Purpose

SafeMigrations uses a hybrid vertical-slice architecture. Public API and
provider package boundaries remain stable, while implementation ownership is
organized by migration capability instead of by technical layer alone.

The architecture has three package-level boundaries:

- `Doka.EntityFrameworkCore.SafeMigrations` owns provider-neutral contracts,
  policy, lifecycle, reports, fingerprints, and feature definitions.
- `Doka.EntityFrameworkCore.SafeMigrations.MySql` owns MySQL and MariaDB
  classification and guarded command generation through the Doka handler SPI.
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql` owns PostgreSQL
  classification and guarded command generation through Npgsql composition.

These boundaries also apply to tests, benchmarks, and package consumers. Core,
MySQL/MariaDB, and PostgreSQL have independent projects and restore graphs.
Provider packages may depend on Core, but never on each other. The package-only
qualification restores one consumer per provider package so a combined test
application cannot conceal an accidental cross-provider dependency.

Inside every package, the same feature slices are mirrored:

- `Schemas`
- `Tables`
- `Columns`
- `Indexes`
- `Constraints/PrimaryKeys`
- `Constraints/UniqueConstraints`
- `Constraints/CheckConstraints`
- `Constraints/ForeignKeys`

The implemented source layout is:

```text
src/Doka.EntityFrameworkCore.SafeMigrations/Features/<slice>/
src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/<slice>/
src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Features/<slice>/
tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/<slice>/
tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/<slice>/
tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/<slice>/
benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks/
benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks/
benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks/
```

The provider test trees additionally contain `Lifecycle` and `Identifiers`;
the core test tree contains `Lifecycle` for provider-neutral cross-slice
contracts. Those areas verify behavior spanning more than one operation family
and do not own product behavior.

Design-time scaffolding is a cross-slice composition concern under Core's
`Scaffolding` folder. It delegates provider-specific C# rendering to EF Core,
then maps only the reviewed table/index boundaries to existing slice entry
points. Provider `buildTransitive` assets own discovery. Provider column
annotation comparison remains in each provider's `Columns` slice; scaffolding
does not introduce a runtime feature registry or a fourth package.

## Slice ownership

A feature slice owns all behavior specific to its operation family:

- public immutable definitions and intents;
- `MigrationBuilder` entry points;
- conversion to ordinary EF migration operations;
- contract-fingerprint fields;
- preflight projection behavior;
- provider catalog classification;
- provider runtime-guard and postcondition inputs;
- focused unit and live integration tests.

The following responsibilities remain in the shared kernel because their
semantics span every operation family:

- the sealed operation envelope;
- operation state, policy, action, and total decision planning;
- preflight/runtime/postflight orchestration;
- report and telemetry contracts;
- provider registration and dispatch;
- projected catalog identity and cross-object rename propagation;
- typed SQL-expression roles and opaque-expression provenance;
- common catalog-query limits, identifier, literal, and command-plan
  primitives.

## Dependency direction

The allowed dependency direction is:

```text
provider feature slice -> provider kernel -> core feature slice -> core kernel
```

Core must not reference either provider. Provider feature slices may use their
own provider kernel and the corresponding core feature contracts. A feature
slice must not register services, open connections, write migration history,
or select policy independently; those responsibilities belong to the shared
lifecycle.

## Dispatcher rule

Closed dispatchers may enumerate all operation kinds so the compiler and tests
can prove exhaustive coverage. A dispatcher may validate input and route to a
slice, but it must not contain feature-specific comparison, SQL, mutation, or
repair rules.

Public types retain the existing
`Doka.EntityFrameworkCore.SafeMigrations[.Provider]` namespaces. Folder layout
is an ownership boundary and does not create an API namespace migration.

## Maintainability constraints

- Adding an operation kind requires one matching core slice and matching
  provider implementations or an explicit fail-closed unsupported outcome.
- Provider implementations mirror the core slice names.
- Shared helpers are promoted only when at least two slices use the same
  semantics, not merely similar syntax.
- Feature behavior is tested in the corresponding slice; cross-slice lifecycle
  tests remain in a dedicated lifecycle test area.
- No slice may bypass the common decision planner or provider registration.
- No source-generated or reflection-based feature discovery is used for the
  closed operation set.

## Performance constraints

The refactor must not add runtime feature registries, per-operation dependency
injection, reflection, or dynamic dispatch. Partial classes are a source-level
ownership mechanism only and compile into the same static or sealed runtime
types. Catalog classification remains ordered across deterministic bounded
chunks. Whole-run allocations also depend on input/model size, generated
commands, assessments, and returned catalog inventory; bounded requests do
not imply constant memory or an operation-count-only bound. Provider matrix
tests retain same-runner live p95 evidence for clean and 1,000-table noisy
catalogs, including pooled connection open/reset/close costs.

## Verification

The vertical-slice refactor is complete only when:

1. every operation kind is owned by exactly one core feature slice;
2. both provider packages mirror all feature slices;
3. central dispatchers contain routing but no feature-specific rules;
4. public API baselines match the reviewed contract, with shipped/unshipped
   status maintained according to the release process;
5. locked Release build and format checks pass;
6. unit, provider live, EF tooling, package-consumer, and performance gates
   remain green;
7. project references, namespace ownership, focused tests, and code review keep
   feature behavior out of aggregate dispatchers and shared fixtures.

MSBuild project references are the executable package boundary. The Release
build and package-only consumer tests reject missing or provider-crossing
dependencies. Slice ownership remains visible in the directory structure and
focused test projects without a second text parser for C# or project files.

The repository uses MSBuild folder scoping deliberately. The root
`Directory.Build.props` contains only repository-wide compiler, audit, lockfile,
and artifact settings. `src`, `tests`, `benchmarks`, and `samples` import that
root and add only their role-specific defaults. Provider dependencies remain in
the corresponding project files so Rider and command-line reviews expose the
same graph.

Primary references:

- [Customize the build by folder](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- [MSBuild extensibility hooks](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-your-build)
- [NuGet Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- [Select assemblies referenced by projects](https://learn.microsoft.com/en-us/nuget/create-packages/select-assemblies-referenced-by-projects)
