using var context = new PostgreSqlGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var runner = BenchmarkRunner.Create(args, "postgresql-results.json");

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    runner.Measure(
        $"postgresql_generation_{size}",
        () => generator.Generate(operations, context.Model).Count);
}

return runner.Complete();

internal sealed class PostgreSqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(
            "Host=127.0.0.1;Port=1;Username=benchmark;Password=benchmark;Database=benchmark");
        optionsBuilder.UsePostgreSqlSafeMigrations();
    }
}
