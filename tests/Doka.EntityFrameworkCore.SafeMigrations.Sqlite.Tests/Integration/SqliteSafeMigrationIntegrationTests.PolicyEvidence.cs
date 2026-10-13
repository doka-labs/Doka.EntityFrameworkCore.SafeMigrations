namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>
    /// An existence-only no-op cannot certify an unapplied column definition for a later strict ensure.
    /// </summary>
    [Fact]
    public async Task ExistenceOnlyColumnDrift_FollowedByStrictEnsureRejectsBeforeMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_column (Id INTEGER NOT NULL, Value TEXT NULL); "
            + "INSERT INTO projection_column (Id, Value) VALUES (1, 'preserve');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedColumnDefinition("Value", typeof(int), isNullable: true, storeType: "INTEGER");
        builder.EnsureColumn("projection_column", definition, SafeMigrationPolicy.ExistenceOnly);
        builder.EnsureColumn("projection_column", definition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-column-policy-evidence"));

        // WHY: Execute independently of the blocked report so the runtime
        // projection must reject the same fabricated evidence itself.
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_column WHERE Id = 1 AND Value = 'preserve';");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal("TEXT", snapshot.Tables["projection_column"].Columns["Value"].StoreType);
        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>A matching column remains a genuine no-op across both policies.</summary>
    [Fact]
    public async Task MatchingColumn_ExistenceOnlyThenStrictEnsureRemainNoOps()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_column (Id INTEGER NOT NULL, Value TEXT NULL); "
            + "INSERT INTO projection_column (Id, Value) VALUES (1, 'preserve');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedColumnDefinition("Value", typeof(string), isNullable: true, storeType: "TEXT");
        builder.EnsureColumn("projection_column", definition, SafeMigrationPolicy.ExistenceOnly);
        builder.EnsureColumn("projection_column", definition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-matching-column-policy"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_column WHERE Id = 1 AND Value = 'preserve';");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        });

        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>
    /// An existence-only no-op cannot replace a physical primary key's ordered columns in projection.
    /// </summary>
    [Fact]
    public async Task ExistenceOnlyPrimaryKeyDrift_FollowedByStrictEnsureRejectsBeforeMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_key (Id INTEGER NOT NULL, OtherId INTEGER NOT NULL, "
            + "CONSTRAINT physical_projection_pk PRIMARY KEY (OtherId)); "
            + "INSERT INTO projection_key (Id, OtherId) VALUES (1, 2);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedPrimaryKeyDefinition("expected_projection_pk", "projection_key", ["Id"]);
        builder.EnsurePrimaryKey(definition, SafeMigrationPolicy.ExistenceOnly);
        builder.EnsurePrimaryKey(definition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-primary-key-policy-evidence"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_key WHERE Id = 1 AND OtherId = 2;");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal("OtherId", Assert.Single(snapshot.Tables["projection_key"].PrimaryKeyColumns));
        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>A matching physical primary-key alias remains safe under a later strict policy.</summary>
    [Fact]
    public async Task MatchingPrimaryKeyAlias_ExistenceOnlyThenStrictEnsureRemainNoOps()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_key (Id INTEGER NOT NULL, OtherId INTEGER NOT NULL, "
            + "CONSTRAINT physical_projection_pk PRIMARY KEY (Id)); "
            + "INSERT INTO projection_key (Id, OtherId) VALUES (1, 2);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedPrimaryKeyDefinition("expected_projection_pk", "projection_key", ["Id"]);
        builder.EnsurePrimaryKey(definition, SafeMigrationPolicy.ExistenceOnly);
        builder.EnsurePrimaryKey(definition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-matching-primary-key-policy"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_key WHERE Id = 1 AND OtherId = 2;");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        });

        Assert.Equal("physical_projection_pk", snapshot.Tables["projection_key"].PrimaryKeyName);
        Assert.Equal("Id", Assert.Single(snapshot.Tables["projection_key"].PrimaryKeyColumns));
        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }
}
