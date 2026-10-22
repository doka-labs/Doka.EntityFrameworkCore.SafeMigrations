namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Later accepted owners and sources cannot promote a native-invalid captured identifier role.</summary>
    /// <param name="createOwner">Whether the stream first creates its previously missing table.</param>
    /// <param name="source">The exact captured source head, including its native delimiter role.</param>
    /// <param name="ready">Whether native syntax and accepted source metadata both certify the ADD.</param>
    [Theory]
    [InlineData(false, "group", false)]
    [InlineData(true, "group", false)]
    [InlineData(false, "`group`", true)]
    [InlineData(true, "`group`", true)]
    [InlineData(false, "value", true)]
    [InlineData(true, "value", true)]
    public async Task CapturedSourceSyntaxCannotBorrowLaterPrerequisites(bool createOwner, string source, bool ready)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var sourceName = source.Trim('`');
        if (!createOwner)
        {
            await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL);");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (createOwner)
        {
            builder.EnsureTable(new ExpectedTableDefinition(Table,
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.EnsureColumn(Table, new ExpectedColumnDefinition(sourceName, typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "later_source_result", Table = Table, ClrType = typeof(bool), ColumnType = "tinyint(1)",
            ComputedColumnSql = source + " > 0", IsNullable = true, IsStored = false,
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-source-before-owner"), CancellationToken.None);

        // Assert
        Assert.Equal(ready ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(ready ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[^1].Action);
        if (!ready)
        {
            Assert.Equal("captured_expression_identifier_keyword_unverified", report.Assessments[^1].AnalysisCode);
        }
    }

    /// <summary>A reserved unquoted source word cannot borrow a valid native backtick identifier certificate.</summary>
    /// <param name="check">Whether the captured source belongs to a CHECK rather than a generated column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedReservedSourceIdentifierCannotMatchQuotedNativeColumn(bool check)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`select` int NOT NULL, "
            + "native_result tinyint(1) GENERATED ALWAYS AS (`select` > 0) VIRTUAL, "
            + "CONSTRAINT ck_source_reserved CHECK(`select` > 0));");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (check)
        {
            builder.EnsureCheckConstraint(SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
            {
                Name = "ck_source_reserved", Table = Table, Sql = "select > 0",
            }), SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
            {
                Name = "native_result", Table = Table, ClrType = typeof(bool), ColumnType = "tinyint(1)",
                ComputedColumnSql = "select > 0", IsStored = false, IsNullable = true,
            }), SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            $"PREPARE source_keyword_probe FROM 'SELECT (select > 0) FROM {Table}';"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-reserved-native-source"), CancellationToken.None);

        // Assert
        Assert.Equal(1064, Assert.IsType<MySqlException>(nativeFailure).Number);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("captured_expression_identifier_keyword_unverified",
            Assert.Single(report.Assessments).AnalysisCode);
    }

    /// <summary>Quoted keyword roots remain native-valid without guessing the MariaDB reserved-word subset.</summary>
    [Fact]
    public async Task CapturedQuotedReservedSourceRetainsNativeReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`select` int NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_quoted_reserved", Table = Table, Sql = "`select` > 0",
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-quoted-keyword"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Null(failure);
    }

    /// <summary>Keyword qualification does not mistake a legal post-dot member for a reserved reference head.</summary>
    [Fact]
    public async Task CapturedQualifiedReservedTerminalPassesNativeSyntaxQualification()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`select` int NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_qualified_reserved", Table = Table, Sql = $"{Table}.select > 0",
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            $"PREPARE qualified_source_probe FROM 'SELECT {Table}.select > 0 FROM {Table}'; "
            + "DEALLOCATE PREPARE qualified_source_probe;"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-qualified-keyword"), CancellationToken.None);

        // Assert
        Assert.Null(nativeFailure);
        Assert.DoesNotContain(report.Assessments,
            static item => item.AnalysisCode == "captured_expression_identifier_keyword_unverified");
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>MariaDB's parse-only proof distinguishes legal bare words that its metadata cannot classify.</summary>
    [Fact]
    public async Task CapturedNonreservedKeywordHeadExposesEngineMetadataBoundary()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`value` int NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_nonreserved_keyword", Table = Table, Sql = "value > 0",
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            $"PREPARE nonreserved_source_probe FROM 'SELECT value > 0 FROM {Table}'; "
            + "DEALLOCATE PREPARE nonreserved_source_probe;"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-nonreserved-keyword"), CancellationToken.None);

        // Assert
        Assert.Null(nativeFailure);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal("classified_missing", Assert.Single(report.Assessments).AnalysisCode);
    }
}
