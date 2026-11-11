namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Qualifies scalar roles, CHECK literal roles and native dependency binding preservation.</summary>
public sealed class SqlServerExpressionCatalogIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Uses the shared engine with a separately owned database per test.</summary>
    /// <param name="fixture">The assembly-owned SQL Server engine.</param>
    public SqlServerExpressionCatalogIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Captured DEFAULT functions retain source roles and reject unsupported quoting before DDL.</summary>
    /// <param name="sql">The exact provider-authored DEFAULT source.</param>
    /// <param name="unsupported">Whether the authored function identity cannot be certified.</param>
    [SqlServerLiveTheory]
    [InlineData("COALESCE(1,2)", false)]
    [InlineData("[COALESCE](1,2)", true)]
    [InlineData("\"COALESCE\"(1,2)", true)]
    [InlineData("`COALESCE`(1,2)", true)]
    public async Task CapturedDefaultFunctionRolesAreValidatedBeforeDdl(
        string sql,
        bool unsupported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_roles(Id int NOT NULL);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(name: "Value",
                table: "default_roles",
                type: "int",
                nullable: false,
                defaultValueSql: sql),
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("captured-default-roles"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations,
            CancellationToken.None));

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.default_roles') AND name=N'Value';",
            CancellationToken.None);

        var value = -1;
        SafeMigrationRunReport? replay = null;
        if (!unsupported)
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                "INSERT INTO dbo.default_roles(Id) VALUES(1);",
                CancellationToken.None);

            value = await ScalarIntAsync(connectionString,
                "SELECT Value FROM dbo.default_roles;",
                CancellationToken.None);

            replay = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context,
                    builder.Operations,
                    new SafeMigrationRunOptions("captured-default-replay"),
                    CancellationToken.None);
        }

        // Assert
        Assert.Equal(unsupported
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(unsupported
                ? 0
                : 1,
            columns);

        if (unsupported)
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported,
                Assert.Single(report.Assessments)
                    .ObservedState);

            Assert.IsType<NotSupportedException>(failure);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(1, value);
            Assert.NotNull(replay);
            Assert.Equal(SafeMigrationObservedState.Matching,
                Assert.Single(replay.Assessments)
                    .ObservedState);
        }
    }

    /// <summary>Computed roots and operands compile, retain UNKNOWN, and replay against the native catalog.</summary>
    /// <param name="role">The typed scalar context.</param>
    /// <param name="negative">The result for a negative source value.</param>
    /// <param name="unknown">The result for NULL, encoded as minus one when SQL NULL survives.</param>
    /// <param name="positive">The result for a positive source value.</param>
    [SqlServerLiveTheory]
    [InlineData("null-test", 0, 1, 0)]
    [InlineData("null-test-script", 0, 1, 0)]
    [InlineData("cast", 0, -1, 1)]
    [InlineData("cast-script", 0, -1, 1)]
    [InlineData("coalesce", 0, 0, 1)]
    [InlineData("comparison", 0, -1, 1)]
    [InlineData("arithmetic", 1, -1, 2)]
    [InlineData("between", 1, -1, 1)]
    [InlineData("in", 0, -1, 1)]
    public async Task ComputedScalarRolesPreserveNativeValuesAndReplay(
        string role,
        int negative,
        int unknown,
        int positive
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.scalar_roles(Id int NOT NULL PRIMARY KEY, Value int NULL); "
            + "INSERT INTO dbo.scalar_roles VALUES(1,-1),(2,NULL),(3,1);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var script = role.EndsWith("-script", StringComparison.Ordinal);
        var kind = script
            ? role[..^7]
            : role;

        var expression = ScalarExpression(kind);
        var integer = kind is "cast" or "arithmetic";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("scalar_roles",
            new ExpectedColumnDefinition("Result",
                integer
                    ? typeof(int)
                    : typeof(bool),
                kind != "null-test",
                integer
                    ? "int"
                    : "bit",
                computedExpression: expression,
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("scalar-roles"),
            CancellationToken.None);

        await ApplyScalarOperationsAsync(context, builder.Operations, script, CancellationToken.None);
        await ApplyScalarOperationsAsync(context, builder.Operations, script, CancellationToken.None);
        var replay = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("scalar-replay"),
            CancellationToken.None);

        var actualNegative = await ScalarIntAsync(connectionString,
            "SELECT COALESCE(CONVERT(int,Result),-1) FROM dbo.scalar_roles WHERE Id=1;",
            CancellationToken.None);

        var actualUnknown = await ScalarIntAsync(connectionString,
            "SELECT COALESCE(CONVERT(int,Result),-1) FROM dbo.scalar_roles WHERE Id=2;",
            CancellationToken.None);

        var actualPositive = await ScalarIntAsync(connectionString,
            "SELECT COALESCE(CONVERT(int,Result),-1) FROM dbo.scalar_roles WHERE Id=3;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(replay.Assessments)
                .ObservedState);

        Assert.Equal(negative, actualNegative);
        Assert.Equal(unknown, actualUnknown);
        Assert.Equal(positive, actualPositive);
    }

    /// <summary>Literal delimiter contents do not block an unrelated rename or authorize a dependent rename.</summary>
    /// <param name="literal">The native string literal's contents.</param>
    /// <param name="dependent">Whether the renamed column is actually referenced by the CHECK.</param>
    [SqlServerLiveTheory]
    [InlineData("\"", false)]
    [InlineData("`", false)]
    [InlineData("[", false)]
    [InlineData("\"", true)]
    [InlineData("`", true)]
    [InlineData("[", true)]
    public async Task CheckLiteralContentsRetainActualRenameDependencies(
        string literal,
        bool dependent
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_quotes",
            "quote_roles",
            "[Value]>0 AND LEN('" + literal + "')=1",
            null);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("quote_roles",
                [
                    new ExpectedColumnDefinition("Value", typeof(int), false, "int"),
                    new ExpectedColumnDefinition("Payload", typeof(int), true, "int"),
                ],
                checkConstraints: [check]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.RenameColumnIfExists(dependent
                ? "Value"
                : "Payload",
            "quote_roles",
            "Other");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("quoted-check-rename"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations,
            CancellationToken.None));

        if (!dependent)
        {
            var replay = new MigrationBuilder(context.Database.ProviderName!);
            replay.RenameColumnIfExists("Payload", "quote_roles", "Other");
            await ExecuteOperationsAsync(context, replay.Operations, CancellationToken.None);
        }

        var destination = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.quote_roles') AND name=N'Other';",
            CancellationToken.None);

        // Assert
        Assert.Equal(dependent
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(dependent
                ? 0
                : 1,
            destination);

        if (dependent)
        {
            Assert.Equal("projected_check_source_dependency", report.Assessments[^1].AnalysisCode);
            Assert.Equal(51001,
                Assert.IsType<SqlException>(failure)
                    .Number);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Native dependencies prevent external column rebinding without weakening ownership stamps.</summary>
    /// <param name="filteredIndex">Whether to exercise a filtered index rather than a CHECK.</param>
    /// <param name="disabled">Whether the dependent object is disabled before the external rename attempt.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeDependenciesPreventStaleStampColumnRebinding(
        bool filteredIndex,
        bool disabled
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.binding_owner(Id int NOT NULL, Value int NULL);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var predicate = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("Value"),
            SafeMigrationSqlBinaryOperator.GreaterThan,
            SafeMigrationSql.Literal(0));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (filteredIndex)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition("ix_binding",
                    "binding_owner",
                    [new ExpectedIndexKeyDefinition(column: "Id")],
                    structuredFilter: predicate),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureCheckConstraint(
                ExpectedCheckConstraintDefinition.FromExpression("ck_binding", "binding_owner", predicate),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        if (disabled)
        {
            await ExecuteSqlAsync(connectionString,
                filteredIndex
                    ? "ALTER INDEX ix_binding ON dbo.binding_owner DISABLE;"
                    : "ALTER TABLE dbo.binding_owner NOCHECK CONSTRAINT ck_binding;",
                CancellationToken.None);
        }

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "EXEC sys.sp_rename N'dbo.binding_owner.Value',N'OldValue',N'COLUMN'; "
            + "ALTER TABLE dbo.binding_owner ADD Value int NULL;",
            CancellationToken.None));

        if (disabled)
        {
            await ExecuteSqlAsync(connectionString,
                filteredIndex
                    ? "ALTER INDEX ix_binding ON dbo.binding_owner REBUILD;"
                    : "ALTER TABLE dbo.binding_owner WITH CHECK CHECK CONSTRAINT ck_binding;",
                CancellationToken.None);
        }

        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.binding_owner') AND name=N'Value';",
            CancellationToken.None);

        var destination = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.binding_owner') AND name=N'OldValue';",
            CancellationToken.None);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("native-binding-preserved"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        var nativeFailure = Assert.IsType<SqlException>(failure);
        Assert.Contains(nativeFailure.Errors.Cast<SqlError>(), error => error.Number is 15336 or 5074);
        Assert.Equal(1, source);
        Assert.Equal(0, destination);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);
    }

    /// <summary>Uses the same typed predicate in each native scalar role.</summary>
    private static SafeMigrationSqlExpression ScalarExpression(
        string role
    )
    {
        var predicate = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("Value"),
            SafeMigrationSqlBinaryOperator.GreaterThan,
            SafeMigrationSql.Literal(0));

        return role switch
        {
            "null-test" => SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("Value")),
            "cast" => SafeMigrationSql.Cast(predicate, "int"),
            "coalesce" => SafeMigrationSql.Function("COALESCE", predicate, SafeMigrationSql.Literal(false)),
            "comparison" => SafeMigrationSql.Binary(predicate,
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal(true)),
            "arithmetic" => SafeMigrationSql.Binary(predicate,
                SafeMigrationSqlBinaryOperator.Add,
                SafeMigrationSql.Literal(1)),
            "between" => SafeMigrationSql.Between(predicate,
                SafeMigrationSql.Literal(false),
                SafeMigrationSql.Literal(true)),
            "in" => SafeMigrationSql.In(SafeMigrationSql.Literal(true), [predicate]),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Qualifies the script transport independently from ordinary runtime command generation.</summary>
    private static async Task ApplyScalarOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        bool script,
        CancellationToken cancellationToken
    )
    {
        var options = script
            ? MigrationsSqlGenerationOptions.Script
            : MigrationsSqlGenerationOptions.Default;

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(operations, context.Model, options);

        // WHY: Keep each native command in the generator's indexed execution order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < commands.Count; index++)
        {
            await context.Database.ExecuteSqlRawAsync(commands[index].CommandText, cancellationToken);
        }
    }
}
