namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Proves required physical columns exclude inline row SQL for matching and facet-only drift.</summary>
    /// <param name="varchar">Whether the target uses varchar rather than datetime(6).</param>
    /// <param name="drift">Whether the comment and default need repair.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullProofEligibility_RequiredPhysicalColumnSkipsInlineRows(
        bool varchar,
        bool drift
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = varchar ? "varchar(800)" : "datetime(6)";
        var value = varchar ? "'preserved'" : "'2026-01-02 03:04:05.123456'";
        var comment = drift ? "legacy" : "canonical";
        var defaultClause = drift ? $" DEFAULT {value}" : string.Empty;
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `eligibility_rows` (`id` int PRIMARY KEY, `value` {storeType} NOT NULL{defaultClause} "
            + $"COMMENT '{comment}') ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `eligibility_rows` VALUES (1, {value});");

        await using var context = CreateContext(connectionString);
        var operation = BuildMySqlNullEligibilityOperation(varchar);
        var plan = CaptureMySqlNullEligibilityPlan(context, operation);

        // Act
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var inlineState = await ExecuteMySqlInlineNullEligibilitySentinelAsync(context, plan);
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var preservedRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `eligibility_rows` WHERE `id` = 1 AND `value` = {value};");
        var canonicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'eligibility_rows' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO' "
            + "AND COLUMN_COMMENT = 'canonical';");

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        Assert.Equal(drift ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Equal(drift ? "different" : "matching", inlineState);
        Assert.Null(runtimeException);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay).ObservedState);
        Assert.Equal(1, preservedRows);
        Assert.Equal(1, canonicalColumns);
    }

    /// <summary>Retains inline evidence for genuinely nullable columns and rejects existing NULL data.</summary>
    /// <param name="varchar">Whether the physical column uses varchar rather than datetime(6).</param>
    /// <param name="containsNull">Whether existing data must reject the repair.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullProofEligibility_NullablePhysicalColumnRequiresInlineRows(
        bool varchar,
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = varchar ? "varchar(800)" : "datetime(6)";
        var value = containsNull ? "NULL" : varchar ? "'preserved'" : "'2026-01-02 03:04:05.123456'";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `eligibility_rows` (`id` int PRIMARY KEY, `value` {storeType} NULL COMMENT 'canonical') "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `eligibility_rows` VALUES (1, {value});");

        await using var context = CreateContext(connectionString);
        var operation = BuildMySqlNullEligibilityOperation(varchar);
        var plan = CaptureMySqlNullEligibilityPlan(context, operation);

        // Act
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var sentinelException = await Record.ExceptionAsync(() =>
            ExecuteMySqlInlineNullEligibilitySentinelAsync(context, plan));
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `eligibility_rows` WHERE `id` = 1 AND `value` "
            + (containsNull ? "IS NULL;" : $"= {value};"));

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Equal(1242, Assert.IsType<MySqlException>(sentinelException).Number);
        if (containsNull)
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(runtimeException).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(runtimeException);
        }

        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(replay).ObservedState);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Does not retain a successful datetime NULL proof across operations or live analysis calls.</summary>
    [Fact]
    public async Task NullProofEligibility_DatetimeRowsRecheckedAfterPreflightDml()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `eligibility_rows` (`id` int PRIMARY KEY, `value` datetime(6) NULL COMMENT 'canonical'); "
            + "INSERT INTO `eligibility_rows` VALUES (1, '2026-01-02 03:04:05.123456');");

        await using var context = CreateContext(connectionString);
        var operation = BuildMySqlNullEligibilityOperation(varchar: false);

        // Act
        var preflight = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "INSERT INTO `eligibility_rows` VALUES (2, NULL);");
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var repeatedAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var insertedNullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `eligibility_rows` WHERE `id` = 2 AND `value` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(preflight).ObservedState);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(runtimeException).Message,
            StringComparison.Ordinal);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(repeatedAnalysis).ObservedState);
        Assert.Equal(1, insertedNullRows);
    }

    /// <summary>Captures exactly the provider-generated physical proof predicates for one bounded operation.</summary>
    private static MySqlSafeMigrationRuntimePlan CaptureMySqlNullEligibilityPlan(
        DbContext context,
        SafeMigrationOperation operation
    )
    {
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation]);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return Assert.Single(lease.Complete());
    }

    /// <summary>Creates a generic required varchar(800) or datetime(6) contract.</summary>
    private static SafeMigrationOperation BuildMySqlNullEligibilityOperation(
        bool varchar
    ) => new(new EnsureColumnIntent("eligibility_rows", new ExpectedColumnDefinition(
        "value", varchar ? typeof(string) : typeof(DateTime), false,
        storeType: varchar ? "varchar(800)" : "datetime(6)", maxLength: varchar ? 800 : null,
        comment: "canonical")), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Uses a cardinality-error sentinel to prove the CASE branch is not evaluated.</summary>
    private static async Task<string> ExecuteMySqlInlineNullEligibilitySentinelAsync(
        DbContext context,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        // WHY: The volatile data error replaces only the row proof. Unlike
        // WHERE value IS NULL on a NOT NULL column, this cannot be optimized
        // away by the engine's knowledge of the column's declared nullability.
        var sentinelPlan = plan with
        {
            NullabilityDataProbe = plan.NullabilityDataProbe! with
            {
                BlockedExpression = "(SELECT marker FROM (SELECT 1 marker UNION ALL SELECT 2) null_sentinel)",
            },
        };

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());
        command.CommandText = "SELECT (" + sentinelPlan.RenderStateExpression(parameterizer.Add, false, false)
            + "), (" + sentinelPlan.RenderRepairPrecondition(parameterizer.Add, false, false) + ");";

        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("The inline classifier returned no state.");
    }
}
