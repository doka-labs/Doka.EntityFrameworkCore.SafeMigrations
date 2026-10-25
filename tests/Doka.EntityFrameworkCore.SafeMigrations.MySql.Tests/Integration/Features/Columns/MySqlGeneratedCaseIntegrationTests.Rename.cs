namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Complete membership cannot bind two physical identifiers through metadata accent folding.</summary>
    /// <param name="dependentAccentMember">Whether only the accent-distinct member references the source.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlRenameCertificatePreservesAccentDistinctGeneratedMemberIdentity(bool dependentAccentMember)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (s int NULL, other int NULL, "
            + "`e` int GENERATED ALWAYS AS (other + 1) VIRTUAL, "
            + "`\u00e9` int GENERATED ALWAYS AS (" + (dependentAccentMember ? "s" : "other")
            + " + 1) VIRTUAL); " + $"INSERT INTO `{Table}` (s, other) VALUES (7, 11);");
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("other"),
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1));

        var expected = new ExpectedTableDefinition(Table,
        [
            new ExpectedColumnDefinition("s", typeof(int), true, "int"),
            new ExpectedColumnDefinition("other", typeof(int), true, "int"),
            new ExpectedColumnDefinition("E", typeof(int), true, "int", computedExpression: expression,
                isStored: false),
            new ExpectedColumnDefinition("\u00e9", typeof(int), true, "int", computedExpression: expression,
                isStored: false),
        ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(expected, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("s", Table, "renamed_s");
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin(operations);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var plan = capture.Complete()[1];

        // Act
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        var parameters = new MySqlCatalogQueryParameterizer(command,
            context.GetService<IRelationalTypeMappingSource>());

        command.CommandText = "SELECT (" + plan.RenderStateExpression(parameters.Add) + ");";
        var state = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            $"ALTER TABLE `{Table}` RENAME COLUMN s TO renamed_s;"));
        var rows = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}`;");

        // Assert
        Assert.Equal(dependentAccentMember ? "unsupported" : "matching", state);
        Assert.Equal(dependentAccentMember, nativeFailure is not null);
        Assert.Equal(1, rows);
    }

    /// <summary>A single MySQL ALTER cannot rename a source referenced by an accepted generated expression.</summary>
    /// <param name="freshOwner">Whether an earlier accepted strict declaration creates the owner.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlGeneratedDependencyRenameIsRejectedBeforeNativeAlter(bool freshOwner)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        if (!freshOwner)
        {
            await ExecuteOperationsAsync(context, initial.Operations);
            await SeedCaseRowsAsync(connectionString);
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("LEFT_BOUND", Table, "renamed_left");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-rename-refused"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var unchanged = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'left_bound';");
        var rows = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[1].ObservedState);
        Assert.Equal("generated_column_rename_dependency", report.Assessments[1].AnalysisCode);
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);
        Assert.Equal("doka_sm_unsupported", diagnostic.GuardCode);
        Assert.Equal(1, diagnostic.OperationOrdinal);
        Assert.NotEqual(3108, native.Number);
        Assert.Equal(1, unchanged);
        Assert.Equal(freshOwner ? 0 : 5, rows);
    }

    /// <summary>Complete exact native generated membership proves a genuinely unrelated source rename.</summary>
    /// <param name="includeManagedData">Whether a typed data write precedes the metadata-only rename.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlUnrelatedRenameUsesCompleteGeneratedMemberCertificate(bool includeManagedData)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        if (includeManagedData)
        {
            builder.EnsureModelManagedDataFromModel(Table, ["id"], ["int"],
                ["id", "left_bound", "right_bound"], ["int", "bigint", "bigint"],
                new object?[,] { { 42, 1L, 2L } });
        }

        builder.RenameColumnIfExists("id", Table, "renamed_id");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unrelated-rename"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var verified = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unrelated-rename-verify"), CancellationToken.None);
        var rows = failure is null ? await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}`;") : -1;

        // Assert
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(report));
        Assert.Null(failure);
        Assert.True(verified.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(verified));
        Assert.Equal(includeManagedData ? 6 : 5, rows);
    }

    /// <summary>Unknown native generated members are not certified by token searches or a missing source.</summary>
    /// <param name="sourceExists">Whether a rename would actually execute rather than skip.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MySqlUnknownGeneratedSetCannotAuthorizeExecutingRename(bool sourceExists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (id int NOT NULL, unrelated int NULL, "
            + "derived int GENERATED ALWAYS AS (id + 1) VIRTUAL); "
            + $"INSERT INTO `{Table}` (id, unrelated) VALUES (1, 7);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists(sourceExists ? "unrelated" : "already_absent", Table, "renamed_value");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-unknown-rename"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}` WHERE derived = 2;");

        // Assert
        Assert.Equal(sourceExists ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(sourceExists ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.Missing,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(sourceExists ? "generated_column_rename_unproven" : "classified_missing",
            report.Assessments[0].AnalysisCode);
        Assert.Equal(sourceExists, failure is not null);
        Assert.Equal(1, rows);
    }

}
