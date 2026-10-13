namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies original scope admission through the real handler's fused prepared-setup paths.</summary>
public sealed class MySqlPreparedSetupFusionTests
{
    private const string PrepareSql = "PREPARE doka_sm_statement FROM @doka_sm_sql;";
    private const string ExecuteSql = "EXECUTE doka_sm_statement;";
    private const string DeallocateSql = "DEALLOCATE PREPARE doka_sm_statement;";
    private const string ProviderCleanup = "DO 0; /* provider cleanup */\n";
    private const string NativeModeSnapshot = "/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */;\n";
    private const string NativeModeMutation = "/*! SET SESSION sql_mode = IF("
        + "FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode), @@SESSION.sql_mode, "
        + "CONCAT_WS(',', NULLIF(@@SESSION.sql_mode, ''), 'NO_BACKSLASH_ESCAPES')) */;\n";
    private const string NativeModeRestore = "/*! SET SESSION sql_mode = @__doka_previous_sql_mode */;\n";

    /// <summary>Unreached setup cannot restore a stale snapshot; acquisition records a fresh one atomically.</summary>
    [Fact]
    public void CertifiedNativeModeCleanupOwnsOnlyReachedFreshSnapshot()
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operationContext = CreateOperationContext(context, handler, CreateOperation("catalog"), _ =>
        [
            MySqlMigrationCommandSpec.CreateScoped([NativeModeSnapshot, NativeModeMutation], "DO 7;",
                [NativeModeRestore]),
        ]);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        var cleanup = Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup);
        var setupText = string.Concat(setup);
        var reset = setupText.IndexOf("SET @doka_sm_provider_setup_entered = 0;", StringComparison.Ordinal);
        var decision = setupText.IndexOf("SELECT 1, 2, 2, 2, 2 WHERE @doka_sm_action", StringComparison.Ordinal);
        var capture = setupText.IndexOf("/*! SET @__doka_previous_sql_mode", StringComparison.Ordinal);

        Assert.Equal(0, reset);
        Assert.StartsWith("SET @doka_sm_provider_setup_entered = 0;"
            + "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;", setupText, StringComparison.Ordinal);
        Assert.True(decision > reset);
        Assert.True(capture > decision);
        Assert.Contains("/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode, "
            + "@doka_sm_provider_setup_entered = 1 */;\n", setup);
        Assert.Contains(NativeModeMutation, setup);
        Assert.Equal(1, Count(setupText, "@doka_sm_provider_setup_entered = 1"));
        Assert.Equal(3, cleanup.Length);
        Assert.Equal("/*! SET SESSION sql_mode = IF(COALESCE(@doka_sm_provider_setup_entered = 1, FALSE), "
            + "@__doka_previous_sql_mode, @@SESSION.sql_mode) */;\n", cleanup[1]);
        Assert.Contains("SET @doka_sm_provider_setup_entered = NULL;", cleanup[2], StringComparison.Ordinal);
        Assert.True(cleanup[2].IndexOf("@doka_sm_provider_setup_entered = NULL", StringComparison.Ordinal)
            < cleanup[2].IndexOf("DROP TEMPORARY TABLE", StringComparison.Ordinal));
    }

    /// <summary>Unknown acquisition, mutation or restoration cannot inherit the native cleanup certificate.</summary>
    /// <param name="invalidShape">The independently violated exact native scope invariant.</param>
    [Theory]
    [InlineData("stale_snapshot")]
    [InlineData("snapshot_statement")]
    [InlineData("missing_snapshot")]
    [InlineData("duplicate_snapshot")]
    [InlineData("different_mutation")]
    [InlineData("mutation_statement")]
    [InlineData("different_restore")]
    [InlineData("restore_statement")]
    [InlineData("nonmutating_cleanup_stateful_setup")]
    public void UncertifiedStatefulNativeCleanupIsRejected(string invalidShape)
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        string[] setup = invalidShape switch
        {
            "stale_snapshot" => [NativeModeSnapshot.Replace("@@SESSION.sql_mode", "@stale_mode",
                StringComparison.Ordinal), NativeModeMutation],
            "snapshot_statement" => [NativeModeSnapshot + "DO 0;", NativeModeMutation],
            "missing_snapshot" => [NativeModeMutation],
            "duplicate_snapshot" => [NativeModeSnapshot, NativeModeSnapshot, NativeModeMutation],
            "different_mutation" => [NativeModeSnapshot, "SET SESSION sql_mode = 'ANSI_QUOTES';"],
            "mutation_statement" => [NativeModeSnapshot, NativeModeMutation + "DO 0;"],
            _ => [NativeModeSnapshot, NativeModeMutation],
        };

        var cleanup = invalidShape switch
        {
            "different_restore" => NativeModeRestore.Replace("@__doka_previous_sql_mode", "@stale_mode",
                StringComparison.Ordinal),
            "restore_statement" => NativeModeRestore + "SET SESSION sql_mode = '';",
            "nonmutating_cleanup_stateful_setup" => "DO 0;",
            _ => NativeModeRestore,
        };

        var operationContext = CreateOperationContext(context, handler, CreateOperation("catalog"), _ =>
        [
            MySqlMigrationCommandSpec.CreateScoped(setup, "DO 7;", [cleanup]),
        ]);

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        var invalidScope = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("cleanup acquisition", invalidScope.Message, StringComparison.Ordinal);
    }

    /// <summary>The public provider rejects missing setup before native fragments can reach the handler.</summary>
    [Fact]
    public void PublicNativeScopeRejectsEmptySetup()
    {
        // Arrange
        const string body = "DO 7;";

        // Act
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped([], body, [NativeModeRestore]));

        // Assert
        var invalidCollection = Assert.IsType<ArgumentException>(exception);

        Assert.Equal("setupCommands", invalidCollection.ParamName);
        Assert.Contains("non-empty SQL command collections", invalidCollection.Message, StringComparison.Ordinal);
    }

    /// <summary>Only literal non-mutating cleanup retains the original opaque fixture boundary.</summary>
    /// <param name="cleanupSql">The exact admitted or refused cleanup fragment.</param>
    /// <param name="admitted">Whether the fragment is only one literal DO statement with ordinary comments.</param>
    [Theory]
    [InlineData("DO 0; /* provider cleanup */\n", true)]
    [InlineData("DO 12; /* provider cleanup */", true)]
    [InlineData("do 12; -- provider cleanup\n", true)]
    [InlineData("DO 12; # provider cleanup\n", true)]
    [InlineData("DO 0; /* M! SET SESSION sql_mode = '' */", true)]
    [InlineData("DO 0; /* m!100100 SET SESSION sql_mode = '' */", true)]
    [InlineData("DO 0; SET SESSION sql_mode = '';", false)]
    [InlineData("DO 0; /*! SET SESSION sql_mode = '' */;", false)]
    [InlineData("DO 0; /*M! SET SESSION sql_mode = '' */", false)]
    [InlineData("DO 0; /*m! SET SESSION sql_mode = '' */", false)]
    [InlineData("DO 0; /*M!100100 SET SESSION sql_mode = '' */", false)]
    [InlineData("DO 0; /*m!100100 SET SESSION sql_mode = '' */", false)]
    [InlineData("DO 0; /*+ hint */", false)]
    [InlineData("DO SLEEP(1);", false)]
    [InlineData("DO @value := 1;", false)]
    [InlineData("DO 0; /* unfinished", false)]
    [InlineData("DO 0; --not-a-comment\nSET SESSION sql_mode = '';", false)]
    public void NonMutatingCleanupCertificateRejectsExecutableSuffixes(string cleanupSql, bool admitted)
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var operationContext = CreateOperationContext(context, handler, CreateOperation("catalog"), _ =>
        [
            MySqlMigrationCommandSpec.CreateScoped(["DO 11;"], "DO 7;", [cleanupSql]),
        ]);
        string[]? generatedCleanup = null;
        string[]? generatedSetup = null;

        // Act
        var exception = Record.Exception(() =>
        {
            var command = Assert.Single(handler.Generate(operationContext).Commands);
            generatedCleanup = Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup);
            generatedSetup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        });

        // Assert
        if (admitted)
        {
            Assert.Null(exception);
            Assert.Contains(cleanupSql, Assert.IsType<string[]>(generatedCleanup));
            Assert.DoesNotContain("@doka_sm_provider_setup_entered",
                string.Concat(Assert.IsType<string[]>(generatedSetup)),
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("cleanup acquisition", Assert.IsType<InvalidOperationException>(exception).Message,
                StringComparison.Ordinal);
        }
    }

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
        Assert.DoesNotContain("@doka_sm_provider_setup_entered", string.Concat(firstCleanup), StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_provider_setup_entered", string.Concat(requiredCleanup),
            StringComparison.Ordinal);
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
    /// <param name="scenario">The generic catalog, state, guard or length-probe path being admitted.</param>
    private static SafeMigrationOperation CreateOperation(
        string scenario
    )
    {
        // WHY: These tests admit opaque provider bodies, not atomic ADD
        // clauses. Declared server-side generation retains the generic
        // state/guard paths without pretending DO fixtures are native ALTERs.
        var definition = scenario switch
        {
            "catalog" or "qualified" => new ExpectedColumnDefinition(
                "value", typeof(int), isNullable: true, storeType: "int"),
            "state" or "guard" => CreateServerGeneratedRequiredColumn(),
            "data" => new ExpectedColumnDefinition(
                "value", typeof(string), isNullable: true, storeType: "varchar(80)", maxLength: 80),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var policy = scenario is "guard" or "data" or "qualified"
            ? SafeMigrationPolicy.RepairIfSafe
            : SafeMigrationPolicy.ThrowIfDifferent;

        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition));

        return new SafeMigrationOperation(
            new EnsureColumnIntent("items", definition, schema: scenario == "qualified" ? "application" : null),
            policy);
    }

    /// <summary>Captures an ordinary intrinsic identity target outside the atomic no-default ADD protocol.</summary>
    private static ExpectedColumnDefinition CreateServerGeneratedRequiredColumn()
    {
        var operation = new AddColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
        };

        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.AutoIncrement;

        return SafeMigrationExpectedDefinitionFactory.From(operation);
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
