[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Tests")]

// Provider property suites verify parser-renderer round trips without making
// the provider-neutral parser part of the supported package API.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests")]

// WHY: Nonpackable measurement code uses the existing internal projection/parser contracts,
// rather than adding public runtime APIs solely to expose a benchmark workload.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Benchmarks")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks")]

// WHY: MySQL/MariaDB consumes the shared parser, ordered projection and native proof contracts.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql")]

// WHY: PostgreSQL uses the common expression, projection and catalog transport implementation.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql")]

// WHY: SQLite integrates Core projection and expression contracts with its atomic rebuild path.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Sqlite")]

// WHY: SQL Server shares Core proof, guarded-command and ordered analysis contracts.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.SqlServer")]
