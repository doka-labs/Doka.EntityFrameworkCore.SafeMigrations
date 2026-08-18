# Changelog

All notable changes are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- .NET 10 and EF Core 10 package, sample, test, benchmark, and tooling surface.
- Provider-neutral sealed `SafeMigrationOperation` with typed immutable intents
  for schema, table, column, index, primary-key, unique, check, and foreign-key
  ensure/drop/rename/alter operations.
- Complete expected definitions for column defaults and facets, advanced index
  facets, constraints, and strict table shape.
- Total I/O-free decision planner for `ExistenceOnly`, `ThrowIfDifferent`, and
  allowlisted `RepairIfSafe` behavior.
- Granular `ConvergeTable` baseline for heterogeneous legacy schemas, including
  copied empty or partial tables.
- Read-only `ISafeMigrationRunner` preflight and postflight, canonical derived-
  context model guard, SHA-256 model/contract fingerprints, unexpected-object
  inventory, versioned report JSON, packaged JSON Schema, and bounded telemetry.
- MySQL and MariaDB integration through the public
  `Doka.EntityFrameworkCore.MySql` exact operation-handler SPI.
- Session-local MySQL/MariaDB guard and prepared provider DDL without stored
  routines or permanent helpers.
- PostgreSQL composed migrations generator and parameterized catalog analyzer.
- Real EF migration/history, provider-lock, least-privilege, partial-failure,
  pooled-session recovery, script, CLI, and Migration Bundle tests.
- Deterministic pairwise legacy-state generator and live cross-provider matrix.
- Identifier, SQL mode, default literal, generated column, advanced index,
  constraint drift, data blocker, and wrong-object-kind coverage.
- Dependency-free duration/allocation gate at 1, 100, and 1000 operations.
- Locked Floor and isolated Latest dependency profiles.
- Deterministic double-pack, exact package-content validation, package-only
  consumer, source/symbol packages, SPDX SBOM generation, and Public API gates.
- Reusable full-engine CI/release qualification, pinned actions and container
  digests, OIDC NuGet Trusted Publishing, SLSA/SBOM attestations, exact partial-
  publish recovery, repository-signature readback, and verified GitHub Release.
- Architecture, support, deployment/recovery, failure-code, release, and sample
  documentation.

### Changed

- Renamed the former MariaDB-only package to
  `Doka.EntityFrameworkCore.SafeMigrations.MySql`; it supports both MySQL and
  MariaDB.
- Replaced Pomelo and generator inheritance with the packaged Doka provider SPI.
- Replaced annotation-based safe operations and migration-embedded preflight
  with a fail-closed envelope and separate read-only runner.
- Changed PostgreSQL integration from subclassing provider internals to
  composition and delegation of ordinary Npgsql operations.
- Replaced string/JSON default fallbacks with typed expected values and provider
  type mappings.
- Replaced global MariaDB procedure guards with session-local temporary state.
- Batched ordered preflight and postflight classifications into one
  parameterized provider command per run.
- Isolated SBOM dependency detection in a canonical source snapshot with its
  own locked restore, excluding prior build and qualification artifacts.

### Removed

- `Doka.EntityFrameworkCore.SafeMigrations.MariaDb` package and namespace.
- Pomelo dependency and provider generator copy/override path.
- `SafeMigrationStrictMode`, `SafeMigrationConflictMode`,
  `SafeMigrationExecutionOptions`, `PreflightOnly`, annotation serializers, and
  dedicated legacy safe constraint operation subclasses.
- Any promise that preflight can be recorded as an applied EF migration.
