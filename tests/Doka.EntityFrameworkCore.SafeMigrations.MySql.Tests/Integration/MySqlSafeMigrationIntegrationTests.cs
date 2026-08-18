namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests : MySqlIntegrationTestBase
{
    public MySqlSafeMigrationIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    private sealed class UnmappedValue;

    private static string MySqlMaximumIdentifier(
        string prefix
    ) => prefix + new string('x', 64 - prefix.Length);

    private static string MySqlIdentifier(
        string value
    ) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";

    private sealed class ConflictingSafeMigrationHandler : IMySqlMigrationOperationHandler
    {
        public string HandlerId => "safe_migrations_tests.conflict";

        public Type OperationType => typeof(SafeMigrationOperation);

        public MySqlMigrationOperationResult Generate(
            MySqlMigrationOperationContext context
        ) => throw new InvalidOperationException("A conflicting handler must never execute.");
    }

    private sealed class SchemaChangingDerivedContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : SafeMigrationDbContext(connectionString, serverVersion)
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
