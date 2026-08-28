# Support and qualification

## Support contract

All publishable projects target `net10.0`. The repository pins SDK 10.0.400
with roll-forward disabled. SafeMigrations supports EF Core 10 only.

| Package | Runtime dependency contract |
| --- | --- |
| Core | `Microsoft.EntityFrameworkCore.Relational` `[10.0.11,10.1.0)` |
| MySQL/MariaDB | `Doka.EntityFrameworkCore.MySql` exact `[10.0.0]` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` `[10.0.3,11.0.0)` |

The MySQL/MariaDB package resolves the exact stable Doka 10.0.0 package. CI
does not build Doka and never uses a cross-repository ProjectReference. A Doka
update is accepted only after the complete package, engine, tooling, coverage,
and performance matrix passes again.

The declared dependency graph was rechecked against the NuGet V3 package
registrations and the .NET 10 release metadata on 2026-08-27. Bounded package
ranges describe compatibility; the committed lockfiles identify the exact
graph selected by a particular revision.

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
- granular heterogeneous table convergence and pairwise legacy-state
  generation with a fixed seed;
- exact expected column, index, primary-key, unique, check, and foreign-key
  facets, including one-field drift matrices and strict-table embedding;
- provider-specific index capabilities and unsupported branches;
- quotes, backslashes, mixed case, Unicode, and maximum-length identifiers;
- PostgreSQL non-default schemas, cross-schema foreign keys, and
  same-named-object isolation;
- fail-closed schema qualification across every MySQL/MariaDB operation family;
- connection-disposal guard recovery, partial-command retry, least privilege,
  and provider migration locks;
- four concurrent migrators on one database and parallel independent
  databases;
- normal EF operations mixed with safe operations;
- EF history success/failure and derived-context model-snapshot guards;
- read-only preflight, unexpected-object inventory, positive and negative
  postflight, and cancellation before and during catalog access;
- PostgreSQL-owned and caller-owned analysis transactions, including accepted
  read-only `RepeatableRead`/`Serializable` scopes and fail-closed rejection of
  read-write or weaker-isolation caller transactions.

The provider-analyzer contract accepts the ordered safe-operation batch. Each
provider first classifies table prerequisites, then executes classification in
deterministic parameterized chunks bounded by operation count, parameter
count, and UTF-8 payload. The unexpected-object inventory remains scoped to
the expected table set for child objects while retaining complete table
discovery. Projection applies global ordered results without per-operation
catalog roundtrips, and no partial report is published after a later failure.

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
- Doka 10.0.0 parenthesizes `DateOnly` and `TimeOnly` typed literals in column
  defaults. The complete MySQL and MariaDB matrix qualifies the resulting DDL
  and each engine's catalog display form.
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
PostgreSQL benchmark sets with duration, regression tolerance,
and allocation ceilings at 1, 100, and 1000 operations. Three independently
restored and executed benchmark projects enforce the Core, MySQL/MariaDB, and
PostgreSQL dependency boundaries for:

- intent construction;
- decision planning;
- MySQL handler/generator output;
- PostgreSQL adapter output;
- canonical snapshot initialization, relational model differ, and fingerprint;
- report JSON serialization.

The benchmark is a deterministic gate, not a throughput claim. Changes to a
budget require captured before/after evidence on the same runner class and a
review of asymptotic behavior; a budget must not be raised merely to make CI
green.

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

Every release candidate and stable release adds GitHub/Sigstore build
provenance and SBOM attestations, NuGet Trusted Publishing, NuGet
repository-signature verification, and a content readback that differs from
the qualified package only by NuGet's `.signature.p7s` entry. Publication
uses the protected environment to obtain a short-lived NuGet credential, then
creates or verifies the exact immutable GitHub Release through GitHub's native
release-asset verification.

## Primary references

- [.NET 10 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json),
  retrieved 2026-08-27.
- [EF Core Relational 10.0.11 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational/10.0.11),
  [Doka 10.0.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.0.0),
  and [Npgsql EF Core 10.0.3 package](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
  retrieved 2026-08-27.
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
