# Changelog

All notable changes are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- FsCheck property-based testing for provider-neutral contract fingerprints,
  structured-expression equivalence and identifier rewriting, plus generated
  MySQL/MariaDB and PostgreSQL identifier, catalog, and foreign-fragment cases.
- A read-only, SHA-pinned GitHub Dependency Review gate for pull requests to
  `main`, with high-severity vulnerability rejection, an explicit SPDX license
  allowlist, bounded automatic-submission snapshot retries, and no write-capable
  pull-request token.

## [10.0.0-rc.1] - 2026-08-28

Prepared first feature-complete release candidate of the SafeMigrations Core,
MySQL/MariaDB, and PostgreSQL package family for .NET 10 and EF Core 10. The
source prepared for this candidate covers the complete intended 10.0.0 contract
and publication workflow; it is not a reduced feature release.

These notes do not establish publication. Require the successful release run
and verified public package/symbol readback before installation. Follow the
[installation guide](README.md#installation) with exact version `10.0.0-rc.1`;
choose the required provider package, which brings Core transitively. All
three package IDs use the same version. Changes and removals below are
relative to the earlier proof of concept, not a previous supported release.

Runtime dependencies are EF Core `[10.0.11,10.1.0)`, exact Doka `[10.0.0]` for
MySQL/MariaDB, and Npgsql EF Core `[10.0.3,11.0.0)` for PostgreSQL. The release
matrix covers MySQL 8.4/9.7, MariaDB 10.11/11.4/11.8/12.3, and PostgreSQL
14-18. Exact images, dependency contracts, and capability limits are maintained
in [Support and qualification](docs/support-and-qualification.md).

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
- EF Core design-time integration that automatically scaffolds strict safe
  table/index operations by default and object-granular legacy convergence
  when selected through the provider registration options.
- Source-frozen `Strict` and `LegacyConvergence` scaffolding modes, including a
  fail-closed legacy rollback body, analyzer-compatible generated source, and
  package-transitive design-service discovery for direct EF Design and EF Tools
  consumers of both provider packages.
- Immutable provider column-annotation capture, fingerprinting, DDL restoration,
  and live identity comparison for Doka MySQL/MariaDB `AUTO_INCREMENT` and
  supported Npgsql PostgreSQL identity strategies.
- Read-only `ISafeMigrationRunner` preflight and postflight, canonical derived-
  context model guard, SHA-256 model/contract fingerprints, unexpected-object
  inventory, versioned report JSON, packaged JSON Schema, and bounded telemetry.
- Snapshot-free explicit analysis with an independently supplied expected
  model fingerprint, and rejection of mismatches before catalog access.
- MySQL and MariaDB integration through the public
  `Doka.EntityFrameworkCore.MySql` exact operation-handler SPI.
- Session-local MySQL/MariaDB guard and prepared provider DDL without stored
  routines or permanent helpers.
- PostgreSQL composed migrations generator and parameterized catalog analyzer.
- Real EF migration/history, provider-lock, least-privilege, partial-failure,
  pooled-session recovery, script, CLI, and Migration Bundle tests.
- Testcontainers-owned provider fixtures with dynamic ports, readiness checks,
  isolated databases, cancellation, and automatic cleanup.
- PostgreSQL baseline-composition tests covering command order, generation
  options, and rejection of transaction-suppressed guarded commands.
- Deterministic pairwise legacy-state generator and live cross-provider matrix.
- Identifier, SQL mode, default literal, generated column, advanced index,
  constraint drift, data blocker, and wrong-object-kind coverage.
- Dependency-free duration/allocation gate at 1, 100, and 1000 operations.
- Schema-versioned benchmark sets that reject missing, duplicate, unknown, or
  orphaned performance-budget measurements.
- Pooled live full-runner p50/p95 evidence with 100 expected tables and 1,000
  foreign tables for every qualified engine cell.
- Centrally bounded dependencies with an exact committed lockfile graph.
- Deterministic double-pack, exact package file-set and required-content
  validation, isolated package-only consumers, Portable PDB/source-symbol
  packages, XML API documentation, SPDX SBOM generation, and Public API gates.
- Reusable full-engine CI/release qualification with one stable fail-closed
  aggregation check, pinned actions and container digests, OIDC NuGet Trusted
  Publishing, SLSA/SBOM attestations, exact partial-publish recovery,
  repository-signature readback, and verified GitHub Release.
- Task-oriented API, architecture, support, observability, deployment/recovery,
  failure-code, release-publication, independent verification, and sample guides.
- Governance, conduct, support and canonical root security policies, eight
  implemented ADRs in Doka MADR Enterprise Profile 1.0 on MADR 4.0.0, and a
  complete pinned OpenSSF Best Practices evidence inventory with a public
  Passing self-assessment. No independent security certification is claimed.
- Task-oriented documentation, dependency-free shell gates with positive and
  negative regression coverage, structured contribution forms, and explicit
  ownership.

### Changed

- Renamed the former MariaDB-only package to
  `Doka.EntityFrameworkCore.SafeMigrations.MySql`; it supports both MySQL and
  MariaDB.
- Replaced Pomelo and generator inheritance with the packaged Doka provider SPI.
- Replaced annotation-based safe operations and migration-embedded preflight
  with a fail-closed envelope and separate read-only runner.
- Unknown provider annotations now classify as unsupported before target DDL
  instead of being silently omitted from a safe expected definition.
- Changed PostgreSQL integration from subclassing provider internals to
  composition and delegation of ordinary Npgsql operations.
- Replaced string/JSON default fallbacks with typed expected values and provider
  type mappings.
- Replaced global MariaDB procedure guards with session-local temporary state.
- Batched ordered preflight and postflight classifications into deterministic
  parameterized chunks with operation, parameter, UTF-8 payload, and MySQL
  packet limits. Values are shared within a chunk, global order is retained,
  and a failed later chunk never produces a partial success report.
- Isolated SBOM dependency detection in a canonical source snapshot with its
  own locked restore, excluding prior build and qualification artifacts.
- Made the SBOM checksum envelope self-verifying and excluded `SHA256SUMS`
  from hashing itself, preventing a deterministically stale self-reference.
- Replaced bare model hashes with the provider-bound
  `safe-relational-model:v1:<provider>:sha256:<hex>` contract. Deployment
  manifests must regenerate `ExpectedModelFingerprint`; bare 64-character
  hashes are rejected before catalog analysis.
- Restricted persisted model fingerprints to relational migration metadata;
  EF convention-cache annotations such as
  `BaseTypeDiscoveryConvention:DerivedTypes` are excluded so patch-level EF
  updates cannot change an otherwise identical model digest.
- Replaced heuristic raw-SQL comparison with a typed expression contract.
  Existing raw filter, computed-column, SQL-default, functional-index, and
  check expressions now classify as `opaque_sql_expression`; migrate them to
  `SafeMigrationSql` expression nodes when structural comparison is required.
- Replaced the index-key `descending` flag with explicit
  `SafeMigrationIndexSortOrder` and `SafeMigrationIndexNullOrder` values.
  Existing definitions must choose provider defaults or an explicit direction
  and null placement.
- Replaced ambiguous collation strings with
  `SafeMigrationCollationIdentifier`; PostgreSQL preserves exact schema/name
  identity while MySQL and MariaDB reject schema-qualified collations.
- Added explicit canonical migration-context registration and PostgreSQL
  baseline-generator composition. Derived runtime contexts must name their
  canonical Core context; custom PostgreSQL generators must use the composed
  registration overload.
- Added `prerequisite_missing` and `reject_prerequisite_missing` to report
  schema v1 and aligned the packaged schema with every serializer wire code.
- Required MySQL and MariaDB connections to set `Allow User Variables=true`
  (`MySqlConnectionStringBuilder.AllowUserVariables`) before SafeMigrations
  command execution.
- Updated the exact Doka dependency to the stable 10.0.0 release and moved each
  guarded MySQL/MariaDB operation to the provider-owned scoped command contract.
  The adapter now consumes validated baseline fragments directly and receives
  failure- and cancellation-safe cleanup with pool eviction on cleanup failure.
- Qualified Doka 10.0.0 temporal expression defaults on every supported
  MySQL/MariaDB line and enabled Binary16 Guid defaults only where catalog
  fidelity is proven: MariaDB 11.8 and 12.3. MySQL, MariaDB 10.11, and MariaDB
  11.4 continue to fail closed before target DDL.
- Retained the scoped-command allocation improvement through Doka 10.0.0,
  with unchanged MySQL generation budgets at 1, 100, and 1,000 operations.
- Made GitHub Release creation rerunnable with platform-native primitives: the
  workflow verifies the signed tag target, qualified package bytes, signed
  public NuGet content, and the exact immutable eight-asset Release. GitHub's
  release-asset attestations replace a repository-owned reconciliation engine.
- Aligned the package family with .NET 10, EF Core 10, and Doka 10. The manually
  dispatched workflow qualifies and attests current `main` before a signed tag
  is created at its protected publication wait. It accepts a canonical package
  version instead of encoding one release line, marks prereleases correctly,
  and leaves `latest` unchanged for release candidates.
- Bound release inputs to the reviewed source `VersionPrefix`, exact dated
  changelog entry, canonical 64-character NuGet limit, and shell-safe
  environment-variable boundary.
- Added repository-owned SSH signer authorization, pre-tag verification of the
  configured signing key, GitHub provenance/SBOM attestations, short-lived
  Trusted Publishing credentials, duplicate-tolerant same-run recovery, and
  signed package-content readback. The operator path uses the zero-argument
  preparation check, explicit waiting-run review, and individually executable
  tag commands.
- Preserved the qualified candidate while protected `main` advances and retained
  each successful producer's artifact identity across failed-job retries.
  Source removal, expired evidence, and content conflicts stop publication.
- Clarified final-state postflight, independently approved execution/verification
  fingerprints, ordinary-provider-operation fingerprint limits, snapshot-free
  model guards, external SQL-script recovery, and migration-principal grants.
- Moved every workflow value crossing into a shell through environment
  variables, documented privileged job permissions, and applied a seven-day
  Dependabot cooldown to non-security version updates.

### Removed

- `Doka.EntityFrameworkCore.SafeMigrations.MariaDb` package and namespace.
- Pomelo dependency and provider generator copy/override path.
- `SafeMigrationStrictMode`, `SafeMigrationConflictMode`,
  `SafeMigrationExecutionOptions`, `PreflightOnly`, annotation serializers, and
  dedicated legacy safe constraint operation subclasses.
- Any promise that preflight can be recorded as an applied EF migration.

[Unreleased]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.0-rc.1...HEAD
[10.0.0-rc.1]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/releases/tag/v10.0.0-rc.1
