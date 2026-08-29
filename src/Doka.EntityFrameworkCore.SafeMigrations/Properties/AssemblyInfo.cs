using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.Tests")]

// Provider property suites verify parser-renderer round trips without making
// the provider-neutral parser part of the supported package API.
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests")]

[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks")]

[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.MySql")]
[assembly: InternalsVisibleTo("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql")]
