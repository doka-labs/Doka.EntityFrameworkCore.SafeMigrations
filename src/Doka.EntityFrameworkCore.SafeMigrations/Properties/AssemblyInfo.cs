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

[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Sqlite")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.SqlServer")]
