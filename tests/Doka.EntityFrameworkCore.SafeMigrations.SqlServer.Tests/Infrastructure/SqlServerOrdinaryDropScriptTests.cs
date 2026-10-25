namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordinary DROP history scripts retain mandatory fresh runtime checks without a database.</summary>
public sealed class SqlServerOrdinaryDropScriptTests
{
    /// <summary>Both script forms preserve the guarded DROP and its actual EF history identity.</summary>
    /// <param name="idempotent">Whether EF adds migration-history replay guards.</param>
    /// <param name="check">Whether the initial native blocker is a CHECK rather than an index.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CompleteScriptRetainsMandatoryNativeDependencyGuard(bool idempotent, bool check)
    {
        // Arrange
        using var context = Context(check);
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;

        // Act
        var script = context.GetService<IMigrator>().GenerateScript("0", null, options);

        // Assert
        Assert.Contains("INSERT INTO [ordinary_drop_history]", script, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN [L]", script, StringComparison.Ordinal);
        Assert.Contains("51001", script, StringComparison.Ordinal);
        Assert.Contains("sys.index_columns", script, StringComparison.Ordinal);
        Assert.Contains(check ? SqlServerOrdinaryDropCheckBlockedMigration.Identifier
            : SqlServerOrdinaryDropBlockedMigration.MigrationIdentifier, script, StringComparison.Ordinal);
        Assert.Equal(idempotent, script.Contains("WHERE [MigrationId] =", StringComparison.Ordinal));
    }

    /// <summary>The upgrade-only script does not recreate or silently delete the native blocking index.</summary>
    /// <param name="idempotent">Whether history guards wrap the upgrade.</param>
    /// <param name="check">Whether the initial native blocker is a CHECK rather than an index.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UpgradeScriptDoesNotInventDependencyRemoval(bool idempotent, bool check)
    {
        // Arrange
        using var context = Context(check);
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(
            check ? SqlServerOrdinaryDropCheckInitialMigration.Identifier
                : SqlServerOrdinaryDropInitialMigration.MigrationIdentifier, null, options);

        // Assert
        Assert.Contains("DROP COLUMN [L]", script, StringComparison.Ordinal);
        Assert.Contains("51001", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP CONSTRAINT [CK_ordinary_drop_history_L]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE [ordinary_drop_history]", script, StringComparison.Ordinal);
    }

    private static DbContext Context(bool check)
    {
        const string connection = "Server=127.0.0.1,1433;Database=offline_drop;User ID=sa;Password=unused;"
            + "TrustServerCertificate=True";

        return check ? new SqlServerOrdinaryDropCheckHistoryContext(connection)
            : new SqlServerOrdinaryDropHistoryContext(connection);
    }
}
