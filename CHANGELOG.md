# Changelog

All notable changes are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Separate catalog-only prerequisites from data-reading state guards during
  MySQL, MariaDB, and PostgreSQL preflight analysis. A required convergence
  column on a completely missing table now reports `prerequisite_missing`
  instead of letting the server resolve an unreachable data probe and raise a
  missing-table error. MySQL/MariaDB runtime SQL applies the same physical
  statement boundary before preparing the guarded probe, while missing-table
  convergence remains executable and idempotent.
- Map structured MySQL/MariaDB literal and cast store types to the bounded CAST
  grammar shared by both engines. Integer column aliases now render as
  `SIGNED` or `UNSIGNED`, so generated computed columns execute on MySQL and
  round-trip MariaDB's catalog normalization. Unsupported targets fail closed
  with `structured_cast_type`; typed null literals retain their explicit type
  on MySQL, MariaDB, and PostgreSQL. PostgreSQL structured cast targets are
  likewise validated through Npgsql's relational type mapping, then documented
  built-in aliases are canonicalized before their grammar reaches generated
  SQL. This prevents drift such as `int4` versus catalog-deparsed `integer` and
  preserves PostgreSQL's `float`/`float(p)` precision semantics even though
  Npgsql does not map that SQL-standard alias directly.
- Reject non-nullable MariaDB generated-column definitions before DDL with
  `generated_column_nullability`. MariaDB accepts an incoming `NOT NULL` clause
  but does not preserve that physical facet, while MySQL continues to converge
  and verify the supported definition.

## [10.1.0] - 2026-09-01

Prepared the first stable minor release after the complete 10.0 delivery. It
qualifies Doka 10.3.0's typed migration metadata, adds two scaffolder-facing
index-prefix entry points, and closes the documented brownfield convergence
defects across MySQL, MariaDB, and PostgreSQL. Existing public operations,
migration source, reports, history semantics, and runtime policies remain
compatible; generated MySQL/MariaDB migrations use the new entry points only
when provider metadata contains ordered index-prefix lengths.

These notes do not establish publication. Require the successful stable
release run, the authorized signed `v10.1.0` tag, and verified public package,
symbol, GitHub Release, provenance, SBOM, and attestation readback before
selecting 10.1.0. All three package IDs must be published at the exact same
version.

### Changed

- Raise the MySQL/MariaDB dependency floor to Doka 10.3.0 within the bounded
  `[10.3.0,10.4.0)` compatibility line. SafeMigrations now consumes Doka's
  public typed migration-operation metadata contract for Guid storage,
  value-generation strategies, and ordered index prefix lengths instead of
  depending on provider annotation names.
- Preserve MySQL/MariaDB index-prefix metadata during automatic scaffolding.
  Generated migrations express one non-negative prefix entry per key through
  the new `CreateIndexWithPrefixesIfNotExistsFromModel` and
  `CreateCompositeIndexWithPrefixesIfNotExistsFromModel` methods; zero means
  that the complete key is indexed.
- Promote both new Core signatures from the unshipped API inventory into the
  10.1.0 shipped baseline. There are no MySQL/MariaDB- or PostgreSQL-specific
  public signature changes in this release.

### Fixed

- Regenerate every affected MySQL/MariaDB lock entry from the official Doka
  10.3.0 package on NuGet.org. Fresh locked restores now validate the published
  package content instead of failing with `NU1403` because of a locally packed
  qualification candidate with the same version.
- Treat Doka's MariaDB JSON representation as one provider-owned column
  contract: expected `json` now matches the emitted `longtext` storage,
  `utf8mb4_bin` collation, and exact inline `JSON_VALID` check. That implicit
  check no longer breaks strict-table postconditions or unexpected-object
  inventory, while independent user checks still report drift.
- Parse bounded EF-generated SQL defaults into the typed expression model when
  expected definitions are captured. `CURRENT_TIMESTAMP(6)` now converges on
  every qualified MySQL/MariaDB server; unbounded SQL remains opaque and
  rejects before target DDL.
- Project an accepted exact-name `DropIndex` or `DropIndexIfExists` into a
  following ordinary column-BTREE index creation, including across recognized
  table/column metadata alterations. Postflight now treats the final safe writer
  for an exact catalog resource as authoritative and reports earlier transient
  writers as `postcondition_superseded`. Physical key limits, duplicate-data
  checks, missing prerequisites, opaque provider operations, and differently
  named semantic conflicts retain their fail-closed result.
- Match Doka's temporal MySQL/MariaDB row-version materialization as one owned
  semantic contract: the provider-generated `CURRENT_TIMESTAMP(6)` default and
  exact `ON UPDATE CURRENT_TIMESTAMP(6)` behavior are required, while missing
  update behavior and unrelated `EXTRA` modifiers remain drift.
- Accept MariaDB's exact quoted catalog representation for string defaults when
  Doka emitted a hexadecimal literal to avoid `sql_mode` ambiguity. Raw,
  provider-literal, and quoted-display forms remain separate exact candidates;
  value, quote, and backslash drift are not normalized away.
- Distinguish an InnoDB index that supports a local foreign key from an
  independently owned differently named index. The former may be superseded by
  the reviewed explicit index, as permitted by the engine; the latter remains
  an identity conflict and no duplicate user index is created.
- Allow `RepairIfSafe` to converge the documented nullability, default, and
  comment facets when every Doka column annotation is recognized by the typed
  provider contract and the remaining physical column shape already matches.
  Unknown, malformed, contradictory, HiLo, generated, identity, type, and
  collation drift remain fail-closed.
- Validate missing MySQL/MariaDB BTREE indexes against the live table engine,
  row format, InnoDB page size, column encodings, key types, and declared
  prefix lengths before target DDL. Unachievable keys now reject with
  `index_prefix_required_for_key_limit`; shapes whose key width cannot be
  proven reject with `index_key_length_unverifiable`.
- Reject differently named but semantically equivalent foreign keys, unique
  constraints, check constraints, and active indexes before adding duplicate
  database objects on MySQL, MariaDB, or PostgreSQL. PostgreSQL also rejects a
  differently named existing primary key because a table can own only one.
  The constraint reasons are `foreign_key_semantic_identity_conflict`,
  `unique_constraint_semantic_identity_conflict`,
  `check_constraint_semantic_identity_conflict`, and
  `primary_key_identity_conflict`; index identity conflicts use the ordinary
  `Different` decision because no provider capability is missing.
- Preserve proven table and column prerequisites across ordered EF
  `InsertDataOperation`, `UpdateDataOperation`, and `DeleteDataOperation`
  entries so a following non-unique safe index can be preflighted. The data
  operations remain `provider_owned_not_analyzed`, and every prior
  data-dependent projection or live pre-batch absence proof is invalidated so
  unique indexes and additive constraints cannot receive a false readiness
  decision. Opaque provider operations invalidate both structural projections
  and live row-level proofs because their SQL may change arbitrary data. Later
  structural provider operations cannot erase that uncertainty.
- Select and decorate the active provider migrations-code generator after EF
  Core has composed referenced, provider, and default design-time services.
  Provider-owned snapshot and migration-metadata namespace discovery remains
  authoritative, while SafeMigrations changes only the generated migration
  body and its validated outer source shape. Explicit non-C# generator
  requests fail at selection only while SafeMigrations scaffolding is active;
  disabled scaffolding preserves EF Core's provider-generator selection and
  output unchanged.
- Preserve the line-ending convention emitted by the provider migrations-code
  generator when SafeMigrations inserts policy or index-prefix arguments and
  converts generated namespaces. LF and CRLF source remain internally
  consistent; already mixed source fails closed before malformed migration
  code is returned.
- Fingerprint EF Core JSON container columns through the public relational
  `IColumn` facet contract. Nested `ToJson` graphs no longer index an empty
  scalar property-mapping collection, while existing scalar-model golden
  fingerprints remain unchanged.
- Reject PostgreSQL constraints whose catalog identity matches but whose
  execution semantics do not. The comparison now includes deferral and
  validation state, foreign-key match type and partial delete-action columns,
  check inheritance, unique-null treatment, and PostgreSQL 18 enforcement and
  temporal-constraint facets without breaking PostgreSQL 14 compatibility.
  Inherited or partition-derived constraints cannot satisfy local ensure or
  drop operations.
- Require MySQL checks to be enforced and bind MariaDB check expressions to
  both table and constraint identity, including the table-scoped constraint
  names supported by MariaDB 12.1 and later.
- Reject invalid, unready, dropping, attached, or constraint-owned PostgreSQL
  indexes where an independently managed index is expected. Partitioned parent
  indexes remain supported. MySQL invisible and MariaDB ignored indexes no
  longer satisfy a visible expected index or suppress creation under another
  name.
- Reject null arrays passed to the generated prefix-aware index entry points
  before any migration operation is appended. Length mismatches and negative
  prefix values remain fail-fast argument errors.

## [10.0.2] - 2026-08-31

Prepared the stable maintenance release that qualifies Doka 10.2.0's
ownership-aware connection contract and repairs generated migration namespace
imports. It preserves the SafeMigrations public API, operation definitions,
SQL behavior, report schema, migration-history semantics, and runtime policy.

These notes do not establish publication. Require the successful stable
release run, the authorized signed `v10.0.2` tag, and verified public package,
symbol, GitHub Release, provenance, SBOM, and attestation readback before
selecting 10.0.2. All three package IDs must be published at the exact same
version.

### Changed

- Advance the MySQL/MariaDB adapter and every affected locked consumer graph
  to Doka 10.2.0 with the bounded `[10.2.0,10.3.0)` compatibility range.
  SafeMigrations now declares its server-side user-variable requirement through
  Doka's ownership-aware connection contract. Provider-owned strings that omit
  `AllowUserVariables` are normalized safely; contradictory owned strings and
  incompatible borrowed connections or data sources fail before database I/O.
- Qualify Doka's unconditional matched-row and `GuidFormat=Binary16` transport
  invariants while retaining Doka's per-property `Binary16` and `Char36` column
  contracts. SafeMigrations adds no duplicate public connection option and
  keeps its command-boundary validation as defense in depth.

### Fixed

- Make every SafeMigrations-generated C# migration explicitly import
  `Doka.EntityFrameworkCore.SafeMigrations`, independently of application
  global usings. Unit, EF tooling, and package-only consumer gates now reject a
  missing import before incomplete migration source can ship.

## [10.0.1] - 2026-08-30

Prepared the first stable maintenance release of the complete SafeMigrations
contract. It preserves the 10.0.0 public API, generated migration source, SQL
behavior, report schema, migration-history semantics, and runtime policy while
advancing the qualified Doka provider patch and closing the release-provenance
verification gap.

These notes do not establish publication. Require the successful stable
release run, the authorized signed `v10.0.1` tag, and verified public package,
symbol, GitHub Release, provenance, SBOM, and attestation readback before
selecting 10.0.1. All three package IDs must be published at the exact same
version.

### Changed

- Advance the MySQL/MariaDB adapter and every affected locked consumer graph
  from Doka 10.1.1 to 10.1.2, and replace the public exact pin with the bounded
  `[10.1.2,10.2.0)` compatibility range. The provider patch retains the public
  operation-handler SPI, generated SQL, database behavior, supported-engine
  policy, and package ranges while correcting ownership of materialized
  `JsonElement` values. Committed lockfiles continue to select the qualified
  10.1.2 graph reproducibly; the next Doka minor requires fresh qualification.

### Security

- Retain the exact `actions/attest` Sigstore build-provenance bundle as the
  canonical `release-provenance.intoto.jsonl` workflow and GitHub Release
  artifact. Publication now rejects malformed envelopes, non-SLSA predicates,
  missing, additional, duplicate, or digest-conflicting subjects and verifies
  all six packages, `SHA256SUMS`, and the SPDX manifest against the release
  workflow and qualified commit before requesting the NuGet credential.
- Require the portable provenance as the ninth immutable Release asset and
  cover materialization, subject selection, cryptographic verification,
  partial-draft recovery, and missing/conflicting evidence with positive and
  negative engineering tests.

## [10.0.0] - 2026-08-29

Prepared the first stable release of the complete SafeMigrations contract for
.NET 10 and EF Core 10 across MySQL, MariaDB, and PostgreSQL. It promotes the
exact rc.3 product contract without changing the public API, generated
migration source, SQL behavior, report schema, migration-history semantics, or
runtime policy.

The stable version is independently rebuilt and qualified from the reviewed
source commit. No rc.3 package archive is renamed, republished, or treated as
evidence for the stable package bytes.

These notes do not establish publication. Require the successful stable
release run, the authorized signed `v10.0.0` tag, and verified public package,
symbol, GitHub Release, and attestation readback before selecting 10.0.0. All
three package IDs must be published at the exact same version.

### Changed

- Promote the fully qualified rc.3 contract to the first stable release line
  while retaining strict-by-default scaffolding, source-frozen legacy
  convergence, fail-closed planning, and the complete provider matrix.
- Select stable NuGet badge endpoints and provide copyable exact-version
  installation commands for all provider consumers.
- Promote all three reviewed public API inventories from unshipped to shipped
  baselines without changing any signature.
- Add positive release-version coverage for the stable identity while
  preserving every published RC as immutable release history.

## [10.0.0-rc.3] - 2026-08-29

Prepared third feature-complete release candidate of the SafeMigrations
10.0.0 contract. It preserves the rc.2 public API and migration-source
compatibility while correcting native Doka Guid-format analysis, ordered
mixed-migration preflight, and retry-safe publication reconciliation.

Compared with rc.2, rc.3 accepts the exact native Doka 10.1.1 Guid storage
contracts throughout strict and legacy-convergence relationship graphs. It
also projects bounded structural postconditions of recognized ordinary EF
operations into later safe prerequisites without classifying those ordinary
operations as safe. Unknown provider effects continue to invalidate projected
state fail-closed.

These notes do not establish publication. Require the successful release run,
the authorized signed tag, and verified public package, symbol, GitHub Release,
and attestation readback before selecting rc.3. All three package IDs must be
published at the exact same version.

### Fixed

- Stage and verify the complete GitHub Release draft before requesting a NuGet
  credential or pushing the first package. Immutable Release and asset
  attestation readback now uses bounded retries, so GitHub's asynchronous
  attestation availability cannot turn an otherwise complete publication into
  a false-negative run without first exhausting the explicit recovery window.
- Accept Doka 10.1.1 native Guid storage annotations for the exact
  `Binary16`/`binary(16)` and `Char36`/`char(36)` contracts across strict and
  legacy-convergence tables, keys, and relationship chains. Undefined values,
  contradictory store types, non-Guid CLR columns, and unknown annotations
  remain fail-closed before target DDL.
- Project deterministic structural postconditions of ordered ordinary EF table
  and column operations into later safe prerequisites. Mixed relationship
  migrations can now preflight an ordinary required-column addition followed by
  a safe index, backfill/default cleanup, and foreign key while every ordinary
  operation remains explicitly `provider_owned_not_analyzed`.

## [10.0.0-rc.2] - 2026-08-29

Prepared second feature-complete release candidate of the SafeMigrations
10.0.0 contract. It keeps strict scaffolding as the default while completing
the reviewed legacy-convergence repair, expression-parsing, prerequisite, and
provider-validation boundaries across MySQL, MariaDB, and PostgreSQL.

Compared with rc.1, rc.2 adds source-frozen legacy-convergence policy selection
and provider-context validation to the public contract. Existing rc.1 migration
source remains compatible, and no existing migration is reinterpreted by the
new configuration. The candidate updates the exact Doka dependency from
`[10.0.0]` to `[10.1.1]` while retaining the public operation-handler SPI.

These notes do not establish publication. Require the successful release run
and verified public package and symbol readback before selecting rc.2. All three
package IDs must be published at the exact same version.

### Added

- Add source-frozen legacy-convergence policy configuration. Generated
  `ConvergeTableFromModel` calls retain `ThrowIfDifferent` by default and can
  explicitly select `RepairIfSafe`. The repair path converges only nullability,
  default, and comment drift on invariant-compatible ordinary columns across
  MySQL, MariaDB, and PostgreSQL; existing nulls and type, collation,
  generated/identity, row-version, or provider-annotation drift remain
  fail-closed before mutation.
- Translate the bounded provider-neutral subset of EF-scaffolded check-
  constraint SQL into the structured `SafeMigrationSql` expression contract.
  Unsupported, ambiguous, commented, parameterized, provider-escape-dependent,
  oversized, or malformed expressions now stop scaffolding with the constraint
  name and a stable parse-failure code instead of producing a migration that
  can never pass live structural comparison.
- Add provider-context validation to `ISafeMigrationProviderAnalyzer` so an
  adapter can reject invalid live connection configuration before EF migration
  history, model, environment, lock, catalog access, or connection opening.

### Changed

- Update the exact `Doka.EntityFrameworkCore.MySql` dependency and every
  affected lockfile from rc.1's `[10.0.0]` to `[10.1.1]`. The provider update
  adds connection-string server discovery and generic scalar `LIKE` support,
  repairs application-owned Guid conversion across relationship chains, and
  preserves its public migration-operation handler SPI.
- Require the complete package, engine, EF tooling, coverage, property,
  performance, and SBOM qualification against the published Doka 10.1.1
  package before rc.2 publication.
- Replace the opaque pre-tag success line and routine fetch output with named
  branch, working-tree, source-commit, release-tag, and SSH-signing results plus
  an explicit next-step message. Positive and negative fixture coverage keeps
  the output and failure boundary executable.

### Fixed

- Treat Doka's `ClientGuid` column annotation as catalog-neutral while retaining
  it in immutable operation snapshots, fingerprints, and replayed provider DDL.
  Strict and legacy-convergence scaffolding now accept application-converted
  Guid keys and relationships; HiLo and unknown column annotations remain
  fail-closed before target DDL.
- Validate the actual MySQL/MariaDB `DbConnection` before pending-history
  discovery, including when EF reuses an internal service provider and the
  application supplies a replacement connection with
  `AllowUserVariables=false`.
- Classify missing referenced columns as `prerequisite_missing` before index,
  key, check, foreign-key, computed-column, or default-expression analysis can
  issue an unsafe data probe or target DDL.
- Project ordered legacy convergence through a newly added nullable column
  without a non-null default so a following unique index can be analyzed and
  applied.
  Unknown columns, non-null defaults, computed values, and nulls-not-distinct
  contracts remain fail-closed.
- Normalize the MySQL/MariaDB catalog alias between expected unique indexes and
  `TABLE_CONSTRAINTS.UNIQUE` during strict table analysis, runtime reruns, and
  unexpected-object inventory. Analysis uses the exact operation batch;
  runtime generation uses EF's target relational model. Unrelated unique keys
  still reject `StrictDefinition` and remain reported.

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
- FsCheck property-based testing for provider-neutral contract fingerprints,
  structured-expression equivalence and identifier rewriting, plus generated
  MySQL/MariaDB and PostgreSQL identifier, catalog, and foreign-fragment cases.
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
- A read-only, SHA-pinned GitHub Dependency Review gate for pull requests to
  `main`, with high-severity vulnerability rejection, an explicit SPDX license
  allowlist, bounded automatic-submission snapshot retries, and no write-capable
  pull-request token.
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
  configured signing key and configuration-independent working-tree state,
  GitHub provenance/SBOM attestations, short-lived Trusted Publishing
  credentials, duplicate-tolerant same-run recovery, and signed package-content
  readback. The operator path uses the zero-argument preparation check, explicit
  waiting-run review, and individually executable tag commands.
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

[Unreleased]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.1.0...HEAD
[10.1.0]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.2...v10.1.0
[10.0.2]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.1...v10.0.2
[10.0.1]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.0...v10.0.1
[10.0.0]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.0-rc.3...v10.0.0
[10.0.0-rc.3]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.0-rc.2...v10.0.0-rc.3
[10.0.0-rc.2]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/compare/v10.0.0-rc.1...v10.0.0-rc.2
[10.0.0-rc.1]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/releases/tag/v10.0.0-rc.1
