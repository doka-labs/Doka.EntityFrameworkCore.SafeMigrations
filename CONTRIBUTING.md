# Contributing

Participation follows the [Code of Conduct](CODE_OF_CONDUCT.md). Before opening
an issue, use [SUPPORT.md](SUPPORT.md) to choose the correct public or private
channel. Do not include production data, credentials, connection strings, or
unredacted migration reports in issues, pull requests, or test fixtures.

## Prerequisites

- .NET SDK 10.0.402, selected by `global.json`
- Docker for MySQL, MariaDB, PostgreSQL, and SQL Server tests; SQLite tests run
  in-process
- Bash, `jq`, `curl`, `unzip`, and `rsync` for engineering gates
- Python 3 for the merged coverage threshold check
- the exact `Doka.EntityFrameworkCore.MySql` package selected by the committed
  lockfiles from nuget.org

Do not add a ProjectReference to the Doka repository. SafeMigrations verifies a
real package boundary.

## Restore and build

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
```

## Tests

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests.csproj --configuration Release
```

The provider fixtures use Testcontainers with dynamically assigned host ports,
readiness checks, and automatic resource-reaper cleanup. Each test receives a
fresh database. SQL Server uses the free Developer container without a separate
account. Its live tests require a supported Linux x86-64 container host; Apple
Silicon skips are not runtime qualification. The three required x86-64 CI cells
must pass without skipped tests. See the [SQL Server guide](docs/sqlserver-behavior.md)
for the platform boundary and declared engine matrix.

### Parallel execution and resource ownership

The tracked solution settings allow Core and all four provider test assemblies
to run concurrently in Rider. The shared `tests/Shared/xunit.runner.json` is
copied to every test output and enables independent collection scheduling for
both IDE and CLI runners. A personal `.DotSettings.user` override can still
replace the solution setting; keep it consistent when checking local scheduling.

Server-provider assemblies share one container for the selected test run, not
one container per class. Their default limit is four concurrent class
collections per assembly. Core and SQLite keep xUnit's CPU-based default.
Every live test owns its database or SQLite connection/file; completed tests
release only their own resources. Explicit initialization-only server profiles
still use a dedicated container when sharing would change their contract.
Unit-only discovery and entirely skipped live selections do not start servers.

Tests that force process-wide garbage collection, capture unfiltered global
metrics, or measure process-total allocations use the explicitly nonparallel
`Process-wide test contracts` collection. Ordinary feature classes remain
parallel. Connection-pool cleanup targets the owned database pool rather than
resetting other tests' sessions. Startup, test and cleanup failures remain
visible; an assembly fixture is never disposed by an individual test class.

Keep all qualification cases in CLI/CI runs, including `LargeScale` tests.
Parallel scheduling is not permission to skip them, lower coverage floors or
add hardware-dependent duration gates. Local elapsed times are informational;
test discovery, overlap, database isolation and cleanup are verified contracts.
Native SQL Server qualification still requires the declared x86-64 engine cells.

The default local engines are MariaDB 12.3.3 and PostgreSQL
18.6. Select another qualified image with:

```bash
SAFE_MIGRATIONS_MYSQL_ENGINE=mysql \
SAFE_MIGRATIONS_MYSQL_VERSION=8.4.13 \
SAFE_MIGRATIONS_MYSQL_IMAGE='mysql:8.4.13@sha256:76f0358884cb92d9a086d24e5671c38048d3fef40f2588703d73eb55392d6b69' \
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
```

or:

```bash
SAFE_MIGRATIONS_POSTGRES_IMAGE='postgres:14.24@sha256:8ab94f61668dc73bbab4dc13273ff2fc37e635536af62aef5306a49a9682a446' \
SAFE_MIGRATIONS_POSTGRES_VERSION=14.24 \
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

### Test structure and formatting

Use explicit Arrange, Act, and Assert sections once each, in that order. Each
test exposes one independently observable contract; separate unrelated positive,
negative, and validation cases. Keep actual execution in Act, including the
delegate passed to an exception assertion, rather than hiding it in an assertion
helper. Theory inputs need stable, discoverable case identities.

A coherent migration, replay, recovery, mutation/snapshot, or concurrency
scenario may contain multiple operations when their ordering is the contract.
Do not split it into order-dependent tests. Share expensive immutable setup when
safe, but keep database leases and mutable test state independently owned; a new
assertion must not repeat a large seed or start another container unnecessarily.

Document public APIs and internal helpers where XML documentation helps IDE
users. Explain non-obvious safety, ownership, and performance decisions with
focused WHY comments. Separate logical groups and AAA sections with blank lines;
also leave a blank line after control-flow blocks, multiline initializations,
and before returns except in tiny return-only blocks. Follow `.editorconfig`
without running `dotnet format whitespace`; use the verify-only style/import
checks below.

Keep synchronous lifecycle tests synchronous when that is the behavior under
test. If an IDE inspection suggests replacing that intentional path, use only a
documented, single-use test-local inspection annotation such as
`// ReSharper disable once UseAwaitUsing`. Do not suppress compiler/analyzer
warnings or disable inspections across a file or test family.

## Required engineering gates

Run the relevant focused tests while developing. Before review, run:

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks.csproj --configuration Release --no-build --no-restore -- --report-only
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks.csproj --configuration Release --no-build --no-restore -- --report-only
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks.csproj --configuration Release --no-build --no-restore -- --report-only
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks.csproj --configuration Release --no-build --no-restore -- --report-only
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks.csproj --configuration Release --no-build --no-restore -- --report-only
python3 -m unittest eng/tests/test_parallel_test_configuration.py -v
python3 -m unittest eng/tests/test_verify_coverage.py -v
bash eng/tests/test-release-version.sh
bash -e -c 'while IFS= read -r -d "" script; do bash -n "$script"; done < <(find eng -type f -name "*.sh" -print0)'
dotnet format Doka.EntityFrameworkCore.SafeMigrations.slnx style --severity warn --verify-no-changes --no-restore
dotnet format Doka.EntityFrameworkCore.SafeMigrations.slnx style --diagnostics IDE0005 --severity hidden --verify-no-changes --no-restore
```

All five benchmark sets run as informational evidence in CI and release
qualification. `--report-only` retains measurements, unchanged comparison limits,
and failed budget verdicts in JSON and console output without rejecting the run.
Invalid configuration, incomplete measurements, execution exceptions, and output
failures still fail. Omit `--report-only` for an explicitly strict local budget
comparison; benchmark limits must not be raised merely to make a run pass.

### Maintenance attribution

The four provider benchmark projects also accept `--maintenance-attribution`.
For example, after a fresh matching Release build:

```bash
SAFE_MIGRATIONS_ATTRIBUTION_SOURCE=candidate dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks.csproj --configuration Release --no-build --no-restore -- --maintenance-attribution --output artifacts/performance/maintenance-postgresql.json
```

Retain the baseline/candidate revision or diff alongside the source-labelled JSON.
It records raw allocation, elapsed, checksum and provider-specific output/transport
evidence for prepared client workloads. Deterministic ADO probes are not native
engine qualification. Elapsed times are informational; invalid evidence,
structural/output mismatches and execution failures still fail the command.

Live pooled-runner p95 comparisons for server providers are also informational.
Their 20 measured calls and `2 * clean p95 + 250 ms` comparison remain unchanged;
JSON retains `evaluationMode: "informational"` and an exceeded
`requirement.passed: false` verdict. Timing overruns do not fail those integration
tests. Report-state and catalog-scope assertions, invalid measurements,
execution exceptions and evidence-output failures still fail qualification.

The reusable quality workflow additionally collects Microsoft Cobertura output
from all five test assemblies, merges product lines conservatively, and runs:

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

The reusable CI workflow additionally runs all engine images, merged coverage
thresholds, EF CLI/script/bundle gates, package consumers, and SBOM validation.

## Change requirements

- Keep all repository content ASCII-only.
- Follow `.editorconfig`; nullable, analyzers, and warnings-as-errors are
  mandatory.
- Keep public XML documentation and update `PublicAPI.Unshipped.txt` for public
  API changes.
- Add automated tests for major new functionality and regression tests for bug
  fixes, including relevant successful, rejected, and boundary behavior.
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
- MySQL/MariaDB, PostgreSQL, SQLite, and SQL Server parity or an explicit provider
  capability rejection;
- true EF migration/history behavior;
- preflight and postflight behavior;
- idempotent second run and failure recovery;
- package consumer and Public API review when surface changes.

Design-time scaffolding changes additionally require strict and legacy source
generation for every affected provider, generated-source compilation, direct Design,
Tools-only, and runtime-only package-consumer profiles, and fail-closed
negative coverage for unexpected generated shapes or annotations.

## Documentation and architecture decisions

Update the canonical guide when behavior, support, deployment, or public API
changes. Use the [documentation index](docs/README.md) to find its owner. Keep
English ASCII text, relative repository links, and dated primary sources for
external behavior. Distinguish implemented behavior, executed checks, and
operator-owned prerequisites.

Material architectural choices use [Doka MADR Enterprise Profile 1.0](docs/decisions/MADR-PROFILE.md)
on MADR 4.0.0. Start with the [template](docs/decisions/adr-template.md), name
Dominic Kalkbrenner as decision maker, and provide concrete alternatives,
symmetric consequences, confirmation, history, implementation references, and
sources. Record actual consultation and approval rather than assuming them.

Review changed Markdown integrity, navigation, and primary-source claims.
Review every ADR against the full profile. External MCP/CLI generation or
import is optional, must preserve the document contract, and does not replace
review or add a build dependency.

## Pull requests

Target `main`, or the exact reviewed maintenance line for an applicable port,
keep the change cohesive, and include exact commands and results
in the pull request. Use the PR template to distinguish executed checks from
checks not run and explain genuinely unaffected areas. Do not claim support
from a build-only result or treat a default MariaDB run as MySQL qualification.
All required checks in `quality-gates.yml` must pass before merge; a local
not-applicable note does not waive the hosted matrix.

Changes to engine behavior or dependencies need primary-source references and
positive/negative regression evidence. Workflow and release changes must also
verify their real upstream Action contracts and distinguish local tests from
hosted execution. Do not create a release tag or publish a package to test a PR.
For workflow edits, run `python3 eng/verify-workflow-concurrency.py --actionlint actionlint`.
GitHub supports concurrency queue retention, but actionlint 1.7.12 does not yet
recognize that field. The adapter first validates its exact placement/value and
non-cancellation contract, then handles only that exact unsupported-schema
diagnostic; unrelated lint errors remain failures. Its positive/negative tests
run in the engineering gate. Remove the compatibility path once upstream
actionlint supports queue; do not use a general lint ignore.
Registered preparation lines receive the same Required Checks before activation;
passing CI does not grant publication rights. Follow [Release lines](docs/operations/release-lines.md)
for line preparation, dependency servicing and separate activation approval.

Use [GitHub Issues](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues)
for bugs, feature requests, and usage questions through the corresponding form.
Report vulnerabilities according to [SECURITY.md](SECURITY.md#reporting-a-vulnerability) and
conduct concerns through the private Code of Conduct contact.
