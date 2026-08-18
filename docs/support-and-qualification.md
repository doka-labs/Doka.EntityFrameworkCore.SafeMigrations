# Support and qualification

## Support contract

All publishable projects target `net10.0`. The repository pins SDK 10.0.400
with roll-forward disabled. SafeMigrations supports EF Core 10 only.

| Package | Runtime dependency contract |
|---|---|
| Core | `Microsoft.EntityFrameworkCore.Relational` `[10.0.8,10.1.0)` |
| MySQL/MariaDB | `Doka.EntityFrameworkCore.MySql` `[10.0.0,10.1.0)` for stable publication |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` `[10.0.0,11.0.0)` |

During pre-release integration, the committed Doka version may identify a
specific immutable development or RC package. `release.yml` passes
`--require-stable-dependencies`; therefore a stable SafeMigrations tag cannot
publish until its package graph resolves a stable Doka 10 package. CI does not
build Doka and never uses a cross-repository ProjectReference.

## Engine matrix

The reusable workflow `.github/workflows/quality-gates.yml` is invoked by both
CI and stable releases. It pins these exact qualification images:

| Engine | Image |
|---|---|
| MySQL 8.4 | `mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb` |
| MySQL 9.7 | `mysql:9.7.2@sha256:257388edf9c84dbc04c763625446d5f3fa6ed60d1b0873bc552c614ba0a7ab4e` |
| MariaDB 10.11 | `mariadb:10.11.18@sha256:de61fed4a40d3842f3ee09944ba52792156cfd9adf489b2cc670fc6ded28df8d` |
| MariaDB 11.4 | `mariadb:11.4.12@sha256:a794d9eb009e20de605858a11f32f63b4075cbd197c650436f0e3b457e4caed7` |
| MariaDB 11.8 | `mariadb:11.8.8@sha256:efb4959ef2c835cd735dbc388eb9ad6aab0c78dd64febcd51bc17481111890c4` |
| MariaDB 12.3 | `mariadb:12.3.2@sha256:759869cb6f003234a95c6384cdee245b4bce7de26913fe607a8110362c0c007d` |
| PostgreSQL 14 | `postgres:14.24@sha256:2fdfb9b432d4a73bd3eea3d989752c1e669b68d502347e0bfd2cc6d709f3d6b4` |
| PostgreSQL 18 | `postgres:18.6@sha256:06cad38a5d9f5d24b4d83d86def30795d5e4b757fedbf5281172b576dedcd941` |

MySQL/MariaDB support follows Doka's canonical feature profiles. PostgreSQL
support spans major versions 14 through 18; the oldest and newest supported
majors are release gates. A new endpoint or removed upstream version requires a
reviewed support-contract change and fresh evidence.

## Dependency profiles

The committed lockfiles are the Floor profile:

- EF Core and Microsoft.Extensions.DependencyInjection 10.0.8;
- Npgsql EF Core 10.0.0;
- the exact committed Doka package.

The current Latest profile is:

- EF Core and Microsoft.Extensions.DependencyInjection 10.0.10;
- Npgsql EF Core 10.0.3.

`eng/verify-dependency-profile.sh` copies the repository to a clean canonical
temporary path, restores the selected versions with lock updates confined to
that copy, asserts exact resolutions, builds, and runs Core, MySQL/MariaDB, and
PostgreSQL suites. This avoids falsely passing on stale build outputs or
silently modifying the Floor contract.

## Behavioral evidence

The test inventory currently contains 103 tests:

- 27 provider-neutral tests;
- 46 MySQL/MariaDB tests;
- 30 PostgreSQL tests.

Provider tests use real Docker servers and cover:

- all 20 operation kinds;
- missing, matching, different, unsupported, and data-blocked states;
- `ExistenceOnly`, `ThrowIfDifferent`, and `RepairIfSafe`;
- granular heterogeneous table convergence and pairwise legacy-state
  generation with a fixed seed;
- exact expected column, index, primary-key, unique, check, and foreign-key
  facets;
- provider-specific index capabilities and unsupported branches;
- quotes, backslashes, mixed case, Unicode, and maximum-length identifiers;
- same-session guard recovery, partial-command retry, least privilege, and
  provider migration locks;
- normal EF operations mixed with safe operations;
- EF history success/failure and derived-context model-snapshot guards;
- read-only preflight, unexpected-object inventory, and postflight.

The provider-analyzer contract accepts the ordered safe-operation batch. Each
provider executes that classification in one parameterized database command;
the unexpected-object inventory is a second, family-oriented query. Projection
then applies the ordered results without additional catalog roundtrips.

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
are classified as `unsupported` before target DDL instead of being compared or
applied heuristically:

- PostgreSQL 14 rejects `NULLS NOT DISTINCT`; PostgreSQL introduced that
  `CREATE INDEX` clause in version 15.
- MySQL rejects `DateOnly` and `TimeOnly` literal defaults while the active Doka
  type mapping renders provider-incompatible typed literals. MariaDB's active
  mappings for the same values are qualified.
- MySQL and MariaDB reject a `Guid` literal default stored as `BINARY` when
  `INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` cannot preserve the complete
  binary value required for a repeatable semantic comparison.

These are complete fail-closed outcomes, not silent degradation. A provider or
server update may remove a boundary only after the same missing, matching,
different, retry, preflight, and EF-pipeline evidence passes for the changed
capability.

## Performance and memory

`eng/performance-budgets.json` defines explicit duration, regression tolerance,
and allocation ceilings at 1, 100, and 1000 operations for:

- intent construction;
- decision planning;
- MySQL handler/generator output;
- PostgreSQL adapter output;
- report JSON serialization.

The benchmark is a deterministic gate, not a throughput claim. Changes to a
budget require captured before/after evidence on the same runner class and a
review of asymptotic behavior; a budget must not be raised merely to make CI
green.

## Package and supply-chain evidence

`eng/qualify-packages.sh`:

1. packs the same Release build twice;
2. compares all three `.nupkg` and three `.snupkg` files byte-for-byte;
3. verifies the exact file set, metadata, dependency shape, assemblies, XML,
   symbols, README, license, and report schema;
4. builds and runs an isolated consumer using packages only;
5. emits sorted SHA-256 checksums.

The Microsoft SBOM Tool binary is downloaded at version 4.1.5 and verified
against the platform-specific release digest before execution. The generated
SPDX 2.2 manifest must validate all six packages plus the checksum file and
contain the required resolved package graph.

Stable release adds GitHub/Sigstore build provenance and SBOM attestations,
NuGet Trusted Publishing, NuGet repository-signature verification, and a
content readback that differs from the qualified package only by NuGet's
`.signature.p7s` entry.

## Primary references

- [.NET support policy](https://dotnet.microsoft.com/platform/support/policy)
- [EF Core supported releases](https://learn.microsoft.com/ef/core/what-is-new/)
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
- [PostgreSQL 18.6, 14.24 release announcement](https://www.postgresql.org/about/news/postgresql-186-1711-1615-1519-1424-and-19-beta-3-released-3365/)
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html)
- [MySQL 8.4 release notes](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/)
- [MySQL 9.7 release notes](https://dev.mysql.com/doc/relnotes/mysql/9.7/en/)
- [MariaDB release model](https://mariadb.org/about/release-model/)
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds)
- [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
- [Microsoft SBOM Tool](https://github.com/microsoft/sbom-tool)
