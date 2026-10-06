namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Sibling drops retain only a validated same-shape repair of an untouched column.</summary>
    /// <param name="control">The independently checked source or rebuild safety condition.</param>
    [Theory]
    [InlineData("valid")]
    [InlineData("null-rows")]
    [InlineData("wrong-old")]
    [InlineData("type-change")]
    [InlineData("unmanaged-trigger")]
    public async Task AlterAfterSiblingDropsRetainsRebuildSafetyBoundaries(string control)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NULL, Legacy TEXT NULL, "
            + "Temporary TEXT NULL, CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id), "
            + "CONSTRAINT uq_rebuild_entities_code UNIQUE (Code), "
            + "CONSTRAINT ck_rebuild_entities_code CHECK (length(\"Code\") > 0)); "
            + "INSERT INTO rebuild_entities (Id, Code) VALUES (1, "
            + (control == "null-rows" ? "NULL" : "'kept'") + ");");

        if (control == "unmanaged-trigger")
        {
            await ExecuteSqlAsync(connection,
                "CREATE TRIGGER unmanaged AFTER UPDATE ON rebuild_entities BEGIN SELECT 1; END;");
        }

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Temporary", "rebuild_entities");
        builder.DropColumnIfExists("Legacy", "rebuild_entities");
        builder.AlterColumnIfDifferent(
            "rebuild_entities",
            new ExpectedColumnDefinition("Code", control == "type-change" ? typeof(int) : typeof(string),
                isNullable: false, storeType: control == "type-change" ? "INTEGER" : "TEXT"),
            new ExpectedColumnDefinition("Code", typeof(string), isNullable: true,
                storeType: control == "wrong-old" ? "VARCHAR(20)" : "TEXT"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-alter-after-sibling-drops"));

        Exception? executionError = null;
        try
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }
        catch (InvalidOperationException exception)
        {
            executionError = exception;
        }

        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = 'kept';");

        var siblingColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_entities') "
            + "WHERE name IN ('Legacy', 'Temporary');");

        var alteration = report.Assessments[^1];

        // Assert
        if (control == "valid")
        {
            Assert.Equal(SafeMigrationAction.Repair, alteration.Action);
            Assert.Null(executionError);
            Assert.Equal(1, retainedRows);
            Assert.Equal(0, siblingColumns);
        }
        else
        {
            Assert.NotEqual(SafeMigrationAction.Repair, alteration.Action);
            Assert.NotNull(executionError);
            Assert.Equal(2, siblingColumns);
        }
    }
}
