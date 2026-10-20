namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies that catalog traffic counters distinguish purpose, not only a shared result width.</summary>
public sealed class MySqlCatalogPurposeCounterTests
{
    /// <summary>Native batch accounting is independent of classifier, prerequisite or unrelated result shape.</summary>
    /// <param name="fieldCount">The synthetic native result width.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task NativeBatchCounterCountsDispatchWithoutInventingSequentialClassifierFacts(int fieldCount)
    {
        // Arrange
        await using var connection = new CatalogClassificationCountingConnection(
            new CatalogStatementTestConnection(true, (_, _) =>
            {
                var result = new System.Data.DataTable();
                for (var index = 0; index < fieldCount; index++)
                {
                    result.Columns.Add("field_" + index, typeof(int));
                }

                result.Rows.Add(new object[fieldCount]);

                return result;
            }), nativeBatch: true);

        await using var batch = connection.CreateBatch();
        var command = batch.CreateBatchCommand();
        command.CommandText = "SELECT 17;";
        batch.BatchCommands.Add(command);

        // Act
        await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
        var hasRow = await reader.ReadAsync(CancellationToken.None);

        // Assert
        Assert.True(hasRow);
        Assert.Equal(1, connection.NativeBatchExecutionCount);
        Assert.Equal(0, connection.ClassificationStatementCount);
        Assert.Equal(0, connection.PrerequisiteStatementCount);
    }

    /// <summary>Required-add and dependency facts have the same semantic counters on both transport paths.</summary>
    /// <param name="native">Whether the wrapper forwards a native batch.</param>
    /// <param name="purpose">The fixed catalog safety-fact purpose or unrelated control.</param>
    [Theory]
    [InlineData(false, "session")]
    [InlineData(true, "session")]
    [InlineData(false, "engine")]
    [InlineData(true, "engine")]
    [InlineData(false, "status")]
    [InlineData(true, "status")]
    [InlineData(false, "dependency")]
    [InlineData(true, "dependency")]
    [InlineData(false, "unrelated")]
    [InlineData(true, "unrelated")]
    public async Task ColumnSafetyFactsKeepPurposeAcrossNativeAndFallback(bool native, string purpose)
    {
        // Arrange
        await using var connection = new CatalogClassificationCountingConnection(
            new CatalogStatementTestConnection(native, (_, _) => SafetyFactResult(purpose)), nativeBatch: native);
        var sql = purpose switch
        {
            "session" => "SELECT @@default_storage_engine, @@SESSION.sql_mode, "
                + "@@SESSION.explicit_defaults_for_timestamp, 1;",
            "engine" => MySqlSafeMigrationProviderAnalyzer.BuildRequiredColumnAddOwnerEngineQuery(
                "SELECT 17 AS ordinal, 'items' AS owner", "'items'"),
            "status" => "SELECT 17, (CASE WHEN FALSE THEN 'complete' "
                + "WHEN FALSE THEN CASE WHEN FALSE THEN 'conflict' ELSE 'fresh' END "
                + "WHEN FALSE THEN CASE WHEN FALSE THEN 'conflict' ELSE 'staged' END ELSE 'ordinary' END) ORDER BY 1;",
            "dependency" => "SELECT /* doka_sm_column_dependencies */ 17, 'primary', NULL ORDER BY 1;",
            _ => "SELECT 17, 'complete', 'fresh', 'staged' ORDER BY 1;",
        };

        // Act
        bool hasRow;
        if (native)
        {
            await using var batch = connection.CreateBatch();
            var command = batch.CreateBatchCommand();
            command.CommandText = sql;
            batch.BatchCommands.Add(command);
            await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
            hasRow = await reader.ReadAsync(CancellationToken.None);
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            hasRow = await reader.ReadAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(hasRow);
        Assert.Equal(purpose == "session" ? 1 : 0, connection.RequiredAddSessionStatementCount);
        Assert.Equal(purpose == "engine" ? 1 : 0, connection.RequiredAddOwnerEngineStatementCount);
        Assert.Equal(purpose == "status" ? 1 : 0, connection.RequiredAddStatusStatementCount);
        Assert.Equal(purpose == "dependency" ? 1 : 0, connection.ColumnDependencyStatementCount);
        Assert.Equal(0, connection.NarrowingEligibilityStatementCount);
        Assert.Equal(0, connection.NarrowingDataStatementCount);
    }

    /// <summary>Lazy guards and narrowing eligibility count separately; unrelated results count as neither.</summary>
    /// <param name="purpose">The statement purpose represented by the deterministic transport.</param>
    /// <param name="guardCount">The expected lazy-guard statement count.</param>
    /// <param name="eligibilityCount">The expected narrowing-eligibility statement count.</param>
    [Theory]
    [InlineData("guard", 1, 0)]
    [InlineData("eligibility", 0, 1)]
    [InlineData("postgresql_eligibility", 0, 1)]
    [InlineData("unrelated", 0, 0)]
    [InlineData("quoted_marker", 0, 0)]
    public async Task ThreeFieldCatalogResultsKeepTheirActualPurpose(
        string purpose,
        int guardCount,
        int eligibilityCount
    )
    {
        // Arrange
        await using var connection = new CatalogClassificationCountingConnection(
            new CatalogStatementTestConnection(false, static (_, _) => ThreeFieldResult()));
        await using var command = connection.CreateCommand();
        command.CommandText = purpose switch
        {
            "guard" => "SELECT 17, CASE WHEN NOT COALESCE((FALSE), FALSE) THEN ('unsupported') ELSE NULL END, "
                + "CASE WHEN NOT COALESCE((FALSE), FALSE) THEN ('column_native_enum_mode_conflict') "
                + "ELSE NULL END ORDER BY 1;",
            "eligibility" => "SELECT 17, COALESCE((TRUE), FALSE), COALESCE((EXISTS (SELECT 1 "
                + "FROM INFORMATION_SCHEMA.COLUMNS narrowing_column WHERE "
                + "narrowing_column.CHARACTER_MAXIMUM_LENGTH > 5)), FALSE) ORDER BY 1;",
            // WHY: The shared counter must recognize PostgreSQL's lowercase
            // catalog identifiers without confusing unrelated three-field SQL.
            "postgresql_eligibility" => "SELECT 17, COALESCE((TRUE), FALSE), COALESCE((EXISTS (SELECT 1 "
                + "FROM information_schema.columns narrowing_column WHERE "
                + "narrowing_column.character_maximum_length > 5)), FALSE) ORDER BY 1;",
            "quoted_marker" => "SELECT 17, 'narrowing_column.CHARACTER_MAXIMUM_LENGTH', FALSE ORDER BY 1;",
            _ => "SELECT 17, TRUE, FALSE ORDER BY 1;",
        };

        // Act
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var hasRow = await reader.ReadAsync(CancellationToken.None);

        // Assert
        Assert.True(hasRow);
        Assert.Equal(3, reader.FieldCount);
        Assert.Equal(17, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(guardCount, connection.StateEvaluationGuardStatementCount);
        Assert.Equal(eligibilityCount, connection.NarrowingEligibilityStatementCount);
        Assert.Equal(0, connection.ClassificationStatementCount);
        Assert.Equal(0, connection.NarrowingDataStatementCount);
    }

    private static DataTable ThreeFieldResult()
    {
        var result = new DataTable();
        result.Columns.Add("ordinal", typeof(int));
        result.Columns.Add("first", typeof(object));
        result.Columns.Add("second", typeof(object));
        result.Rows.Add(17, true, false);

        return result;
    }

    private static DataTable SafetyFactResult(string purpose)
    {
        var result = new DataTable();
        if (purpose == "session")
        {
            result.Columns.Add("engine", typeof(string));
            result.Columns.Add("mode", typeof(string));
            result.Columns.Add("timestamp", typeof(int));
            result.Columns.Add("checks", typeof(int));
            result.Rows.Add("InnoDB", "STRICT_TRANS_TABLES", 1, 1);
        }
        else
        {
            result.Columns.Add("ordinal", typeof(int));
            result.Columns.Add("fact", typeof(object));
            result.Rows.Add(17, purpose == "status" ? (object)"ordinary" : true);
        }

        return result;
    }
}
