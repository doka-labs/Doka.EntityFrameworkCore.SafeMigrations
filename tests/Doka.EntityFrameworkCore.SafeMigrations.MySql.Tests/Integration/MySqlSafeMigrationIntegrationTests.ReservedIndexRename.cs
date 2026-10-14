namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ReservedIndexRename migration contracts in an independent class collection.</summary>
public sealed partial class MySqlReservedIndexRenameIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlReservedIndexRenameIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Both reserved rename endpoints reject through the guard while ordinary names remain usable.</summary>
    /// <param name="source">The existing physical source index.</param>
    /// <param name="target">The requested destination, including case-insensitive PRIMARY variants.</param>
    /// <param name="reserved">Whether native RENAME cannot accept the requested endpoint.</param>
    [Theory]
    [InlineData("PRIMARY", "target_index", true)]
    [InlineData("source_index", "PRIMARY", true)]
    [InlineData("source_index", "primary", true)]
    [InlineData("source_index", "target_index", false)]
    public async Task ReservedIndexRenameIsRejectedBeforeNativeMutation(string source, string target, bool reserved)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE reserved_index_rows (id int NOT NULL"
            + (source == "PRIMARY" ? ", PRIMARY KEY (id)" : string.Empty) + "); "
            + (source == "PRIMARY" ? string.Empty : "CREATE INDEX source_index ON reserved_index_rows (id); ")
            + "INSERT INTO reserved_index_rows (id) VALUES (7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameIndexIfExists(source, "reserved_index_rows", target);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("reserved-index-rename"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM reserved_index_rows WHERE id = 7;");
        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'reserved_index_rows' "
            + $"AND INDEX_NAME = '{source}';");

        // Assert
        Assert.Equal(reserved ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(reserved ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            Assert.Single(report.Assessments).Action);

        if (reserved)
        {
            var exception = AssertOwnedGuardFailure(failure);
            Assert.Contains("doka_sm_different", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, rows);
        Assert.Equal(reserved ? 1 : 0, sourceCount);
    }
}
