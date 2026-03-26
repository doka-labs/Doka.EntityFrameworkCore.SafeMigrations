# Contributing

Contributions are welcome. Please read this document before opening a pull request.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Docker](https://docs.docker.com/get-docker/) - required for the MariaDB and PostgreSQL integration test suites

## Building

```bash
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx
```

## Running Tests

**All tests** (requires Docker):

```bash
dotnet test Doka.EntityFrameworkCore.SafeMigrations.slnx
```

**Unit tests only** (no Docker required):

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj
```

**MariaDB integration tests** (requires Docker):

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests/Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.csproj
```

**PostgreSQL integration tests** (requires Docker):

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj
```

The integration test projects spin up database containers automatically via Testcontainers. Docker must be running locally.

## Code Style

- Follow the existing naming and formatting conventions in the codebase.
- All code comments must be in English.
- `<Nullable>enable</Nullable>` and `TreatWarningsAsErrors` are enforced solution-wide - the build must remain warning-free.
- Do not add third-party library dependencies without first opening an issue to discuss the rationale.

## Pull Requests

- Target the `main` branch.
- Keep each PR focused on a single concern.
- All new operation families must include:
  - unit tests for operation creation, planner decisions, and SQL shape
  - live MariaDB and PostgreSQL integration tests
- The build and all test suites must be green before requesting review.
- Summarize the motivation and approach in the PR description.

## Reporting Issues

Use [GitHub Issues](https://github.com/doka-org/Doka.EntityFrameworkCore.SafeMigrations/issues) for bug reports and feature requests.
For security vulnerabilities, see [SECURITY.md](.github/SECURITY.md).
