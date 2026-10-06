namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Proves explicit string alterations preserve rows and replay without row probes.</summary>
    /// <param name="sourceType">The exact prior store type.</param>
    /// <param name="targetType">The desired store type.</param>
    /// <param name="requiresLengthProof">Whether live values must fit a smaller character domain.</param>
    [Theory]
    [InlineData("varchar(20)", "varchar(5)", true)]
    [InlineData("text", "varchar(5)", true)]
    [InlineData("longtext", "varchar(5)", true)]
    [InlineData("varchar(5)", "varchar(20)", false)]
    [InlineData("varchar(5)", "text", false)]
    [InlineData("text", "mediumtext", false)]
    public async Task AlterTransition_PreservesValuesAndReplaysWithoutDataProbes(
        string sourceType,
        string targetType,
        bool requiresLengthProof
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`id` int PRIMARY KEY, `value` {sourceType} NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES (1, REPEAT(CONVERT(0xF09F9880 USING utf8mb4), 5)), "
            + "(2, 'a   '), (3, NULL);");
        var operation = AlterTransitionOperation(sourceType, targetType);
        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        await using var analysisContext = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        analysisContext.Database.SetDbConnection(connection);
        var analyzer = analysisContext.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var beforeResults = await analyzer.AnalyzeAsync(analysisContext, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var firstVisits = await ReadRuntimeMatchingVisitsAsync(context);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        await ExecuteOperationsAsync(context, [operation]);
        var replayVisits = await ReadRuntimeMatchingVisitsAsync(context);
        var afterResults = await analyzer.AnalyzeAsync(analysisContext, [operation]);
        var values = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(CONCAT(`id`, ':', COALESCE(HEX(`value`), 'null')) ORDER BY `id`) "
            + "FROM `alter_rows`;");

        // Assert
        var before = Assert.Single(beforeResults);
        var after = Assert.Single(afterResults);
        Assert.Equal(SafeMigrationObservedState.Different, before.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, before.RepairCapability);
        Assert.Equal(requiresLengthProof, before.RequiresLiveDataProof);
        Assert.False(before.RepairMutatesData);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, before.OperationalImpact);
        Assert.Contains(before.Differences, difference => difference.Facet == "column_store_type");
        Assert.Equal(requiresLengthProof ? 1 : 0, firstVisits.LengthRows);
        Assert.Equal(0, replayVisits.LengthRows);
        Assert.Equal(0, replayVisits.NullRows);
        Assert.Equal(0, replayVisits.Transition);
        Assert.Equal(requiresLengthProof ? 1 : 0, connection.NarrowingDataStatementCount);
        if (targetType.StartsWith("varchar", StringComparison.Ordinal))
        {
            Assert.Equal(2, observer.RowInstrumentations);
            Assert.Equal(2, observer.MatchingInstrumentations);
        }

        Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
        Assert.False(after.RequiresLiveDataProof);
        Assert.True(after.PostconditionSatisfied);
        Assert.Equal("1:F09F9880F09F9880F09F9880F09F9880F09F9880,2:61202020,3:null", values);
    }

    /// <summary>Rejects overlength data without truncation and emits the narrowing reason.</summary>
    /// <param name="sourceType">The prior varchar or text store type.</param>
    [Theory]
    [InlineData("varchar(20)")]
    [InlineData("text")]
    public async Task AlterTransition_RejectsOverlengthValues(string sourceType)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` {sourceType} NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES ('a     ');");
        await using var context = CreateContext(connectionString);
        var operation = AlterTransitionOperation(sourceType, "varchar(5)");

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `alter_rows` WHERE HEX(`value`) = '612020202020';");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, analysis.ObservedState);
        Assert.Equal("varchar_narrowing_value_too_long", analysis.Code);
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, preserved);
    }

    /// <summary>Preserves exact source, policy, metadata, and missing-column rejection boundaries.</summary>
    /// <param name="blocker">The independent eligibility condition that must fail closed.</param>
    [Theory]
    [InlineData("source")]
    [InlineData("no_source")]
    [InlineData("policy")]
    [InlineData("missing")]
    [InlineData("collation")]
    [InlineData("default_case")]
    [InlineData("structured_default_case")]
    [InlineData("expression_default_case")]
    [InlineData("default_quoted")]
    [InlineData("default_quoted_expected")]
    [InlineData("default_null_text")]
    [InlineData("default_literal_null_text")]
    [InlineData("expression_literal")]
    [InlineData("expression_payload_case")]
    [InlineData("invisible")]
    [InlineData("foreign_key")]
    public async Task AlterTransition_RejectsIneligibleRepairsWithoutRowProbes(string blocker)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var column = blocker == "missing" ? string.Empty : ", `value` varchar(20) NULL"
            + (blocker == "collation" ? " COLLATE utf8mb4_bin" : string.Empty)
            + (blocker is "default_case" or "structured_default_case" ? " DEFAULT 'UPPER'" : string.Empty)
            + (blocker == "expression_default_case" ? " DEFAULT ('UPPER')" : string.Empty)
            + (blocker == "default_quoted" ? " DEFAULT '''upper'''" : string.Empty)
            + (blocker == "default_quoted_expected" ? " DEFAULT 'upper'" : string.Empty)
            + (blocker is "default_null_text" or "default_literal_null_text" ? " DEFAULT 'NULL'" : string.Empty)
            + (blocker == "expression_literal" ? " DEFAULT 'lower(''A'')'" : string.Empty)
            + (blocker == "expression_payload_case" ? " DEFAULT (concat('UPPER'))" : string.Empty)
            + (blocker == "invisible" ? " INVISIBLE" : string.Empty);

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`id` int PRIMARY KEY{column}) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
            + (blocker == "missing" ? string.Empty : "INSERT INTO `alter_rows` (`id`, `value`) VALUES (1, 'kept');"));
        if (blocker == "foreign_key")
        {
            await ExecuteSqlAsync(connectionString,
                "ALTER TABLE `alter_rows` ADD UNIQUE KEY `uq_value` (`value`); "
                + "CREATE TABLE `alter_children` (`value` varchar(20) NULL, "
                + "CONSTRAINT `fk_value` FOREIGN KEY (`value`) REFERENCES `alter_rows` (`value`)) "
                + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        }

        var operation = blocker == "no_source"
            ? new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)")),
                SafeMigrationPolicy.RepairIfSafe)
            : AlterTransitionOperation(blocker == "source" ? "varchar(30)" : "varchar(20)", "varchar(5)",
                blocker == "policy" ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.RepairIfSafe);

        if (blocker is "default_case" or "structured_default_case" or "expression_default_case"
            or "default_quoted" or "default_quoted_expected")
        {
            var sourceDefault = blocker is "structured_default_case" or "expression_default_case"
                ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("upper"))
                : SafeMigrationDefaultValue.Literal(blocker == "default_quoted_expected" ? "'upper'" : "upper");

            operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)"),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)",
                    defaultValue: sourceDefault)),
                SafeMigrationPolicy.RepairIfSafe);
        }

        if (blocker == "default_literal_null_text")
        {
            operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)"),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal(null))), SafeMigrationPolicy.RepairIfSafe);
        }

        if (blocker is "expression_literal" or "expression_payload_case")
        {
            operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)"),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Sql(blocker == "expression_literal"
                        ? "lower('A')" : "concat('upper')"))), SafeMigrationPolicy.RepairIfSafe);
        }

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value' AND CHARACTER_MAXIMUM_LENGTH = 20;");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.IsType<MySqlException>(failure);
        Assert.Equal(0, visits.LengthRows);
        Assert.Equal(0, visits.NullRows);
        Assert.Equal(blocker == "missing" ? 0 : 1, columns);
    }

    /// <summary>Separates exact Alter eligibility from the shared Ensure row-length proof in either order.</summary>
    /// <param name="alterFirst">Whether the ineligible Alter precedes the eligible Ensure.</param>
    /// <param name="overlength">Whether the shared physical values exceed the target length.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AlterTransition_AnalyzerSeparatesEligibilityAndDeduplicatesRows(
        bool alterFirst,
        bool overlength
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + $"INSERT INTO `alter_rows` VALUES ('{(overlength ? "too-long" : "kept")}');");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var rejected = AlterTransitionOperation("varchar(30)", "varchar(5)");
        var accepted = AlterTransitionOperation("varchar(20)", "varchar(5)");
        var ensure = new SafeMigrationOperation(
            new EnsureColumnIntent("alter_rows", new ExpectedColumnDefinition("value", typeof(string), true,
                "varchar(5)")), SafeMigrationPolicy.RepairIfSafe);

        SafeMigrationOperation[] operations = alterFirst ? [rejected, ensure, accepted] : [ensure, rejected, accepted];

        // Act
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        var rejectedResult = results[alterFirst ? 0 : 1];
        Assert.Equal(SafeMigrationObservedState.Different, rejectedResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, rejectedResult.RepairCapability);
        Assert.False(rejectedResult.RequiresLiveDataProof);
        foreach (var result in new[] { results[alterFirst ? 1 : 0], results[2] })
        {
            Assert.Equal(overlength ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
                result.ObservedState);
            Assert.Equal(overlength ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
                result.RepairCapability);
            Assert.True(result.RequiresLiveDataProof);
        }

        Assert.Equal(1, connection.NarrowingDataStatementCount);
    }

    /// <summary>
    /// Reuses exact definition identities without sharing eligibility between distinct old contracts.
    /// </summary>
    /// <param name="reuseOperations">
    /// Whether callers reuse operations or only their immutable column definitions.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_AnalyzerBoundsRepeatedEligibility(bool reuseOperations)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES ('kept');");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var target = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)");
        SafeMigrationIntent[] intents =
        [
            new EnsureColumnIntent("alter_rows", target),
            new AlterColumnIntent("alter_rows", target,
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)")),
            new AlterColumnIntent("alter_rows", target,
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(30)")),
        ];

        var shared = intents.Select(intent => new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe))
            .ToArray();

        var operations = Enumerable.Range(0, 65).SelectMany(_ => reuseOperations
                ? shared
                : intents.Select(intent => new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe)))
            .ToArray();

        // Act
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        Assert.Equal(1, connection.NarrowingEligibilityStatementCount);
        Assert.Equal(1, connection.NarrowingDataStatementCount);
        for (var ordinal = 0; ordinal < results.Count; ordinal++)
        {
            Assert.Equal(ordinal % 3 == 2 ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
                results[ordinal].RepairCapability);
            Assert.Equal(ordinal % 3 != 2, results[ordinal].RequiresLiveDataProof);
        }
    }

    /// <summary>Combines narrowing with the existing explicit default backfill contract.</summary>
    /// <param name="hasDefault">Whether the target declares a provably non-null backfill.</param>
    /// <param name="containsNull">Whether existing rows require backfill.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AlterTransition_NullabilityRequiresProofOrExplicitBackfill(
        bool hasDefault,
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + $"INSERT INTO `alter_rows` VALUES ({(containsNull ? "NULL" : "'short'")}), ('kept');");
        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(5)",
                defaultValue: hasDefault ? SafeMigrationDefaultValue.Literal("new") : null),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `alter_rows` WHERE `value` = 'kept';");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(hasDefault || !containsNull
                ? SafeMigrationObservedState.Different : SafeMigrationObservedState.DataBlocked,
            analysis.ObservedState);
        if (hasDefault
            || !containsNull)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(hasDefault || !containsNull ? 0 : 1, nulls);
        Assert.Equal(hasDefault ? 0 : 1, visits.NullRows);
        Assert.Equal(1, preserved);
        Assert.Equal(hasDefault, analysis.RepairMutatesData);
    }

    /// <summary>Rechecks current rows and strict SQL mode instead of trusting a prior successful analysis.</summary>
    /// <param name="disableStrictMode">Whether execution loses strict mode instead of receiving long data.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_RuntimeRejectsInvalidatedPreflight(bool disableStrictMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES ('kept');");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var operation = AlterTransitionOperation("varchar(20)", "varchar(5)");

        // Act
        var beforeResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        await context.Database.ExecuteSqlRawAsync(disableStrictMode
            ? "SET SESSION sql_mode = '';"
            : "INSERT INTO `alter_rows` VALUES ('too-long');");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows`;");

        // Assert
        var before = Assert.Single(beforeResults);
        Assert.Equal(SafeMigrationRepairCapability.Safe, before.RepairCapability);
        Assert.True(before.RequiresLiveDataProof);
        Assert.Contains(disableStrictMode ? "doka_sm_different" : "doka_sm_data_blocked",
            Assert.IsType<MySqlException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(20, length);
        Assert.Equal(disableStrictMode ? 1 : 2, rows);
    }

    /// <summary>Rejects defaults that cannot safely cross the provider's UPDATE-before-MODIFY boundary.</summary>
    /// <param name="sourceType">The column type receiving the backfill before alteration.</param>
    /// <param name="targetType">The final type that must retain every replacement value.</param>
    /// <param name="expressionDefault">Whether a non-null SQL expression has an unproven value domain.</param>
    /// <param name="permissiveMode">Whether a non-strict session could otherwise truncate the backfill.</param>
    /// <param name="castLiteral">Whether a structured literal changes value through an explicit SQL cast.</param>
    [Theory]
    [InlineData("varchar(5)", "varchar(20)", false, true, false)]
    [InlineData("varchar(20)", "varchar(5)", false, false, false)]
    [InlineData("varchar(20)", "varchar(30)", true, false, false)]
    [InlineData("varchar(20)", "varchar(30)", true, false, true)]
    public async Task AlterTransition_RejectsUnprovenBackfillBeforeUpdatingRows(
        string sourceType,
        string targetType,
        bool expressionDefault,
        bool permissiveMode,
        bool castLiteral
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` {sourceType} NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES (NULL), ('kept');");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        if (permissiveMode)
        {
            await context.Database.ExecuteSqlRawAsync("SET SESSION sql_mode = '';");
        }

        var defaultValue = castLiteral
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("replacement", "char(3)"))
            : expressionDefault
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function("COALESCE",
                SafeMigrationSql.Literal("replacement"), SafeMigrationSql.Literal("fallback")))
            : SafeMigrationDefaultValue.Literal("replacement");

        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, targetType, defaultValue: defaultValue),
            new ExpectedColumnDefinition("value", typeof(string), true, sourceType)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, nulls);
    }

    /// <summary>Requires NULL-free rows when a default update could violate a unique key or table check.</summary>
    /// <param name="unique">Whether the table enforces uniqueness instead of a check predicate.</param>
    /// <param name="containsNull">Whether the constrained default update would modify a row.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AlterTransition_ConstrainedBackfillRequiresNullFreeRows(
        bool unique,
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var constraint = unique ? "UNIQUE KEY `uq_value` (`value`)" : "CONSTRAINT `ck_value` CHECK (`value` <> 'x')";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` varchar(10) NULL, {constraint}) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + $"INSERT INTO `alter_rows` VALUES ('{(unique ? "x" : "kept")}')"
            + (containsNull ? ", (NULL);" : ";"));
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                defaultValue: SafeMigrationDefaultValue.Literal("x")),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.Equal(containsNull ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            analysis.RepairCapability);
        Assert.True(analysis.RequiresLiveDataProof);
        if (containsNull)
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(containsNull ? 1 : 0, nulls);
        Assert.Equal(containsNull ? 10 : 20, length);
    }

    /// <summary>
    /// Includes earlier stream constraints in the NULL-proof contract before they exist in the catalog.
    /// </summary>
    /// <param name="constraintKind">The declared unique index, unique constraint, or check.</param>
    /// <param name="containsNull">Whether the subsequent replacement would violate that contract.</param>
    [Theory]
    [InlineData("index", false)]
    [InlineData("index", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("unique-uppercase", false)]
    [InlineData("unique-uppercase", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public async Task AlterTransition_PlannedConstraintRequiresNullFreeBackfill(
        string constraintKind,
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var check = constraintKind == "check";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(10) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + $"INSERT INTO `alter_rows` VALUES ('{(check ? "kept" : "x")}')"
            + (containsNull ? ", (NULL);" : ";"));
        SafeMigrationIntent constraint = constraintKind switch
        {
            "index" => new EnsureIndexIntent(new ExpectedIndexDefinition("uq_value", "alter_rows",
                [new ExpectedIndexKeyDefinition("value")], unique: true)),
            "unique" or "unique-uppercase" => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_value", "alter_rows",
                    [constraintKind == "unique-uppercase" ? "VALUE" : "value"])),
            _ => new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression(
                "ck_value", "alter_rows", SafeMigrationSql.Binary(SafeMigrationSql.Identifier("value"),
                    SafeMigrationSqlBinaryOperator.NotEqual, SafeMigrationSql.Literal("x")))),
        };

        SafeMigrationOperation[] operations =
        [
            new(constraint, SafeMigrationPolicy.ThrowIfDifferent),
            new(new AlterColumnIntent("alter_rows",
                new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal("x")),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
                SafeMigrationPolicy.RepairIfSafe),
        ];

        await using var context = CreateContext(connectionString);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analyses[1].ObservedState);
        Assert.Equal(containsNull ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            analyses[1].RepairCapability);
        if (containsNull)
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(containsNull ? 1 : 0, nulls);
        Assert.Equal(containsNull ? 10 : 20, length);
    }

    /// <summary>Retains transient uniqueness through table rename and a later drop during source recapture.</summary>
    /// <param name="containsNull">Whether the declared unique backfill would collide with an existing value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_RenamedTransientConstraintRetainsNullProof(bool containsNull)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(10) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES ('x')" + (containsNull ? ", (NULL);" : ";"));
        SafeMigrationOperation[] operations =
        [
            new(new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_value", "alter_rows", ["value"])),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(new RenameTableIntent("alter_rows", "renamed_rows"), SafeMigrationPolicy.ThrowIfDifferent),
            new(new AlterColumnIntent("renamed_rows",
                new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal("x")),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
                SafeMigrationPolicy.RepairIfSafe),
            new(new DropUniqueConstraintIntent("uq_value", "renamed_rows"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        await using var context = CreateContext(connectionString);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        _ = await analyzer.AnalyzeAsync(context, operations);
        var captured = ((ISafeMigrationRenamedTableAnalyzer)analyzer).TryGetRenamedTableAnalysis(
            operations[2], "alter_rows", null, out var sourceAnalysis);

        _ = await analyzer.AnalyzeAsync(context, []);
        var retainedAfterEmpty = ((ISafeMigrationRenamedTableAnalyzer)analyzer).TryGetRenamedTableAnalysis(
            operations[2], "alter_rows", null, out _);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var nulls = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `renamed_rows` WHERE `value` IS NULL;");
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'renamed_rows' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.True(captured);
        Assert.False(retainedAfterEmpty);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            sourceAnalysis!.ObservedState);
        Assert.Equal(containsNull ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            sourceAnalysis.RepairCapability);
        if (containsNull)
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(containsNull ? 1 : 0, nulls);
        Assert.Equal(containsNull ? 10 : 20, length);
    }

    /// <summary>Accepts exact BMP defaults and rejects supplementary catalog identities before any mutation.</summary>
    /// <param name="sourceType">The original character or byte-limited string type.</param>
    /// <param name="targetType">The final character or byte-limited string type.</param>
    /// <param name="supplementary">Whether the catalog cannot faithfully expose the literal identity.</param>
    [Theory]
    [InlineData("varchar(20)", "varchar(5)", false)]
    [InlineData("tinytext", "varchar(5)", false)]
    [InlineData("varchar(5)", "text", false)]
    [InlineData("varchar(20)", "varchar(5)", true)]
    [InlineData("tinytext", "varchar(5)", true)]
    [InlineData("varchar(5)", "text", true)]
    public async Task AlterTransition_RequiresExactUnicodeLiteralCatalogIdentity(
        string sourceType,
        string targetType,
        bool supplementary
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` {sourceType} NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES (NULL);");
        await using var context = CreateContext(connectionString);
        var replacement = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(supplementary ? 0x1F600 : 0xE9), 5));
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, targetType,
                defaultValue: SafeMigrationDefaultValue.Literal(replacement)),
            new ExpectedColumnDefinition("value", typeof(string), true, sourceType)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var catalogType = await ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        var defaultCatalog = await ScalarStringAsync(connectionString,
            "SELECT CONCAT(COALESCE(COLUMN_DEFAULT, '<null>'), '|', HEX(COALESCE(COLUMN_DEFAULT, '')), '|', EXTRA) "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        if (!supplementary)
        {
            await ExecuteSqlAsync(connectionString, "INSERT INTO `alter_rows` VALUES (DEFAULT);");
        }

        var storedValues = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(HEX(`value`) ORDER BY HEX(`value`)) FROM `alter_rows`;");

        var preserved = await ScalarIntAsync(connectionString,
            supplementary ? "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;"
                : "SELECT COUNT(*) FROM `alter_rows` WHERE HEX(`value`) = 'C3A9C3A9C3A9C3A9C3A9';");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(supplementary ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            analysis.RepairCapability);
        Assert.Equal(!supplementary, analysis.RepairMutatesData);
        if (supplementary)
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.True(failure is null,
                $"Unexpected failure: {failure}. Synthetic default catalog: {defaultCatalog}; stored: {storedValues}");
        }

        Assert.Equal(supplementary ? sourceType : targetType, catalogType);
        Assert.Equal(supplementary ? 1 : 2, preserved);
    }

    /// <summary>Proves the replacement is representable in the unchanged column character set.</summary>
    /// <param name="asciiLiteral">Whether the replacement is representable in the ASCII source and target.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_BackfillRequiresCharacterSetRepresentability(bool asciiLiteral)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin; INSERT INTO `alter_rows` VALUES (NULL);");
        await using var context = CreateContext(connectionString);
        var replacement = asciiLiteral ? "new" : char.ConvertFromUtf32(0x1F600);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(5)",
                defaultValue: SafeMigrationDefaultValue.Literal(replacement)),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(asciiLiteral ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            analysis.RepairCapability);
        if (asciiLiteral)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(asciiLiteral ? 0 : 1, nulls);
    }

    /// <summary>Compares a direct structured string literal as an exact value and preserves its backfill.</summary>
    /// <param name="replacement">The synthetic default whose catalog spelling must preserve exact identity.</param>
    /// <param name="noBackslashEscapes">Whether literal interpretation disables backslash escaping.</param>
    /// <param name="targetType">The target family determining the engine's exact default display.</param>
    [Theory]
    [InlineData("New", false, "varchar(5)")]
    [InlineData("N'ew", false, "varchar(5)")]
    [InlineData("N\\ew", false, "varchar(5)")]
    [InlineData("N\new", false, "varchar(5)")]
    [InlineData("N\"ew", false, "varchar(5)")]
    [InlineData("N\tew", false, "varchar(5)")]
    [InlineData("N\bew", false, "varchar(5)")]
    [InlineData("N\rew", false, "varchar(5)")]
    [InlineData("N\0ew", false, "varchar(5)")]
    [InlineData("N\u001Aew", false, "varchar(5)")]
    [InlineData("\u20AC", false, "varchar(5)")]
    [InlineData("\u6F22", false, "varchar(5)")]
    [InlineData("N'ew", true, "varchar(5)")]
    [InlineData("N\\ew", true, "varchar(5)")]
    [InlineData("N\new", true, "varchar(5)")]
    [InlineData("N\"ew", true, "varchar(5)")]
    [InlineData("N\tew", true, "varchar(5)")]
    [InlineData("N\bew", true, "varchar(5)")]
    [InlineData("N\rew", true, "varchar(5)")]
    [InlineData("N\0ew", true, "varchar(5)")]
    [InlineData("N\u001Aew", true, "varchar(5)")]
    [InlineData("New", false, "text")]
    [InlineData("N'ew", false, "text")]
    [InlineData("N\\ew", false, "text")]
    [InlineData("N\new", false, "text")]
    [InlineData("N\"ew", false, "text")]
    [InlineData("N\tew", false, "text")]
    [InlineData("N\bew", false, "text")]
    [InlineData("N\rew", false, "text")]
    [InlineData("N\0ew", false, "text")]
    [InlineData("N\u001Aew", false, "text")]
    [InlineData("\u20AC", false, "text")]
    [InlineData("\u6F22", false, "text")]
    [InlineData("N'ew", true, "text")]
    [InlineData("N\\ew", true, "text")]
    [InlineData("N\new", true, "text")]
    [InlineData("N\"ew", true, "text")]
    [InlineData("N\tew", true, "text")]
    [InlineData("N\bew", true, "text")]
    [InlineData("N\rew", true, "text")]
    [InlineData("N\0ew", true, "text")]
    [InlineData("N\u001Aew", true, "text")]
    public async Task AlterTransition_StructuredLiteralBackfillPreservesExactValue(
        string replacement,
        bool noBackslashEscapes,
        string targetType
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES (NULL);");
        var contextConnection = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(contextConnection.ConnectionString);
        var replacementHex = Convert.ToHexString(Encoding.UTF8.GetBytes(replacement));
        var blocked = Fixture.IsMariaDb
            ? targetType == "text"
                && replacement.Any(static character => character == '\\' || char.IsControl(character))
            : noBackslashEscapes && replacement.Contains('\'');

        await context.Database.OpenConnectionAsync();
        if (noBackslashEscapes)
        {
            await context.Database.ExecuteSqlRawAsync(
                "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES');");
        }

        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, targetType,
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(replacement))),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var beforeResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var firstFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var secondFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        if (!blocked)
        {
            await ExecuteSqlAsync(connectionString, "INSERT INTO `alter_rows` VALUES (DEFAULT);");
        }

        var afterResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var preserved = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `alter_rows` WHERE HEX(`value`) = '{replacementHex}';");

        var defaultCatalog = await ScalarStringAsync(connectionString,
            "SELECT CONCAT(COALESCE(COLUMN_DEFAULT, '<null>'), '|', HEX(COALESCE(COLUMN_DEFAULT, '')), '|', EXTRA) "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");

        var storedHex = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(COALESCE(HEX(`value`), '<null>') ORDER BY `value`) FROM `alter_rows`;");

        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        // Assert
        var before = Assert.Single(beforeResults);
        var after = Assert.Single(afterResults);
        if (blocked)
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(firstFailure).Message,
                StringComparison.Ordinal);
            Assert.IsType<MySqlException>(secondFailure);
            Assert.Equal(SafeMigrationRepairCapability.None, before.RepairCapability);
            Assert.Equal(SafeMigrationRepairCapability.None, after.RepairCapability);
            Assert.Equal(0, preserved);
            Assert.Equal(1, unchanged);
            Assert.Equal(20, length);

            return;
        }

        Assert.True(firstFailure is null,
            $"Unexpected failure: {firstFailure}. Synthetic default catalog: {defaultCatalog}; rows: {storedHex}");
        Assert.Null(secondFailure);
        Assert.Equal(SafeMigrationRepairCapability.Safe, before.RepairCapability);
        Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
        Assert.Equal(2, preserved);
    }

    /// <summary>Restricts only target expression defaults under disabled backslash escapes.</summary>
    /// <param name="textTarget">Whether the provider emits the literal as a TEXT expression default.</param>
    /// <param name="sourceDefault">Whether the quote belongs only to the unchanged source contract.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AlterTransition_QuotedPlainDefaultsRespectSqlModeScope(
        bool textTarget,
        bool sourceDefault
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`value` varchar(20) NULL"
            + (sourceDefault ? " DEFAULT 'a''b'" : string.Empty)
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `alter_rows` VALUES (NULL);");
        var configuredConnection = new MySqlConnectionStringBuilder(connectionString) { NoBackslashEscapes = true };
        await using var context = CreateContext(configuredConnection.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(
            "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES');");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), sourceDefault, textTarget ? "text" : "varchar(30)",
                defaultValue: sourceDefault ? null : SafeMigrationDefaultValue.Literal("a'b")),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)",
                defaultValue: sourceDefault ? SafeMigrationDefaultValue.Literal("a'b") : null)),
            SafeMigrationPolicy.RepairIfSafe);

        var blocked = !Fixture.IsMariaDb && textTarget;

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        if (!blocked)
        {
            await ExecuteSqlAsync(connectionString, "INSERT INTO `alter_rows` VALUES (DEFAULT);");
        }

        var nulls = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `alter_rows` WHERE `value` IS NULL;");
        var oldColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value' AND CHARACTER_MAXIMUM_LENGTH = 20;");

        var catalog = await ScalarStringAsync(connectionString,
            "SELECT CONCAT(COALESCE(COLUMN_DEFAULT, '<null>'), '|', HEX(COALESCE(COLUMN_DEFAULT, '')), '|', EXTRA) "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        var valueHex = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(COALESCE(HEX(`value`), '<null>') ORDER BY `value`) FROM `alter_rows`;");

        // Assert
        Assert.Equal(blocked ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            Assert.Single(analyses).RepairCapability);
        if (blocked)
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.True(failure is null,
                $"Unexpected failure: {failure}. Synthetic catalog: {catalog}; row: {valueHex}");
        }

        Assert.Equal(blocked ? 1 : sourceDefault ? 2 : 0, nulls);
        Assert.Equal(blocked ? 1 : 0, oldColumns);
    }

    /// <summary>Rejects an inline InnoDB overflow even when the SQL-layer declared row limit fits.</summary>
    [Fact]
    public async Task AlterTransition_RejectsInlineRowOverflowWithoutDataProbes()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var additionalColumns = string.Join(", ", Enumerable.Range(1, 38)
            .Select(index => $"`other_{index}` varchar(200) NULL"));

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` varchar(200) NULL, {additionalColumns}) "
            + "ENGINE=InnoDB ROW_FORMAT=DYNAMIC DEFAULT CHARSET=latin1 COLLATE=latin1_bin;");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var operation = AlterTransitionOperation("varchar(200)", "varchar(300)");

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, connection.NarrowingDataStatementCount);
        Assert.Equal(200, length);
    }

    /// <summary>Retains the full width of an InnoDB clustered unique fallback when there is no primary key.</summary>
    [Fact]
    public async Task AlterTransition_RejectsClusteredUniqueInlineOverflow()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var additionalColumns = string.Join(", ", Enumerable.Range(1, 750)
            .Select(index => $"`other_{index}` double NOT NULL"));

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `alter_rows` (`value` varchar(100) NOT NULL, {additionalColumns}, "
            + "UNIQUE KEY `uq_value` (`value`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(768)");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows", target,
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(100)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysisResults = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, [operation]);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'alter_rows' AND COLUMN_NAME = 'value';");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `alter_rows` MODIFY COLUMN `value` varchar(768) NOT NULL;");
        var directFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "INSERT INTO `alter_rows` VALUES (REPEAT(CONVERT(0xF09F9880 USING utf8mb4), 768), "
            + string.Join(", ", Enumerable.Repeat("0", 750)) + ");"));

        // Assert
        var analysis = Assert.Single(analysisResults);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Contains("Row size too large", Assert.IsType<MySqlException>(directFailure).Message,
            StringComparison.Ordinal);
        Assert.Equal(100, length);
    }

    /// <summary>Shares the Boolean lossless transition while retaining exact previous metadata.</summary>
    /// <param name="nullableClr">Whether the CLR type carries explicit nullable metadata.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_BooleanDomainPreservesValuesAndReplays(bool nullableClr)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `alter_rows` (`id` int PRIMARY KEY, `value` bit(1) NULL) ENGINE=InnoDB; "
            + "INSERT INTO `alter_rows` VALUES (1, b'0'), (2, b'1'), (3, NULL);");
        await using var context = CreateContext(connectionString);
        var clrType = nullableClr ? typeof(bool?) : typeof(bool);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", clrType, true, "tinyint(1)"),
            new ExpectedColumnDefinition("value", clrType, true, "bit(1)")),
            SafeMigrationPolicy.RepairIfSafe);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var beforeResults = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var afterResults = await analyzer.AnalyzeAsync(context, [operation]);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `alter_rows` WHERE (`id` = 1 AND `value` = 0) "
            + "OR (`id` = 2 AND `value` = 1) OR (`id` = 3 AND `value` IS NULL);");

        // Assert
        var before = Assert.Single(beforeResults);
        var after = Assert.Single(afterResults);
        Assert.Equal(SafeMigrationRepairCapability.Safe, before.RepairCapability);
        Assert.False(before.RequiresLiveDataProof);
        Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
        Assert.Equal(3, preserved);
    }

    /// <summary>Builds an explicit nullable string transition with an exact source definition.</summary>
    private static SafeMigrationOperation AlterTransitionOperation(
        string sourceType,
        string targetType,
        SafeMigrationPolicy policy = SafeMigrationPolicy.RepairIfSafe
    ) => new(new AlterColumnIntent("alter_rows",
        new ExpectedColumnDefinition("value", typeof(string), true, targetType),
        new ExpectedColumnDefinition("value", typeof(string), true, sourceType)), policy);
}
