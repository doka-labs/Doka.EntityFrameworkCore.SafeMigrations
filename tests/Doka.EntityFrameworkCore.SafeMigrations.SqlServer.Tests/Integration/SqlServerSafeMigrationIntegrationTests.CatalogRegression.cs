namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Preserves a caller-owned temporary table while checking more than one column identifier.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AnalyzeAsync_PreservesCallerOwnedIdentifierTemporaryTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE #doka_sm_identifiers (Sentinel int NOT NULL); "
            + "INSERT INTO #doka_sm_identifiers VALUES (73); "
            + "CREATE TABLE dbo.identifier_items (Id int NOT NULL, Caption nvarchar(80) NULL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("identifier_items", new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "identifier_items", new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var sentinel = await ReadContextIntAsync(context, "SELECT Sentinel FROM #doka_sm_identifiers;");
        var temporaryTableCount = await ReadContextIntAsync(context,
            "SELECT COUNT(*) FROM tempdb.sys.tables "
            + "WHERE name LIKE N'#doka[_]sm[_]identifiers[_]%';");

        // Assert
        Assert.Equal(2, analyses.Count);
        Assert.All(analyses,
            static analysis => Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState));
        Assert.Equal(73, sentinel);
        Assert.Equal(1, temporaryTableCount);
    }

    /// <summary>
    /// Does not accept sparse or row-guid storage as an ordinary expected column.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("int SPARSE NULL", typeof(int), "int", "is_sparse")]
    [InlineData("uniqueidentifier ROWGUIDCOL NULL", typeof(Guid), "uniqueidentifier", "is_rowguidcol")]
    public async Task OrdinaryColumn_RejectsUnmodeledPhysicalStorage(
        string physicalDefinition,
        Type clrType,
        string storeType,
        string catalogFacet
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.physical_items (Value " + physicalDefinition + ");");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("physical_items", new ExpectedColumnDefinition("Value", clrType, true, storeType),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-unmodeled-column-storage"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var physicalCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.physical_items', N'U') "
            + "AND name = N'Value' AND " + catalogFacet + " = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, physicalCount);
    }

    /// <summary>
    /// Shares provider facet inference between catalog analysis, baseline DDL, and replay guards.
    /// </summary>
    [SqlServerLiveFact]
    public async Task OmittedStoreType_InferredFacetsApplyMatchAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("inferred_items",
            [
                new ExpectedColumnDefinition("AnsiCaption", typeof(string), true, isUnicode: false, maxLength: 80),
                new ExpectedColumnDefinition("FixedCaption", typeof(string), true,
                    isUnicode: true, maxLength: 80, isFixedLength: true),
                new ExpectedColumnDefinition("Amount", typeof(decimal), true, precision: 12, scale: 3),
            ]), SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-inferred-column-facets"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var matchingColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
            + "WHERE c.object_id = OBJECT_ID(N'dbo.inferred_items', N'U') AND "
            + "((c.name = N'AnsiCaption' AND ty.name = N'varchar' AND c.max_length = 80) "
            + "OR (c.name = N'FixedCaption' AND ty.name = N'nchar' AND c.max_length = 160) "
            + "OR (c.name = N'Amount' AND ty.name = N'decimal' AND c.precision = 12 AND c.scale = 3));");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(3, matchingColumns);
    }

    /// <summary>
    /// Accepts one physical spelling alias consistently in classifiers and unexpected-object inventory.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("PHYSICAL_ITEMS", "ID", "IX_PHYSICAL_ID")]
    [InlineData("physical_items ", "id ", "ix_physical_id ")]
    public async Task CatalogSpellingAlias_ClassificationAndInventoryAgree(
        string physicalTable,
        string physicalColumn,
        string physicalIndex
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE dbo.[{physicalTable}] ([{physicalColumn}] int NOT NULL); "
            + $"CREATE INDEX [{physicalIndex}] ON dbo.[{physicalTable}] ([{physicalColumn}]);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("physical_items",
            [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_physical_id", "physical_items",
            [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-physical-spelling-alias"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(2, report.Assessments.Count);
        Assert.All(report.Assessments,
            static assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Empty(report.UnexpectedObjects);
    }

    /// <summary>
    /// Rejects distinct requested names that refer to one physical catalog identity.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("VALUE", "value")]
    [InlineData("value", "value ")]
    public async Task RequestedSpellingAliases_AreUnsupportedWithoutMutation(
        string first,
        string second
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.alias_items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("alias_items", new ExpectedColumnDefinition(first, typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn("alias_items", new ExpectedColumnDefinition(second, typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columnCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.alias_items', N'U');");

        // Assert
        Assert.Equal(2, analyses.Count);
        Assert.All(analyses,
            static analysis => Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState));
        Assert.Equal(51002, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, columnCount);
    }

    /// <summary>
    /// Retains original ordinals and delayed missing-reference safety across more than one transport batch.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DelayedClassifiers_MoreThanOneBatchPreservesEveryOriginalOrdinal()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.delayed_items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < 521; ordinal++)
        {
            builder.EnsureColumn(ordinal % 3 == 0 ? "delayed_missing" : "delayed_items",
                new ExpectedColumnDefinition($"required_{ordinal:D4}", typeof(int), false, "int"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-delayed-multiple-batches"));

        // Assert
        Assert.Equal(521, report.Assessments.Count);
        for (var ordinal = 0; ordinal < report.Assessments.Count; ordinal++)
        {
            var assessment = report.Assessments[ordinal];
            Assert.Equal(ordinal, assessment.Ordinal);
            Assert.Equal($"required_{ordinal:D4}", assessment.ObjectName);
            Assert.Equal(
                ordinal % 3 == 0
                    ? SafeMigrationObservedState.PrerequisiteMissing
                    : SafeMigrationObservedState.Missing,
                assessment.ObservedState);
        }
    }

    /// <summary>
    /// Rejects physically nullable primary-key columns regardless of the current row contents.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryKey_NullablePhysicalColumnIsUnsupportedBeforeDdl(bool hasNonNullRow)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.nullable_key (Id int NULL); "
            + (hasNonNullRow ? "INSERT INTO dbo.nullable_key VALUES (1);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition("PK_nullable_key", "nullable_key", ["Id"]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var nullableColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.nullable_key', N'U') "
            + "AND name = N'Id' AND is_nullable = 1;");

        var primaryKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.nullable_key', N'U') "
            + "AND type = 'PK';");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("primary_key_nullable_column", analysis.Code);
        Assert.Equal(51002, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, nullableColumns);
        Assert.Equal(0, primaryKeys);
    }

    /// <summary>
    /// Requires one existing primary key to have the requested physical name before treating it as matching.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryKey_ExistingKeyWithDifferentNameIsDifferentBeforeDdl(bool matchingName)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.existing_key (Id int NOT NULL CONSTRAINT PK_existing PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition(
            matchingName ? "PK_existing" : "PK_requested", "existing_key", ["Id"]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var originalKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.existing_key', N'U') "
            + "AND type = 'PK' AND name = N'PK_existing';");

        var requestedKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'PK_requested';");

        // Assert
        Assert.Equal(matchingName ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            Assert.Single(analyses).ObservedState);
        if (matchingName)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        }

        Assert.Equal(1, originalKeys);
        Assert.Equal(0, requestedKeys);
    }

    private static async Task<int> ReadContextIntAsync(
        DbContext context,
        string sql
    )
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
