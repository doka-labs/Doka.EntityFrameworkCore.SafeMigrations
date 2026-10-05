namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies dependency admission for bounded variable-width column repair.</summary>
public sealed class SqlServerColumnTransitionSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=column_transition_sql;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Only column alteration admits optimizer-owned statistics that SQL Server removes itself.</summary>
    [Fact]
    public void AlterColumn_DistinguishesAutomaticFromCallerOwnedStatistics()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var before = new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)");
        var after = new ExpectedColumnDefinition("Caption", typeof(string), false, "nvarchar(20)");
        var alteration = new SafeMigrationOperation(new AlterColumnIntent("items", after, before),
            SafeMigrationPolicy.RepairIfSafe);

        var deletion = new SafeMigrationOperation(new DropColumnIntent("Caption", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var alterationPlan = catalog.Build(alteration);
        var deletionPlan = catalog.Build(deletion);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, alterationPlan.RepairCapability);
        Assert.Contains("s.auto_created = 0", alterationPlan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("s.auto_created = 0", alterationPlan.RepairPrecondition, StringComparison.Ordinal);
        Assert.DoesNotContain("s.auto_created = 0", deletionPlan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("sys.index_columns", alterationPlan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("sys.foreign_key_columns", alterationPlan.StateExpression, StringComparison.Ordinal);
    }
}
