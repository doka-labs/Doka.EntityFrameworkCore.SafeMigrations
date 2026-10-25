namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks the real CLI/bundle verifier against complete and independently damaged databases.</summary>
public sealed class SqliteToolingDatabaseVerifierTests : SqliteIntegrationTestBase
{
    private const string InitialMigrationId = "20990101000000_Initial";
    private const string TransitionMigrationId = "20990101000001_Transition";

    /// <summary>A complete target passes and behavioral probes leave its rows unchanged.</summary>
    [Fact]
    public async Task CompleteTargetPassesWithoutPersistingProbeRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);

        // Act
        PackageConsumer.ToolingDatabaseVerifier.Verify(connection, InitialMigrationId, TransitionMigrationId);

        // Assert
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM scaffolding_generated_nodes;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM tooling_entities;"));
        using var transaction = connection.BeginTransaction();
        Assert.Same(connection, transaction.Connection);
    }

    /// <summary>A baseline without the transition or generated table must not qualify as the target.</summary>
    [Fact]
    public async Task BaselineOnlyDatabaseIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection, baselineOnly: true, includeGeneratedTable: false);

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("migration_history", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>An extra unique index cannot compensate for either missing migration identity.</summary>
    /// <param name="missingMigrationId">The absent initial or transition history entry.</param>
    [Theory]
    [InlineData(InitialMigrationId)]
    [InlineData(TransitionMigrationId)]
    public async Task ExtraIndexCannotCompensateForMissingHistory(string missingMigrationId)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection, "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '"
            + missingMigrationId + "'; CREATE UNIQUE INDEX unrelated_unique ON tooling_entities (Code, Id);");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("migration_history", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>History row counts do not substitute for the exact transition identity.</summary>
    [Fact]
    public async Task DifferentTransitionHistoryIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection, "UPDATE __EFMigrationsHistory SET MigrationId = '20990101000001_Other' "
            + "WHERE MigrationId = '" + TransitionMigrationId + "';");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("migration_history", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The final schema must include both the new column and the renamed column.</summary>
    /// <param name="column">The deliberately omitted final column.</param>
    [Theory]
    [InlineData("AddedCode")]
    [InlineData("renamed_code")]
    public async Task MissingTargetColumnIsRejected(string column)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection, "ALTER TABLE tooling_entities DROP COLUMN \"" + column + "\";");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("target_columns", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>A different index cannot impersonate the model's unique Code index.</summary>
    /// <param name="replacement">A renamed, reordered or nonunique replacement definition.</param>
    [Theory]
    [InlineData("CREATE UNIQUE INDEX unrelated_unique ON tooling_entities (Code)")]
    [InlineData("CREATE UNIQUE INDEX IX_tooling_entities_Code ON tooling_entities (Id)")]
    [InlineData("CREATE INDEX IX_tooling_entities_Code ON tooling_entities (Code)")]
    public async Task IncorrectCodeIndexIsRejected(string replacement)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection, "DROP INDEX IX_tooling_entities_Code; " + replacement + ";");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("code_index", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The generated table is part of real transport qualification, not a source-only grep.</summary>
    [Fact]
    public async Task MissingGeneratedTableIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection, includeGeneratedTable: false);

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("generated_columns", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The precise generated index key order is independently verified.</summary>
    [Fact]
    public async Task ReorderedGeneratedIndexIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection,
            "DROP INDEX IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound; "
            + "CREATE INDEX IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound "
            + "ON scaffolding_generated_nodes (HasDescendants, RightBound, LeftBound);");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("generated_index", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Matching generated metadata cannot hide an incorrect CASE expression.</summary>
    [Fact]
    public async Task IncorrectGeneratedExpressionIsRejectedAndProbeRowsAreRolledBack()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection, generatedExpression: "0");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("generated_values", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM scaffolding_generated_nodes;"));
    }

    /// <summary>A named but ineffective CHECK must fail its behavioral negative control.</summary>
    /// <param name="checkExpression">The absent or ineffective constraint expression.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public async Task MissingOrIneffectiveCheckIsRejected(string? checkExpression)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection, checkExpression: checkExpression);

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("generated_check", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM scaffolding_generated_nodes;"));
    }

    /// <summary>Foreign-key violations remain fatal even when all selected application objects match.</summary>
    [Fact]
    public async Task ForeignKeyViolationIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateFixtureAsync(connection);
        await ExecuteSqlAsync(connection, "PRAGMA foreign_keys = OFF; "
            + "CREATE TABLE extra_parent (Id INTEGER PRIMARY KEY); "
            + "CREATE TABLE extra_child (ParentId INTEGER REFERENCES extra_parent (Id)); "
            + "INSERT INTO extra_child VALUES (1); PRAGMA foreign_keys = ON;");

        // Act
        var exception = Record.Exception(() => PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, InitialMigrationId, TransitionMigrationId));

        // Assert
        Assert.Contains("foreign_key_integrity", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Creates independent engine fixtures rather than reproducing the verifier's SQL predicates.</summary>
    private static async Task CreateFixtureAsync(
        SqliteConnection connection,
        bool baselineOnly = false,
        bool includeGeneratedTable = true,
        string? generatedExpression = null,
        string? checkExpression = "\"RightBound\" > \"LeftBound\""
    )
    {
        var transitionColumns = baselineOnly
            ? "\"old_code\" TEXT NULL, \"LegacyCode\" INTEGER NULL"
            : "\"renamed_code\" TEXT NULL, \"AddedCode\" TEXT NULL";

        await ExecuteSqlAsync(connection,
            "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL); "
            + "INSERT INTO __EFMigrationsHistory VALUES ('" + InitialMigrationId + "', '10.0.13'); "
            + (baselineOnly ? string.Empty
                : "INSERT INTO __EFMigrationsHistory VALUES ('" + TransitionMigrationId + "', '10.0.13'); ")
            + "CREATE TABLE tooling_entities (Id INTEGER NOT NULL PRIMARY KEY, Code TEXT NOT NULL, "
            + transitionColumns + "); CREATE UNIQUE INDEX IX_tooling_entities_Code ON tooling_entities (Code); "
            + "INSERT INTO tooling_entities (Id, Code) VALUES (1, 'baseline');");

        if (includeGeneratedTable)
        {
            var expression = generatedExpression
                ?? "\"RightBound\" > CASE WHEN \"LeftBound\" = 9223372036854775807 "
                + "THEN \"LeftBound\" ELSE \"LeftBound\" + 1 END";

            var check = checkExpression is null ? string.Empty
                : ", CONSTRAINT ck_scaffolding_generated_bounds CHECK (" + checkExpression + ")";

            await ExecuteSqlAsync(connection,
                "CREATE TABLE scaffolding_generated_nodes (Id INTEGER NOT NULL PRIMARY KEY, "
                + "LeftBound INTEGER NOT NULL, RightBound INTEGER NOT NULL, HasDescendants INTEGER NOT NULL "
                + "GENERATED ALWAYS AS (" + expression + ") VIRTUAL" + check + "); "
                + "CREATE INDEX IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound "
                + "ON scaffolding_generated_nodes (HasDescendants, LeftBound, RightBound);");
        }
    }
}
