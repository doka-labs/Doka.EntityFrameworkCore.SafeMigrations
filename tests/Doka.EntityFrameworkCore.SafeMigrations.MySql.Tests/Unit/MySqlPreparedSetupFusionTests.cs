namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies original scope admission through the real handler's fused prepared-setup paths.</summary>
public sealed class MySqlPreparedSetupFusionTests
{
    private const string PrepareSql = "PREPARE doka_sm_statement FROM @doka_sm_sql;";
    private const string ExecuteSql = "EXECUTE doka_sm_statement;";
    private const string DeallocateSql = "DEALLOCATE PREPARE doka_sm_statement;";
    private const string ProviderCleanup = "DO 0; /* provider cleanup */\n";

    /// <summary>Exactly 128 original fragments remain admissible without grouping opaque provider setup.</summary>
    /// <param name="scenario">The independent catalog, state, guard or data-probe generation path.</param>
    /// <param name="providerSetupCount">The opaque setup count completing the original 128-fragment scope.</param>
    /// <param name="evaluations">The prepared lazy evaluations preceding baseline preparation.</param>
    /// <param name="transactionSuppressed">The provider's transaction-suppression contract.</param>
    [Theory]
    [InlineData("catalog", 118, 0, false)]
    [InlineData("catalog", 118, 0, true)]
    [InlineData("state", 113, 1, false)]
    [InlineData("state", 113, 1, true)]
    [InlineData("guard", 104, 3, false)]
    [InlineData("guard", 104, 3, true)]
    [InlineData("data", 103, 3, false)]
    [InlineData("data", 103, 3, true)]
    [InlineData("qualified", 112, 1, false)]
    [InlineData("qualified", 112, 1, true)]
    public void ExactOriginalFragmentLimitIsAcceptedByHandler(
        string scenario,
        int providerSetupCount,
        int evaluations,
        bool transactionSuppressed
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operation = CreateOperation(scenario);
        var providerSetup = CreateProviderSetup(providerSetupCount);
        MySqlMigrationCommandSpec[] apply =
        [
            MySqlMigrationCommandSpec.CreateScoped(
                providerSetup, "DO 7;\n", [ProviderCleanup], transactionSuppressed),
        ];

        MySqlMigrationCommandSpec[] repair =
        [
            MySqlMigrationCommandSpec.CreateScoped(
                providerSetup, "DO 9;\n", [ProviderCleanup], transactionSuppressed),
        ];

        var operationContext = CreateOperationContext(
            context, handler, operation, standard => standard is AlterColumnOperation ? repair : apply);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        var body = Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Body));
        var cleanup = Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup);
        Assert.Equal(128, providerSetupCount + OriginalSingleBaselineOverhead(scenario));
        Assert.True(command.Fragments.Count < 128);
        Assert.True(command.Fragments.Count + 1 <= 128);
        Assert.Equal("safe_guarded_operation", result.OutcomeCode);
        Assert.Equal(transactionSuppressed, command.TransactionSuppressed);
        Assert.Equal(providerSetup, setup.Where(text => text.StartsWith("DO 0; /* provider setup ",
            StringComparison.Ordinal)));
        Assert.Equal(3, cleanup.Length);
        Assert.Equal(ProviderCleanup, cleanup[1]);
        Assert.StartsWith("PREPARE doka_sm_statement FROM 'DO 0';", cleanup[0], StringComparison.Ordinal);
        Assert.Contains("DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;", cleanup[2], StringComparison.Ordinal);
        Assert.StartsWith(ExecuteSql + "\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PrepareSql, body, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_post_ok", body, StringComparison.Ordinal);
        AssertPreparedSetup(setup, evaluations, baselinePreparedInSetup: true, hasDataProbe: scenario == "data");
        Assert.Contains("CONVERT(0x" + Hex("DO 7") + " USING utf8mb4)", string.Concat(setup),
            StringComparison.Ordinal);

        if (operation.Policy == SafeMigrationPolicy.RepairIfSafe)
        {
            Assert.Contains("CONVERT(0x" + Hex("DO 9") + " USING utf8mb4)", string.Concat(setup),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A 129-fragment original scope is rejected even when its fused physical shape fits the provider.
    /// </summary>
    /// <param name="scenario">The independent catalog, state, guard or data-probe generation path.</param>
    /// <param name="providerSetupCount">One more opaque setup fragment than the accepted original boundary.</param>
    [Theory]
    [InlineData("catalog", 119)]
    [InlineData("state", 114)]
    [InlineData("guard", 105)]
    [InlineData("data", 104)]
    [InlineData("qualified", 113)]
    public void OriginalFragmentOverflowIsRejectedByHandler(
        string scenario,
        int providerSetupCount
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operation = CreateOperation(scenario);
        var providerSetup = CreateProviderSetup(providerSetupCount);
        MySqlMigrationCommandSpec[] baseline =
        [
            MySqlMigrationCommandSpec.CreateScoped(providerSetup, "DO 7;", [ProviderCleanup]),
        ];

        var operationContext = CreateOperationContext(context, handler, operation, _ => baseline);

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        Assert.Equal(129, providerSetupCount + OriginalSingleBaselineOverhead(scenario));
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.Equal("setupCommands", argumentException.ParamName);
        Assert.Contains("original MySQL migration scope", argumentException.Message, StringComparison.Ordinal);
    }

    /// <summary>Opaque provider sequences keep each baseline preparation in the body, not the fused setup.</summary>
    /// <param name="scenario">The independent catalog, state, guard or data-probe generation path.</param>
    /// <param name="evaluations">The prepared lazy evaluations emitted before the provider sequence.</param>
    /// <param name="originalFragments">The original outer scope count without provider cleanup or setup.</param>
    [Theory]
    [InlineData("catalog", 0, 7)]
    [InlineData("state", 1, 12)]
    [InlineData("guard", 3, 21)]
    [InlineData("data", 3, 22)]
    [InlineData("qualified", 1, 13)]
    public void MultipleProviderBaselinesRetainCommandLocalPreparation(
        string scenario,
        int evaluations,
        int originalFragments
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operation = CreateOperation(scenario);
        MySqlMigrationCommandSpec[] apply =
        [
            MySqlMigrationCommandSpec.Create("DO 7;\n", transactionSuppressed: true),
            MySqlMigrationCommandSpec.Create("DO 8;\n", transactionSuppressed: true),
        ];

        MySqlMigrationCommandSpec[] repair =
        [
            MySqlMigrationCommandSpec.Create("DO 9;\n", transactionSuppressed: true),
            MySqlMigrationCommandSpec.Create("DO 10;\n", transactionSuppressed: true),
        ];

        var operationContext = CreateOperationContext(
            context, handler, operation, standard => standard is AlterColumnOperation ? repair : apply);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        var setupSql = string.Concat(setup);
        var body = Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Body));
        Assert.True(command.TransactionSuppressed);
        Assert.Equal(2, Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup).Length);
        Assert.Equal(originalFragments, OriginalSingleBaselineOverhead(scenario) - 3);
        Assert.True(command.Fragments.Count <= originalFragments - (evaluations * 3));
        AssertPreparedSetup(setup, evaluations, baselinePreparedInSetup: false, hasDataProbe: scenario == "data");
        Assert.DoesNotContain("WHEN @doka_sm_action = 'apply'", setupSql, StringComparison.Ordinal);
        Assert.Equal(2, Count(body, PrepareSql));
        Assert.Equal(2, Count(body, ExecuteSql));
        Assert.Equal(2, Count(body, DeallocateSql));
        Assert.Contains("CONVERT(0x" + Hex("DO 7") + " USING utf8mb4)", body, StringComparison.Ordinal);
        Assert.Contains("CONVERT(0x" + Hex("DO 8") + " USING utf8mb4)", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf(Hex("DO 7"), StringComparison.Ordinal)
            < body.IndexOf(Hex("DO 8"), StringComparison.Ordinal));
        Assert.True(body.LastIndexOf(DeallocateSql, StringComparison.Ordinal)
            < body.IndexOf("SET @doka_sm_post_ok", StringComparison.Ordinal));

        if (operation.Policy == SafeMigrationPolicy.RepairIfSafe)
        {
            Assert.Contains("CONVERT(0x" + Hex("DO 9") + " USING utf8mb4)", body, StringComparison.Ordinal);
            Assert.Contains("CONVERT(0x" + Hex("DO 10") + " USING utf8mb4)", body, StringComparison.Ordinal);
        }
    }

    /// <summary>A qualified nullable column retains exact matching even when neither row proof is needed.</summary>
    [Fact]
    public void QualifiedNullableRepairMatchesBeforeStateEvaluationWithoutRowProbes()
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operation = CreateOperation("qualified");
        MySqlMigrationCommandSpec[] baseline = [MySqlMigrationCommandSpec.Create("DO 7;")];
        var operationContext = CreateOperationContext(context, handler, operation, _ => baseline);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        var setupSql = string.Concat(setup);
        const string matchingPrefix = "SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN CASE";
        var evaluation = Assert.Single(setup, text => text.Contains(matchingPrefix, StringComparison.Ordinal));
        Assert.Contains("SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x",
            evaluation, StringComparison.Ordinal);
        Assert.Contains("SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different'",
            evaluation, StringComparison.Ordinal);
        Assert.DoesNotContain("SET @doka_sm_data_probe_required", setupSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SET @doka_sm_column_repair_eligible", setupSql, StringComparison.Ordinal);
        Assert.True(setupSql.IndexOf("ELSE 'unsupported' END;", StringComparison.Ordinal)
            < setupSql.IndexOf(matchingPrefix, StringComparison.Ordinal));
        Assert.True(setupSql.IndexOf("WHEN NOT @doka_sm_prerequisite_ok THEN 'prerequisite_missing'",
                StringComparison.Ordinal)
            < setupSql.IndexOf(matchingPrefix, StringComparison.Ordinal));
        AssertPreparedSetup(setup, evaluations: 1, baselinePreparedInSetup: true, hasDataProbe: false);
    }

    /// <summary>Prepared controls split across dispatches cannot satisfy the contiguous-fusion assertion.</summary>
    [Fact]
    public void SplitPreparedControlsAreRejectedByFusionAssertion()
    {
        // Arrange
        string[] setup = [PrepareSql, ExecuteSql, DeallocateSql];

        // Act
        var exception = Record.Exception(() =>
            AssertPreparedSetup(setup, evaluations: 1, baselinePreparedInSetup: false, hasDataProbe: false));

        // Assert
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(exception);
    }

    /// <summary>Provider snapshots cannot mutate shared cleanup templates or leak proof-specific cleanup.</summary>
    [Fact]
    public void CleanupTemplatesRemainIsolatedAcrossProofShapes()
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        MySqlMigrationCommandSpec[] baseline = [MySqlMigrationCommandSpec.Create("DO 7;")];
        var nullableContext = CreateOperationContext(context, handler, CreateOperation("catalog"), _ => baseline);
        var requiredContext = CreateOperationContext(context, handler, CreateOperation("guard"), _ => baseline);

        // Act
        var first = handler.Generate(nullableContext);
        var required = handler.Generate(requiredContext);
        var repeated = handler.Generate(nullableContext);

        // Assert
        var firstCleanup = Fragments(Assert.Single(first.Commands), MySqlMigrationCommandFragmentKind.Cleanup);
        var requiredCleanup = Fragments(Assert.Single(required.Commands), MySqlMigrationCommandFragmentKind.Cleanup);
        var repeatedCleanup = Fragments(Assert.Single(repeated.Commands), MySqlMigrationCommandFragmentKind.Cleanup);
        Assert.Equal(firstCleanup, repeatedCleanup);
        Assert.Equal(2, firstCleanup.Length);
        Assert.Equal(2, requiredCleanup.Length);
        Assert.StartsWith("PREPARE doka_sm_statement FROM 'DO 0';", firstCleanup[0], StringComparison.Ordinal);
        Assert.EndsWith("DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;",
            firstCleanup[1], StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_nullability_blocked", firstCleanup[1], StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked = NULL", requiredCleanup[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Lists independently counted pre-fusion setup, body and cleanup fragments excluding provider setup.
    /// </summary>
    private static int OriginalSingleBaselineOverhead(
        string scenario
    ) => scenario switch
    {
        // WHY: These are the established unfused scope costs, not counts obtained from the optimized handler.
        "catalog" => 10,
        "state" => 15,
        "guard" => 24,
        "data" => 25,
        "qualified" => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    /// <summary>Creates distinct opaque setup text so any grouping, reordering or dropping is observable.</summary>
    private static string[] CreateProviderSetup(
        int count
    ) => Enumerable.Range(0, count)
        .Select(index => "DO 0; /* provider setup " + index.ToString(CultureInfo.InvariantCulture) + " */\n")
        .ToArray();

    /// <summary>Creates the real planner paths without synthetic runtime plans.</summary>
    private static SafeMigrationOperation CreateOperation(
        string scenario
    )
    {
        var definition = scenario switch
        {
            "catalog" or "qualified" => new ExpectedColumnDefinition(
                "value", typeof(int), isNullable: true, storeType: "int"),
            "state" or "guard" => new ExpectedColumnDefinition(
                "value", typeof(int), isNullable: false, storeType: "int"),
            "data" => new ExpectedColumnDefinition(
                "value", typeof(string), isNullable: true, storeType: "varchar(80)", maxLength: 80),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var policy = scenario is "guard" or "data" or "qualified"
            ? SafeMigrationPolicy.RepairIfSafe
            : SafeMigrationPolicy.ThrowIfDifferent;

        return new SafeMigrationOperation(
            new EnsureColumnIntent("items", definition, schema: scenario == "qualified" ? "application" : null),
            policy);
    }

    /// <summary>Creates the real provider context while replacing only bounded standard-renderer output.</summary>
    private static MySqlMigrationOperationContext CreateOperationContext(
        DbContext context,
        MySqlSafeMigrationOperationHandler handler,
        SafeMigrationOperation operation,
        Func<MigrationOperation, IReadOnlyList<MySqlMigrationCommandSpec>> standardRenderer
    )
    {
        // WHY: Doka 10.4.2 has no public context or feature-set constructor. Reflection invokes its real
        // constructors and canonical version profile; an uninitialized feature stub would bypass capability guards.
        var serverVersion = MySqlServerVersion.MySql(new Version(8, 4, 11));
        var profileProperty = typeof(MySqlServerVersion).GetProperty("Profile",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The pinned provider no longer exposes its canonical profile.");

        var profile = profileProperty.GetValue(serverVersion)
            ?? throw new InvalidOperationException("The configured provider profile is missing.");

        var featureConstructor = Assert.Single(typeof(MySqlMigrationFeatureSet)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        var features = Assert.IsType<MySqlMigrationFeatureSet>(featureConstructor.Invoke([profile]));
        var contextConstructor = Assert.Single(typeof(MySqlMigrationOperationContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        return Assert.IsType<MySqlMigrationOperationContext>(contextConstructor.Invoke(
        [
            operation,
            context.Model,
            MigrationsSqlGenerationOptions.Default,
            serverVersion,
            features,
            0,
            handler.HandlerId,
            standardRenderer,
        ]));
    }

    /// <summary>Creates the production handler with real provider service dependencies.</summary>
    private static MySqlSafeMigrationOperationHandler CreateHandler(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>(),
        context.GetService<MySqlSafeMigrationPlanCapture>(),
        context.GetService<IDesignTimeModel>());

    /// <summary>Creates an isolated real provider service scope without opening a connection.</summary>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder()
            .UseMySql(
                "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }

    /// <summary>Reads exact fragment payloads from the actual provider-validated handler result.</summary>
    private static string[] Fragments(
        MySqlMigrationCommandSpec command,
        MySqlMigrationCommandFragmentKind kind
    ) => command.Fragments.Where(fragment => fragment.Kind == kind)
        .Select(fragment => fragment.CommandText.ToString())
        .ToArray();

    /// <summary>Checks fused dispatches retain every immediate prepared control in its original order.</summary>
    private static void AssertPreparedSetup(
        IReadOnlyList<string> setup,
        int evaluations,
        bool baselinePreparedInSetup,
        bool hasDataProbe
    )
    {
        var setupSql = string.Concat(setup);
        Assert.Equal(evaluations + (baselinePreparedInSetup ? 1 : 0), Count(setupSql, PrepareSql));
        Assert.Equal(evaluations, Count(setupSql, ExecuteSql));
        Assert.Equal(evaluations, Count(setupSql, DeallocateSql));
        // WHY: A fused fragment may contain later assignments or multiple evaluation triples.
        // Count only complete triples within one fragment; concatenating first would hide a split dispatch.
        Assert.Equal(evaluations, setup.Sum(text => Count(text, PrepareSql + ExecuteSql + DeallocateSql)));
        Assert.Equal(hasDataProbe, setupSql.Contains("SET @doka_sm_data_probe_required", StringComparison.Ordinal));
    }

    /// <summary>Encodes expected normalized provider bodies independently of the handler.</summary>
    private static string Hex(
        string sql
    ) => Convert.ToHexString(Encoding.UTF8.GetBytes(sql));

    /// <summary>Counts exact prepared controls without interpreting literal SQL payloads.</summary>
    private static int Count(
        string sql,
        string fragment
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = sql.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
