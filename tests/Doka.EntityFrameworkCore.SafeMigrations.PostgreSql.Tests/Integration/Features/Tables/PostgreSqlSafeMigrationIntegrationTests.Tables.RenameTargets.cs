namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Combines projected source creation with destination occupancy and accepted drops.</summary>
    /// <param name="targetExists">Whether the destination already exists in its own namespace.</param>
    /// <param name="dropTarget">Whether an earlier accepted drop frees that destination.</param>
    /// <param name="explicitSchema">Whether the destination is in an explicitly named schema.</param>
    /// <param name="intermediateExists">Whether Npgsql's source-schema intermediate name is occupied.</param>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public async Task RenameTarget_ProjectedSourceRespectsOccupiedDestination(
        bool targetExists,
        bool dropTarget,
        bool explicitSchema,
        bool intermediateExists
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var targetSchema = explicitSchema ? "rename_scope" : null;
        var qualifiedTarget = explicitSchema ? "rename_scope.target_rows" : "public.target_rows";
        var decoy = explicitSchema ? "public.target_rows" : "rename_scope.target_rows";
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA rename_scope; "
            + (!explicitSchema || intermediateExists ? $"CREATE TABLE {decoy} (value integer); " : string.Empty)
            + (targetExists
                ? $"CREATE TABLE {qualifiedTarget} (value integer); INSERT INTO {qualifiedTarget} VALUES (99);"
                : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("source_rows",
                [new ExpectedColumnDefinition("value", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        if (dropTarget)
        {
            builder.DropTableIfExists("target_rows", targetSchema);
        }

        builder.RenameTableIfExists("source_rows", "target_rows", newSchema: targetSchema);
        var rename = (SafeMigrationOperation)builder.Operations[^1];
        var blocked = (targetExists && !dropTarget) || intermediateExists;

        // Act
        var evidence = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rename-target-projected-source"));

        if (!blocked)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var targetRows = targetExists || !blocked
            ? await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM {qualifiedTarget};")
            : 0;

        // Assert
        var analysis = Assert.Single(evidence);
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(targetExists, analysis.RenameTargetExists);
        Assert.Equal(explicitSchema ? intermediateExists : (bool?)null, analysis.RenameIntermediateTargetExists);
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(blocked ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        Assert.Equal(targetExists && blocked ? 1 : 0, targetRows);
    }

    /// <summary>Preserves idempotent missing-source behavior even when the destination exists.</summary>
    /// <param name="crossSchema">Whether both final and intermediate names are occupied.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameTarget_MissingSourceReplayRemainsNoOp(
        bool crossSchema
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE target_rows (value integer); INSERT INTO target_rows VALUES (99); "
            + (crossSchema ? "CREATE SCHEMA rename_scope; CREATE TABLE rename_scope.target_rows (value integer);"
                : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("source_rows", "target_rows", newSchema: crossSchema ? "rename_scope" : null);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rename-target-missing-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM target_rows WHERE value = 99;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, rows);
    }

    /// <summary>Rejects a source-schema collision at runtime before invoking the unchanged baseline.</summary>
    [Fact]
    public async Task RenameTarget_ExistingSourceRejectsIntermediateCollisionAtRuntime()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA rename_scope; CREATE TABLE source_rows (value integer); "
            + "INSERT INTO source_rows VALUES (7); CREATE TABLE target_rows (value integer); "
            + "INSERT INTO target_rows VALUES (99);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("source_rows", "target_rows", newSchema: "rename_scope");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rename-target-intermediate-runtime"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM source_rows WHERE value = 7;");
        var targetRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM target_rows WHERE value = 99;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, targetRows);
    }

    /// <summary>Allows a projected source after an accepted drop frees the exact intermediate namespace.</summary>
    [Fact]
    public async Task RenameTarget_AcceptedIntermediateDropAllowsProjectedSource()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA rename_scope; CREATE TABLE target_rows (value integer);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("source_rows",
                [new ExpectedColumnDefinition("value", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropTableIfExists("target_rows");
        builder.RenameTableIfExists("source_rows", "target_rows", newSchema: "rename_scope");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rename-target-intermediate-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var targetRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM rename_scope.target_rows;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(0, targetRows);
    }

    /// <summary>Recognizes a view as a destination collision rather than treating only tables as occupied.</summary>
    [Fact]
    public async Task RenameTarget_ProjectedSourceRejectsViewDestination()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE VIEW target_rows AS SELECT 99 AS value;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("source_rows",
                [new ExpectedColumnDefinition("value", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameTableIfExists("source_rows", "target_rows");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rename-target-view"));

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM target_rows WHERE value = 99;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal(1, rows);
    }

    /// <summary>Keeps sparse rename-target evidence aligned across statement and batch boundaries.</summary>
    /// <param name="native">Whether the transport exposes native PostgreSQL batches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameTarget_BatchedEvidencePreservesSparseOperationOwnership(
        bool native
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE target_rows (value integer);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new NpgsqlConnection(connectionString), nativeBatch: native);

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var index = 0; index < 65; index++)
        {
            builder.RenameTableIfExists($"missing_source_{index}", index % 2 == 0 ? "target_rows" : "absent_rows");
            builder.EnsureTable(new ExpectedTableDefinition($"missing_table_{index}",
                    [new ExpectedColumnDefinition("value", typeof(int), true, "integer")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        Assert.Equal(130, analyses.Count);
        for (var ordinal = 0; ordinal < analyses.Count; ordinal++)
        {
            Assert.Equal(SafeMigrationObservedState.Missing, analyses[ordinal].ObservedState);
            Assert.Equal(ordinal % 2 == 0 ? ordinal % 4 == 0 : (bool?)null, analyses[ordinal].RenameTargetExists);
        }
    }
}
