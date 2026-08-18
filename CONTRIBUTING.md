# Contributing

## Prerequisites

- .NET SDK 10.0.400, selected by `global.json`
- Docker for MySQL, MariaDB, and PostgreSQL tests
- Bash, `jq`, `curl`, `unzip`, and `rsync` for engineering gates
- the exact locked `Doka.EntityFrameworkCore.MySql` package from nuget.org or
  an immutable local package feed during prerelease integration

Do not add a ProjectReference to the Doka repository. SafeMigrations verifies a
real package boundary.

## Restore and build

After the locked Doka package is public:

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
```

During Doka prerelease development, add the directory containing the exact
`.nupkg` before nuget.org:

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx \
  --locked-mode \
  --source /absolute/path/to/immutable-doka-feed \
  --source https://api.nuget.org/v3/index.json
```

## Tests

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

The provider fixtures invoke `docker run`, use a dynamically assigned host port,
and create a fresh database for each test. The default local engines are
MariaDB 11.8.8 and PostgreSQL 18.6. Select another qualified image with:

```bash
SAFE_MIGRATIONS_MYSQL_ENGINE=mysql \
SAFE_MIGRATIONS_MYSQL_VERSION=8.4.11 \
SAFE_MIGRATIONS_MYSQL_IMAGE='mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb' \
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
```

or:

```bash
SAFE_MIGRATIONS_POSTGRES_IMAGE='postgres:14.24@sha256:2fdfb9b432d4a73bd3eea3d989752c1e669b68d502347e0bfd2cc6d709f3d6b4' \
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

## Required engineering gates

Run the relevant focused tests while developing. Before review, run:

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks.csproj --configuration Release --no-build --no-restore
```

Package qualification requires an empty output directory and the Doka feed:

```bash
eng/qualify-packages.sh \
  --version 1.0.0-local.1 \
  --output /absolute/empty/output \
  --doka-source /absolute/path/to/immutable-doka-feed
```

The reusable CI workflow additionally runs all engine images, EF CLI/script/
bundle gates, the Latest dependency profile, and SBOM validation.

## Change requirements

- Keep all repository content ASCII-only.
- Follow `.editorconfig`; nullable, analyzers, and warnings-as-errors are
  mandatory.
- Keep public XML documentation and update `PublicAPI.Unshipped.txt` for public
  API changes.
- Do not suppress analyzer or compiler warnings; correct the design.
- Do not introduce a third-party dependency without prior design approval.
- Preserve provider-neutral Core boundaries and exact fail-closed operation
  ownership.
- Add no configuration value, flag, or public member without an active consumer.
- Keep SQL identifiers and literals on EF/provider rendering paths and catalog
  inputs parameterized.
- Never put connection data, credentials, object names, or data values in
  low-cardinality telemetry tags.

Every operation or facet change requires:

- constructor/definition and planner tests;
- live missing, matching, different, unsupported, and data-blocked coverage as
  applicable;
- MySQL/MariaDB and PostgreSQL parity or an explicit provider capability
  rejection;
- true EF migration/history behavior;
- preflight and postflight behavior;
- idempotent second run and failure recovery;
- package consumer and Public API review when surface changes.

## Pull requests

Target `main`, keep the change cohesive, and include exact commands and results
in the pull request. Do not claim support from a build-only result. All required
checks in `quality-gates.yml` must pass before merge.

Use [GitHub Issues](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues)
for bugs and feature requests. Report vulnerabilities according to
[SECURITY.md](.github/SECURITY.md).
