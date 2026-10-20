namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises real generated-upgrade migration SQL without depending on a native engine.</summary>
public sealed class SqlServerGeneratedHistoryScriptTests
{
    /// <summary>Generates the actual historical operations without an implicit target-model data mapping.</summary>
    /// <param name="upgrade">Whether to include the computed-column upgrade.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealHistoryUpScriptUsesExplicitHistoricalDataTypes(bool upgrade)
    {
        // Arrange
        using var context = Context();
        var target = upgrade ? SqlServerGeneratedUpgradeMigration.MigrationIdentifier
            : SqlServerGeneratedInitialMigration.MigrationIdentifier;

        // Act
        var script = context.GetService<IMigrator>().GenerateScript("0", target);

        // Assert
        Assert.Contains("INSERT INTO [generated_upgrade_nodes]", script, StringComparison.Ordinal);
        Assert.Contains(SqlServerGeneratedInitialMigration.MigrationIdentifier, script, StringComparison.Ordinal);
        Assert.Equal(upgrade, script.Contains("HasChildren", StringComparison.Ordinal));
    }

    /// <summary>The real reverse migration remains scriptable with the guarded provider enabled.</summary>
    [Fact]
    public void RealHistoryDownScriptRetainsGuardedDropOperations()
    {
        // Arrange
        using var context = Context();

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(
            SqlServerGeneratedUpgradeMigration.MigrationIdentifier, "0");

        // Assert
        Assert.Contains("DROP TABLE", script, StringComparison.Ordinal);
        Assert.Contains("HasChildren", script, StringComparison.Ordinal);
        Assert.Contains(SqlServerGeneratedUpgradeMigration.MigrationIdentifier, script, StringComparison.Ordinal);
    }

    /// <summary>The same fixture cannot silently fall back to nonexistent historical model metadata.</summary>
    [Fact]
    public void RemovingHistoricalStoreTypesReproducesEfDataMappingFailure()
    {
        // Arrange
        using var context = Context();
        var migration = new SqlServerGeneratedInitialMigration { ActiveProvider = context.Database.ProviderName! };
        var insert = migration.UpOperations.OfType<InsertDataOperation>().Single();
        insert.ColumnTypes = null;
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var targetModel = context.GetService<IModelRuntimeInitializer>().Initialize(
            migration.TargetModel, designTime: true);

        // Act
        var failure = Record.Exception(() => generator.Generate(migration.UpOperations, targetModel));

        // Assert
        var actual = Assert.IsType<InvalidOperationException>(failure);

        Assert.Contains("generated_upgrade_nodes", actual.Message, StringComparison.Ordinal);
    }

    private static SqlServerGeneratedUpgradeContext Context()
        => new("Server=127.0.0.1,1433;Database=offline_history;User ID=sa;Password=unused;TrustServerCertificate=True");
}
