# Support and qualification

## Support contract

All publishable projects target `net10.0`. The repository pins SDK 10.0.400
with roll-forward disabled. SafeMigrations supports EF Core 10 only.

| Package | Runtime dependency contract |
| --- | --- |
| Core | `Microsoft.EntityFrameworkCore.Relational` `[10.0.11,10.1.0)` |
| MySQL/MariaDB | `Doka.EntityFrameworkCore.MySql` `[10.3.0,10.4.0)` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` `[10.0.3,11.0.0)` |

The MySQL/MariaDB package requires Doka 10.3.0 or a compatible later 10.3 patch
release and rejects the next minor line. This boundary avoids an exact
transitive pin without claiming compatibility across an unqualified behavioral
SPI revision. CI does not build Doka and never uses a cross-repository
ProjectReference. The committed lockfiles were regenerated from the public
Doka 10.3.0 package. Its typed migration-operation metadata was rechecked
against signed tag `v10.3.0`, commit `1217d087e269c346d41131688925d29ebd6151f7`,
on 2026-09-01. The complete package, engine, tooling, coverage, and performance
matrix remains the SafeMigrations release gate; a local Doka ProjectReference
or locally packed candidate is not release evidence.
The remaining declared dependency graph and .NET 10 release metadata were
rechecked on 2026-08-27. Bounded package ranges describe compatibility; the
committed lockfiles identify the exact graph selected by a particular
revision.

## Engine matrix

The reusable workflow `.github/workflows/quality-gates.yml` is invoked by CI,
release candidates, and stable releases. It declares these exact qualification
images:

| Engine | Image |
| --- | --- |
| MySQL 8.4 | `mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb` |
| MySQL 9.7 | `mysql:9.7.2@sha256:257388edf9c84dbc04c763625446d5f3fa6ed60d1b0873bc552c614ba0a7ab4e` |
| MariaDB 10.11 | `mariadb:10.11.18@sha256:de61fed4a40d3842f3ee09944ba52792156cfd9adf489b2cc670fc6ded28df8d` |
| MariaDB 11.4 | `mariadb:11.4.12@sha256:a794d9eb009e20de605858a11f32f63b4075cbd197c650436f0e3b457e4caed7` |
| MariaDB 11.8 | `mariadb:11.8.8@sha256:efb4959ef2c835cd735dbc388eb9ad6aab0c78dd64febcd51bc17481111890c4` |
| MariaDB 12.3 | `mariadb:12.3.2@sha256:759869cb6f003234a95c6384cdee245b4bce7de26913fe607a8110362c0c007d` |
| PostgreSQL 14 | `postgres:14.24@sha256:2fdfb9b432d4a73bd3eea3d989752c1e669b68d502347e0bfd2cc6d709f3d6b4` |
| PostgreSQL 15 | `postgres:15.19@sha256:5f72c7b5bd616308ccfd2e74d6be16fb06364e5eecbb815fe9dc6ab9761d2111` |
| PostgreSQL 16 | `postgres:16.15@sha256:e17e86066e5ef83e0952a9347f5c792b7ece00972e2aa787a6986f471b3dd3d5` |
| PostgreSQL 17 | `postgres:17.11@sha256:e38411452a464af89e5adadb8d223bf53b898d47d6ef918b2d58c08707350449` |
| PostgreSQL 18 | `postgres:18.6@sha256:06cad38a5d9f5d24b4d83d86def30795d5e4b757fedbf5281172b576dedcd941` |

MySQL/MariaDB support follows Doka's canonical feature profiles. PostgreSQL
support spans major versions 14 through 18; every supported major is an
independent release-gate cell. A new endpoint or removed upstream version
requires a reviewed support-contract change and fresh evidence.

At the 2026-08-27 source review, PostgreSQL 14 through 18 are the upstream
supported majors and the pinned versions above are their current minor
releases. PostgreSQL 14 reaches upstream end of life on 2026-11-12. Release
qualification performed after that boundary must re-evaluate the declared
matrix instead of inheriting this dated support conclusion.

The table records the matrix configured in source. Only a successful workflow
run for the exact commit/package version establishes executed qualification;
the presence of a tag or image digest in this document does not.

## Dependency qualification

Central package declarations define bounded compatible ranges; committed
lockfiles define the exact graph selected by the current revision. Dependabot
proposes dependency and lockfile updates through ordinary pull requests. Every
accepted update therefore runs the same complete CI workflow as product code,
including all provider cells, package consumers, coverage, performance, and
SBOM validation. GitHub Automatic Dependency Submission publishes resolved
base and head snapshots to the Dependency Graph. The independent, read-only
Dependency Review workflow compares those snapshots, rejects newly introduced
high-or-critical vulnerabilities and licenses outside the approved SPDX set,
and runs the official bounded retry. Because action v5.0.0 proceeds after that
timeout even when GitHub still reports a snapshot warning, a final comparison
header check requires the warning to be present and empty. The public REST
reference did not specify this header on 2026-08-28; its verified contract comes
from the immutable action source, and absence fails closed. There is no second
repository-owned polling state machine or dynamically restored dependency
profile whose results can diverge from the committed graph.

## Behavioral evidence

The executable test inventory consists of three independent xUnit assemblies
and focused engineering checks:

- [provider-neutral Core tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests);
- [MySQL/MariaDB tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests);
- [PostgreSQL tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests);
- FsCheck properties in all three assemblies for generated Core-contract,
  identifier-rendering, catalog-normalization, and provider-boundary inputs;
- [coverage verifier tests](../eng/tests/test_verify_coverage.py);
- package-content, package-only consumer, SBOM, EF tooling, and public NuGet
  readback scripts exercised by the reusable quality and release workflows.

Test results from the qualified commit are the authority for current case
counts. Counts are not duplicated here because parameterized cases and the
engineering suites evolve independently of the support contract.

Provider tests use real Docker servers and cover:

- all 20 operation kinds;
- all 22 supported non-null CLR literal families plus literal `NULL`, with
  provider-specific convergence or pre-DDL fail-closed classification;
- missing, matching, different, unsupported, data-blocked, and
  prerequisite-missing states;
- `ExistenceOnly`, `ThrowIfDifferent`, and `RepairIfSafe`;
- source-frozen legacy convergence policy selection, mutable column repair,
  matching rerun, null-data blocking, invariant-drift rejection, and
  MySQL/MariaDB preservation of unmodeled `EXTRA` modifiers;
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
- connection-disposal guard recovery, partial-command retry, least privilege,
  and provider migration locks;
- four concurrent migrators on one database and parallel independent
  databases;
- normal EF operations mixed with safe operations;
- safe table, typed seed/update/delete-data, and following non-unique-index
  ordering, plus fail-closed unique-index projection after unanalyzed data;
- EF history success/failure and derived-context model-snapshot guards;
- read-only preflight, unexpected-object inventory, positive and negative
  postflight, and cancellation before and during catalog access;
- PostgreSQL-owned and caller-owned analysis transactions, including accepted
  read-only `RepeatableRead`/`Serializable` scopes and fail-closed rejection of
  read-write or weaker-isolation caller transactions.

The provider-analyzer contract accepts the ordered safe-operation batch. Each
provider first classifies table and referenced-column prerequisites, then
executes classification in parameterized ADO.NET batches. Optimizer-visible
statements, statements per transport batch, parameters, and UTF-8 payload are
bounded independently. MySQL/MariaDB provider plans are captured in bounded
windows. The unexpected-object inventory remains scoped to the expected table
set for child objects while retaining complete table discovery and provider-
verified semantic-alias reconciliation. Projection applies global ordered
results without per-operation catalog roundtrips, and no partial report is
published after a later failure. Every engine profile also qualifies 100,000
deterministically ordered mixed operations. That workload covers `Missing`,
`Matching`, `Different`, `Unsupported`, `DataBlocked`, and
`PrerequisiteMissing`, all seven planned actions, and heterogeneous table,
column, index, primary-key, unique, check, and foreign-key operations. Native
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

The release workflow runs all three test assemblies against pinned MariaDB
11.8 and PostgreSQL 18 images with Microsoft's built-in code-coverage
collector. `eng/verify-coverage.py` conservatively merges Cobertura line and
branch evidence by product source line and excludes test and third-party
assemblies by exact package name.

`eng/coverage-thresholds.json` is a blocking floor, not a quality target:

| Product assembly | Line floor | Branch floor |
| --- | ---: | ---: |
| Core | 92% | 80% |
| MySQL/MariaDB adapter | 92% | 75% |
| PostgreSQL adapter | 94% | 84% |

The behavioral and engine matrices remain mandatory even when the numeric
floor passes. A threshold reduction requires reviewed evidence and must not be
used to hide an uncovered regression.

## Performance and memory

`eng/performance-budgets.json` defines explicit Core, MySQL/MariaDB, and
PostgreSQL benchmark sets with duration baselines, coarse hosted-runner
ceilings, and strict allocation ceilings at 1, 100, and 1000 operations. Three
independently restored and executed benchmark projects enforce the Core,
MySQL/MariaDB, and PostgreSQL dependency boundaries for:

- intent construction;
- decision planning;
- MySQL handler/generator output;
- PostgreSQL adapter output;
- canonical snapshot initialization, relational model differ, and fingerprint;
- report JSON serialization.

Allocation ceilings are deterministic blocking gates. Wall-clock measurements
on shared GitHub-hosted runners are not deterministic, so their three-times-
baseline ceilings only catch gross regressions and are not throughput claims.
Changes to a baseline or ceiling require captured before/after evidence on the
same runner class and a review of asymptotic behavior; a budget must not be
raised merely to make CI green.

The MySQL/MariaDB benchmark has no Npgsql dependency, and the PostgreSQL
benchmark has no Doka MySQL dependency. Shared measurement and workload source
is linked at compile time; no benchmark assembly introduces a cross-provider
runtime edge.

Every provider engine cell additionally runs 20 complete pooled
`SafeMigrationRunner` invocations against 100 expected tables, then repeats them
after adding 1,000 foreign tables with child objects. The cell stores p50/p95
JSON evidence and fails unless assessments are unchanged, foreign child rows
remain excluded by the expected-table scope, and noisy p95 is at most
`2 * clean p95 + 250 ms`. Each invocation can include multiple database
roundtrips. This is a same-runner relative SLO; it is not an absolute
cross-machine latency promise.

After locked restore, the quality workflow rejects warning-level Roslyn style
violations and unnecessary imports. Rider/ReSharper remains the repository
formatter for layout rules that Roslyn cannot represent.

## Package and supply-chain evidence

`eng/qualify-packages.sh`:

1. packs the same Release build twice;
2. compares all three `.nupkg` and three `.snupkg` files byte-for-byte;
3. verifies the exact file set, metadata, dependency shape, assemblies, XML,
   symbols, README, license, and report schema;
4. builds and runs an isolated consumer using packages only;
5. emits sorted SHA-256 checksums.

The two package-consumer fixtures are normal Solution projects in local
`Source` mode so Rider loads their complete C# and MSBuild models. That mode
uses the matching provider `ProjectReference` and participates in locked
Solution restore, formatting, and build gates. Package qualification copies
the fixtures into a temporary root and explicitly selects `Package` mode. That
mode removes every `ProjectReference`, consumes the generated packages, and
fails if the restored asset graph contains a project dependency. The local IDE
path therefore remains maintainable without weakening package-boundary
evidence.

The Microsoft SBOM Tool binary is downloaded at version 4.1.5 and verified
against the platform-specific release digest before execution. The generated
SPDX 2.2 manifest must validate all six packages plus the checksum file and
contain the required resolved package graph.

Every future release adds GitHub/Sigstore build provenance and SBOM
attestations, a portable `release-provenance.intoto.jsonl` asset, NuGet Trusted
Publishing, NuGet repository-signature verification, and a content readback
that differs from the qualified package only by NuGet's `.signature.p7s`
entry. Publication validates the exact SLSA subject inventory and verifies the
portable bundle against the release workflow and qualified commit before the
protected environment obtains a short-lived NuGet credential. The final
nine-asset immutable GitHub Release is then covered by GitHub's native Release
and release-asset verification.

## Primary references

- [.NET 10 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json),
  retrieved 2026-08-27.
- [EF Core Relational 10.0.11 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational/10.0.11)
  and [Npgsql EF Core 10.0.3 package](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
  retrieved 2026-08-27.
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
