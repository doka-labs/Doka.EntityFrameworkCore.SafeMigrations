namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests : PostgreSqlIntegrationTestBase
{
    public PostgreSqlSafeMigrationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    private static string PostgreSqlMaximumIdentifier(
        string prefix
    )
    {
        var remainingBytes = 63 - Encoding.UTF8.GetByteCount(prefix);
        return prefix + new string('x', remainingBytes);
    }

    private static string PostgreSqlIdentifier(
        string value
    ) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed class UnmappedValue;

    private sealed class SchemaChangingDerivedContext(string connectionString)
        : SafeMigrationDbContext(connectionString)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    private sealed class DerivedEntity
    {
        public int Id { get; set; }
    }
}
