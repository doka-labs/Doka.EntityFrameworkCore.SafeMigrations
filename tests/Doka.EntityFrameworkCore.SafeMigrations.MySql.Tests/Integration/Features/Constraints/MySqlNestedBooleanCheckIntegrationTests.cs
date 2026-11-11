namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies native nested CHECK displays through table, constraint and computed-column consumers.</summary>
public sealed class MySqlNestedBooleanCheckIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const string Table = "nested_check_scope";

    /// <summary>Uses the assembly-owned engine and a separately leased database per test.</summary>
    /// <param name="fixture">The configured native MySQL or MariaDB engine.</param>
    public MySqlNestedBooleanCheckIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Retains independent native DDL rather than deriving catalog expectations from the renderer.</summary>
    private static string NativeTableSql => $"CREATE TABLE `{Table}` ("
        + "`S0` bigint NOT NULL PRIMARY KEY, `ActiveGeneration` bigint unsigned NULL, "
        + "`ActiveProof` tinyint unsigned NULL, `ActiveShape` binary(32) NULL, "
        + "`NextGeneration` bigint unsigned NOT NULL, `TargetGeneration` bigint unsigned NULL, "
        + "`TargetShape` binary(32) NULL, CONSTRAINT `ck_active` CHECK ("
        + MySqlNestedBooleanCatalogCases.ActiveSource
        + "), CONSTRAINT `ck_target` CHECK ("
        + MySqlNestedBooleanCatalogCases.TargetSource
        + ")) ENGINE=InnoDB";

    /// <summary>Checks table creation, populated replay and individual CHECKs against the actual catalog.</summary>
    /// <param name="script">Whether to execute the generated script rather than provider commands.</param>
    /// <param name="safeFirst">Whether SafeMigrations is registered before the native provider.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StrictTableCreationAndPopulatedReplayPreserveNativeChecks(
        bool script,
        bool safeFirst
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateRegisteredContext(connectionString, safeFirst);
        var builder = CreateTable(context);
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model, MigrationsSqlGenerationOptions.Script);

        var sql = string.Join(Environment.NewLine, commands.Select(static command => command.CommandText));
        var checks = new MigrationBuilder(context.Database.ProviderName!);
        checks.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_active",
                Table,
                MySqlNestedBooleanCatalogCases.Create(target: false, rightAssociated: true)),
            SafeMigrationPolicy.ThrowIfDifferent);

        checks.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_target",
                Table,
                MySqlNestedBooleanCatalogCases.Create(target: true, rightAssociated: false)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            await ApplyAsync(context, connectionString, builder.Operations, sql, script, CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                $"INSERT INTO `{Table}` (`S0`, `NextGeneration`) VALUES (42, 1);",
                CancellationToken.None);

            await ApplyAsync(context, connectionString, builder.Operations, sql, script, CancellationToken.None);
        });

        var native = await NativeClauseAsync(connectionString, "ck_active", CancellationToken.None);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                checks.Operations,
                new SafeMigrationRunOptions("nested-native-checks"),
                CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{Table}` WHERE `S0`=42;",
            CancellationToken.None);

        // Assert
        Assert.True(failure is null, $"{failure}; native={native}");
        Assert.False(string.IsNullOrWhiteSpace(native));
        if (!Fixture.IsMariaDb)
        {
            Assert.Equal(MySqlNestedBooleanCatalogCases.ActiveTuple, native);
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching,
                assessment.ObservedState));

        Assert.Equal(1, rows);
    }

    /// <summary>A different literal, column or Boolean grouping must remain blocked on populated data.</summary>
    /// <param name="drift">The semantic difference introduced into the expected contract.</param>
    [Theory]
    [InlineData("literal")]
    [InlineData("identifier")]
    [InlineData("group")]
    public async Task DifferentNestedCheckRemainsBlockedWithoutMutation(
        string drift
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateRegisteredContext(connectionString, safeFirst: false);
        await ExecuteSqlAsync(connectionString,
            NativeTableSql
            + "; "
            + $"INSERT INTO `{Table}` (`S0`, `NextGeneration`) VALUES (42, 1);",
            CancellationToken.None);

        var before = await NativeClauseAsync(connectionString, "ck_active", CancellationToken.None);
        var expectedSql = drift switch
        {
            "literal" => MySqlNestedBooleanCatalogCases.ActiveTuple.Replace("= 1", "= 2", StringComparison.Ordinal),
            "identifier" => MySqlNestedBooleanCatalogCases.ActiveTuple.Replace("`ActiveShape`",
                "`TargetShape`",
                StringComparison.Ordinal),
            "group" => MySqlNestedBooleanCatalogCases.ActiveTuple.Replace(" or ", " and ", StringComparison.Ordinal),
            _ => throw new UnreachableException(),
        };

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_active",
            Table,
            expectedSql,
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("nested-native-drift"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations,
            CancellationToken.None));

        var after = await NativeClauseAsync(connectionString, "ck_active", CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{Table}` WHERE `S0`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        AssertOwnedGuardFailure(failure);
        Assert.Equal(before, after);
        Assert.Equal(1, rows);
    }

    /// <summary>Recognizes nested OR chains without discarding required AND-group boundaries.</summary>
    /// <param name="different">Whether the existing clause omits required OR-group parentheses.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedOrChainsRetainRequiredGroupBoundaries(
        bool different
    )
    {
        // Arrange
        const string expected = "(`a` IS NULL OR `b` IS NULL OR `c` IS NULL) AND (`d` IS NULL OR `e` IS NULL)";
        var actual = different
            ? "`a` IS NULL OR `b` IS NULL OR `c` IS NULL AND `d` IS NULL OR `e` IS NULL"
            : expected;

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `or_groups` (`id` int NOT NULL PRIMARY KEY, `a` int NULL, `b` int NULL, "
            + $"`c` int NULL, `d` int NULL, `e` int NULL, CONSTRAINT `ck_or_groups` CHECK ({actual})) ENGINE=InnoDB; "
            + (different
                ? "INSERT INTO `or_groups` VALUES (42, NULL, 1, 1, 1, 1);"
                : "INSERT INTO `or_groups` VALUES (42, NULL, NULL, NULL, NULL, NULL);"),
            CancellationToken.None);

        await using var context = CreateRegisteredContext(connectionString, safeFirst: false);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_or_groups",
            "or_groups",
            expected,
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("nested-or-grouping"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations,
            CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `or_groups` WHERE `id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(different
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(different
                ? SafeMigrationObservedState.Different
                : SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        if (different)
        {
            AssertOwnedGuardFailure(failure);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Computed columns reuse the same native spelling without changing their values on replay.</summary>
    /// <param name="target">Whether the generated predicate uses the target tuple.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputedNestedBooleanExpressionMatchesCatalogAndReplay(
        bool target
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            NativeTableSql
            + "; "
            + $"INSERT INTO `{Table}` (`S0`, `NextGeneration`) VALUES (42, 1);",
            CancellationToken.None);

        await using var context = CreateRegisteredContext(connectionString, safeFirst: true);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table,
            new ExpectedColumnDefinition("tuple_matches",
                typeof(int),
                isNullable: true,
                storeType: "int",
                computedExpression: MySqlNestedBooleanCatalogCases.Create(target, rightAssociated: true),
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("nested-generated-replay"),
                CancellationToken.None);

        var value = await ScalarIntAsync(connectionString,
            $"SELECT `tuple_matches` FROM `{Table}` WHERE `S0`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(1, value);
    }

    /// <summary>Builds the same seven-column captured ordinary-EF table contract as the native reproduction.</summary>
    private static MigrationBuilder CreateTable(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(Table,
            columns => new
            {
                S0 = columns.Column<long>(type: "bigint", nullable: false),
                ActiveGeneration = columns.Column<ulong>(type: "bigint unsigned", nullable: true),
                ActiveProof = columns.Column<byte>(type: "tinyint unsigned", nullable: true),
                ActiveShape =
                    columns.Column<byte[]>(type: "binary(32)", maxLength: 32, fixedLength: true, nullable: true),
                NextGeneration = columns.Column<ulong>(type: "bigint unsigned", nullable: false),
                TargetGeneration = columns.Column<ulong>(type: "bigint unsigned", nullable: true),
                TargetShape =
                    columns.Column<byte[]>(type: "binary(32)", maxLength: 32, fixedLength: true, nullable: true),
            },
            constraints: table =>
            {
                table.Annotation("Doka:MySql:StorageEngine", "InnoDB");
                table.PrimaryKey("pk_nested_check_scope", row => row.S0);
                table.CheckConstraint("ck_active", MySqlNestedBooleanCatalogCases.ActiveSource);
                table.CheckConstraint("ck_target", MySqlNestedBooleanCatalogCases.TargetSource);
            });

        return builder;
    }

    /// <summary>Checks both provider registration orders without changing the leased native engine.</summary>
    private DbContext CreateRegisteredContext(
        string connectionString,
        bool safeFirst
    )
    {
        var options = new DbContextOptionsBuilder();
        if (safeFirst)
        {
            options.UseMySqlSafeMigrations();
        }

        options.UseMySql(connectionString, Fixture.ServerVersion);
        if (!safeFirst)
        {
            options.UseMySqlSafeMigrations();
        }

        return new DbContext(options.Options);
    }

    /// <summary>Reads the engine-produced CHECK clause from the actual owned catalog.</summary>
    private static Task<string> NativeClauseAsync(
        string connectionString,
        string name,
        CancellationToken cancellationToken
    ) => ScalarStringAsync(connectionString,
        $"SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() "
        + $"AND CONSTRAINT_NAME='{name}';",
        cancellationToken);

    /// <summary>Chooses provider commands or generated-script execution with explicit cancellation.</summary>
    private static Task ApplyAsync(
        DbContext context,
        string connectionString,
        IReadOnlyList<MigrationOperation> operations,
        string script,
        bool useScript,
        CancellationToken cancellationToken
    ) => useScript
        ? ExecuteMigrationScriptAsync(connectionString, script, cancellationToken)
        : ExecuteOperationsAsync(context, operations, cancellationToken);
}
