# Contributing

## Prerequisites

- .NET SDK 10.0.400, selected by `global.json`
- Docker for MySQL, MariaDB, and PostgreSQL tests
- Bash, `jq`, `curl`, `unzip`, and `rsync` for engineering gates
- the exact locked `Doka.EntityFrameworkCore.MySql` 10.0.0 package from
  nuget.org

Do not add a ProjectReference to the Doka repository. SafeMigrations verifies a
real package boundary.

## Restore and build

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
```

## Tests

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

The provider fixtures use Testcontainers with dynamically assigned host ports,
readiness checks, and automatic resource-reaper cleanup. Each test receives a
fresh database. The default local engines are MariaDB 11.8.8 and PostgreSQL
18.6. Select another qualified image with:

```bash
SAFE_MIGRATIONS_MYSQL_ENGINE=mysql \
SAFE_MIGRATIONS_MYSQL_VERSION=8.4.11 \
SAFE_MIGRATIONS_MYSQL_IMAGE='mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb' \
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
```

or:

```bash
SAFE_MIGRATIONS_POSTGRES_IMAGE='postgres:14.24@sha256:2fdfb9b432d4a73bd3eea3d989752c1e669b68d502347e0bfd2cc6d709f3d6b4' \
SAFE_MIGRATIONS_POSTGRES_VERSION=14.24 \
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
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks.csproj --configuration Release --no-build --no-restore
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks.csproj --configuration Release --no-build --no-restore
eng/verify-vertical-slices.sh
python3 eng/verify-project-boundaries.py
python3 -m unittest discover -s eng/tests -p 'test_*.py' -v
node --test eng/tests/github-release.test.js
bash -n eng/*.sh eng/release/*.sh
dotnet format Doka.EntityFrameworkCore.SafeMigrations.slnx style --severity warn --verify-no-changes --no-restore
dotnet format Doka.EntityFrameworkCore.SafeMigrations.slnx style --diagnostics IDE0005 --severity hidden --verify-no-changes --no-restore
```

The reusable quality workflow additionally collects Microsoft Cobertura output
from all three test assemblies, merges product lines conservatively, and runs:

```bash
python3 eng/verify-coverage.py \
  --reports-root artifacts/coverage \
  --thresholds-file eng/coverage-thresholds.json
```

Package qualification requires an empty output directory and the Doka feed:

```bash
eng/qualify-packages.sh \
  --version 10.0.0-local.1 \
  --output /absolute/empty/output \
  --doka-source https://api.nuget.org/v3/index.json
```

The reusable CI workflow additionally runs all engine images, the merged
coverage floor, EF CLI/script/bundle gates, the Latest dependency profile, and
SBOM validation.

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
