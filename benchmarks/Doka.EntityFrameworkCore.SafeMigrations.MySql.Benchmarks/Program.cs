using var context = new MySqlGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var runner = BenchmarkRunner.Create(args, "mysql-results.json");

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    runner.Measure(
        $"mysql_generation_{size}",
        () => generator.Generate(operations, context.Model).Count);
}

return runner.Complete();

internal sealed class MySqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=benchmark;Password=benchmark;Database=benchmark",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        optionsBuilder.UseMySqlSafeMigrations();
    }
}
