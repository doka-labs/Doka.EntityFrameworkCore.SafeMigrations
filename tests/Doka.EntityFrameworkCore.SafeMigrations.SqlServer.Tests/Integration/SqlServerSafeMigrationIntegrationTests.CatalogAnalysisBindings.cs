namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Same-session impersonation cannot retain the preceding full-visibility inventory proof.</summary>
    [SqlServerLiveFact]
    public async Task CatalogPreamble_ImpersonatedPrincipalRequiresFreshInventoryEvidence()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.inventory_items (Id int NULL); "
            + "CREATE USER inventory_limited WITHOUT LOGIN;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("inventory_items",
            [new ExpectedColumnDefinition("Id", typeof(int), true, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var analyses = await analyzer.AnalyzeAsync(context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        // Act
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'inventory_limited';");
        Exception? failure;
        try
        {
            failure = await Record.ExceptionAsync(() => analyzer.FindUnexpectedObjectsAsync(context, builder.Operations));
        }
        finally
        {
            // WHY: Impersonation belongs to this test, not to the returned pooled session or scope cleanup.
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analyses).ObservedState);
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Similar parameterized templates retain distinct row classifications and evidence ownership.</summary>
    [SqlServerLiveFact]
    public async Task CatalogBindings_DistinctKeysRetainSourceTargetDifferentAndMissingEvidence()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.binding_rows (Id int NOT NULL PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL); INSERT dbo.binding_rows VALUES "
            + "(1,N'source'),(2,N'target'),(3,N'edited');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var key = 1; key <= 4; key++)
        {
            builder.UpdateModelManagedDataFromModel("binding_rows", ["Id"], ["int"],
                new object?[,] { { key } }, ["Caption"], ["nvarchar(80)"],
                new object?[,] { { "source" } }, new object?[,] { { "target" } });
        }

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var unchanged = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.binding_rows "
            + "WHERE (Id=1 AND Caption=N'source') OR (Id=2 AND Caption=N'target') OR (Id=3 AND Caption=N'edited');");

        // Assert
        Assert.Equal([SafeMigrationObservedState.TransitionReady, SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Different, SafeMigrationObservedState.PrerequisiteMissing],
            analyses.Select(analysis => analysis.ObservedState));
        Assert.Equal([SafeMigrationModelManagedRowState.Source, SafeMigrationModelManagedRowState.Target,
            SafeMigrationModelManagedRowState.Different, SafeMigrationModelManagedRowState.Missing],
            analyses.Select(analysis => Assert.Single(analysis.ModelManagedDataEvidence!.RowStates)));
        Assert.False(analyses[0].PostconditionSatisfied);
        Assert.True(analyses[1].PostconditionSatisfied);
        Assert.Equal(3, unchanged);
    }

    /// <summary>Equal UTC instants with different offsets remain an explicit representation transition.</summary>
    [SqlServerLiveFact]
    public async Task CatalogBindings_OffsetOnlyTransitionRetainsSourceAndTargetRepresentations()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.binding_offsets "
            + "(Id int NOT NULL PRIMARY KEY, Zoned datetimeoffset(7) NOT NULL); "
            + "INSERT dbo.binding_offsets VALUES (1,CAST('2026-01-02T03:04:05+00:00' AS datetimeoffset(7)));");
        await using var context = CreateContext(connectionString);
        var source = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var target = source.ToOffset(TimeSpan.FromHours(1));
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel("binding_offsets", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Zoned"], ["datetimeoffset(7)"],
            new object?[,] { { source } }, new object?[,] { { target } });

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var after = await analyzer.AnalyzeAsync(context, operations);
        var offset = await ScalarIntAsync(connectionString,
            "SELECT DATEPART(TZOFFSET,Zoned) FROM dbo.binding_offsets WHERE Id=1;");

        // Assert
        var initial = Assert.Single(before);
        Assert.Equal(SafeMigrationObservedState.TransitionReady, initial.ObservedState);
        Assert.Equal(SafeMigrationModelManagedRowState.Source,
            Assert.Single(initial.ModelManagedDataEvidence!.RowStates));
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(after).ObservedState);
        Assert.Equal(SafeMigrationModelManagedRowState.Target,
            Assert.Single(Assert.Single(after).ModelManagedDataEvidence!.RowStates));
        Assert.Equal(60, offset);
    }

    /// <summary>Unicode, binary, decimal, and temporal source parameters round-trip without loss.</summary>
    [SqlServerLiveFact]
    public async Task CatalogBindings_FullSourceValuesApplyAndVerifyWithoutRounding()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.binding_values (Id int NOT NULL PRIMARY KEY, "
            + "Caption nvarchar(80) NULL, Bytes varbinary(8) NULL, Amount decimal(38,20) NULL, "
            + "Moment datetime2(7) NULL, Zoned datetimeoffset(7) NULL, Clock time(7) NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("binding_values", ["Id"], ["int"],
            ["Id", "Caption", "Bytes", "Amount", "Moment", "Zoned", "Clock"],
            ["int", "nvarchar(80)", "varbinary(8)", "decimal(38,20)", "datetime2(7)", "datetimeoffset(7)", "time(7)"],
            new object?[,]
            {
                { 1, "a\u20ac\ud83d\ude00", new byte[] { 0, 1, 128, 255 }, 123456789.12345678901234567890m,
                    new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567),
                    new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567),
                    new TimeSpan(123456789) },
                { 2, null, null, null, null, null, null },
            });

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var after = await analyzer.AnalyzeAsync(context, operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.binding_values;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        var matching = Assert.Single(after);
        Assert.Equal(SafeMigrationObservedState.Matching, matching.ObservedState);
        Assert.True(matching.PostconditionSatisfied);
        Assert.Equal([SafeMigrationModelManagedRowState.Target, SafeMigrationModelManagedRowState.Target],
            matching.ModelManagedDataEvidence!.RowStates);
        Assert.Equal(2, rows);
    }

    /// <summary>Narrow destination conversions cannot borrow a pre-truncated source parameter as proof.</summary>
    /// <param name="kind">The conversion that must remain fail-closed.</param>
    [SqlServerLiveTheory]
    [InlineData("ansi")]
    [InlineData("decimal")]
    [InlineData("temporal")]
    public async Task CatalogBindings_NarrowConversionsRemainUnsupported(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var storeType = kind switch
        {
            "ansi" => "varchar(80)",
            "decimal" => "decimal(5,2)",
            "temporal" => "datetime2(3)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        object source = kind switch
        {
            "ansi" => "a\ud83d\ude00",
            "decimal" => 999.995m,
            "temporal" => DateTime.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.narrow_binding_values "
            + "(Id int NOT NULL PRIMARY KEY, Value " + storeType + " NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("narrow_binding_values", ["Id"], ["int"],
            ["Id", "Value"], ["int", storeType], new object?[,] { { 1, source } });

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.narrow_binding_values;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(analyses).ObservedState);
        Assert.False(Assert.Single(analyses).PostconditionSatisfied);
        Assert.Equal(0, rows);
    }

    /// <summary>Occupancy covers non-table objects and missing schemas without declaring them absent.</summary>
    [SqlServerLiveFact]
    public async Task AbsentTablePrepass_PreservesWrongObjectKindsAndSchemaPrerequisites()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE VIEW dbo.occupied_view AS SELECT 1 AS Id;");
        await ExecuteSqlAsync(connectionString, "CREATE SEQUENCE dbo.occupied_sequence AS int START WITH 1; "
            + "CREATE SYNONYM dbo.occupied_synonym FOR dbo.not_created;");
        await using var context = CreateContext(connectionString);
        SafeMigrationOperation[] operations =
        [
            CatalogTargetTable("missing_table"), CatalogTargetTable("occupied_view"),
            CatalogTargetTable("occupied_sequence"), CatalogTargetTable("occupied_synonym"),
            CatalogTargetTable("missing_schema_table", "not_created"),
        ];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        Assert.Equal([SafeMigrationObservedState.Missing, SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Different, SafeMigrationObservedState.Different,
            SafeMigrationObservedState.PrerequisiteMissing], analyses.Select(analysis => analysis.ObservedState));
        Assert.All(analyses, analysis => Assert.False(analysis.PostconditionSatisfied));
    }

    /// <summary>A new occupant after the absence read is a conflict, even when its columns appear compatible.</summary>
    /// <param name="view">Whether the concurrent occupant is a view rather than an ordinary table.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentTablePrepass_NewOccupantAfterProofRemainsDifferent(bool view)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var operation = CatalogTargetTable("changed_target");
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var absent = await SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
            connection, null, [operation], 0, 1, 67, CancellationToken.None);

        // WHY: This deliberately exercises the race between the bounded metadata proof and
        // later classification. Absence is not a promise that an intervening creator cannot run.
        await ExecuteSqlAsync(connectionString, view
            ? "CREATE VIEW dbo.changed_target AS SELECT 1 AS Id;"
            : "CREATE TABLE dbo.changed_target (Id int NOT NULL);");

        var plan = catalog.Build(operation, targetTableKnownAbsent: absent[0]);
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(connection, null, 67,
            [plan], 0, results, CancellationToken.None);

        // Assert
        Assert.True(Assert.Single(absent));
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(results).ObservedState);
        Assert.False(Assert.Single(results).PostconditionSatisfied);
    }

    /// <summary>Missing-table fast paths retain invalid collation and inline-FK rejection.</summary>
    /// <param name="collation">Whether collation rather than the inline foreign key is invalid.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentTablePrepass_InvalidAuthoredGuardRemainsBlocked(bool collation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition("guarded_absent_target",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)",
                    collation: collation ? new SafeMigrationCollationIdentifier("not_an_engine_collation") : null)],
            foreignKeys: collation ? [] :
            [new ExpectedForeignKeyDefinition("FK_guarded_absent_parent", "guarded_absent_target", ["Id"],
                "not_created", ["Id"])]);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.guarded_absent_target');");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(collation ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
        Assert.Equal(collation ? "column_collation_unproven" : "inline_foreign_key_prerequisite", analysis.Code);
        Assert.Equal(0, tables);
    }

    /// <summary>Creates a strict one-column table target with a generic physical identity.</summary>
    private static SafeMigrationOperation CatalogTargetTable(string name, string? schema = null)
        => new(new EnsureTableIntent(new ExpectedTableDefinition(name,
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: schema),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);
}
