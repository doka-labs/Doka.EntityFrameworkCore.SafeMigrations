namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string RuntimeMatchingResetSql = "SET @runtime_matching_catalog_visits = 0, "
        + "@runtime_matching_transition_visits = 0, @runtime_matching_repair_visits = 0, "
        + "@runtime_matching_null_visits = 0, @runtime_matching_length_visits = 0, "
        + "@runtime_matching_final_repair_visits = 0;";

    /// <summary>Skips repair catalogs and row proofs after an exact operation-local runtime match.</summary>
    /// <param name="textColumn">Whether the required column is varchar rather than int.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeMatching_ExactRequiredColumnSkipsRepairCatalogsAndRows(
        bool textColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = textColumn ? "varchar(5)" : "int";
        var value = textColumn ? "'kept'" : "7";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` {storeType} NOT NULL "
            + "COMMENT 'canonical') ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `runtime_match_rows` VALUES (1, {value});");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql
            + "SET @doka_sm_state = 'different', @doka_sm_repair_ok = TRUE, "
            + "@doka_sm_nullability_blocked = TRUE, @doka_sm_data_blocked = TRUE;");
        var operation = RuntimeMatchingOperation(textColumn);

        // Act
        await ExecuteOperationsAsync(context, [operation]);
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var preservedRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `runtime_match_rows` WHERE `id` = 1 AND `value` = {value};");
        var report = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        // Assert
        AssertRuntimeMatchingInstrumentation(observer, textColumn, operationCount: 1);
        Assert.Equal(new RuntimeMatchingVisits(1, 0, 0, 0, 0, 0), visits);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report).ObservedState);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Uses real drift as a negative control for catalog and row-proof branch counters.</summary>
    /// <param name="textColumn">Whether varchar narrowing also requires a length proof.</param>
    /// <param name="drift">The facet-only, nullable, or NULL-containing physical mismatch.</param>
    [Theory]
    [InlineData(false, "facet")]
    [InlineData(true, "facet")]
    [InlineData(false, "nullable")]
    [InlineData(true, "nullable")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public async Task RuntimeMatching_DriftStillVisitsRepairCatalogsAndRequiredRows(
        bool textColumn,
        string drift
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var nullable = drift != "facet";
        var blocked = drift == "null";
        var storeType = textColumn ? nullable ? "varchar(20)" : "varchar(5)" : "int";
        var value = blocked ? "NULL" : textColumn ? "'kept'" : "7";
        var nullability = nullable ? "NULL" : "NOT NULL";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` {storeType} {nullability} "
            + "COMMENT 'legacy') ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `runtime_match_rows` VALUES (1, {value});");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        var operation = RuntimeMatchingOperation(textColumn);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `runtime_match_rows` WHERE `id` = 1 AND `value` "
            + (blocked ? "IS NULL;" : $"= {value};"));
        var canonicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'runtime_match_rows' AND COLUMN_NAME = 'value' "
            + "AND IS_NULLABLE = 'NO' AND COLUMN_COMMENT = 'canonical';");
        var report = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        // Assert
        AssertRuntimeMatchingInstrumentation(observer, textColumn, operationCount: 1);
        Assert.Equal(new RuntimeMatchingVisits(1, textColumn ? 1 : 0, 1,
            nullable ? 1 : 0, textColumn && nullable ? 1 : 0, blocked ? 0 : 1), visits);
        if (blocked)
        {
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.Equal(blocked ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(report).ObservedState);
        Assert.Equal(blocked ? 0 : 1, canonicalColumns);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Default and collation drift cannot enter the exact-match short circuit.</summary>
    /// <param name="collationDrift">
    /// Whether the mismatch is an unsafe collation rather than a repairable default.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeMatching_DefaultAndCollationDriftVisitRepairCatalogs(bool collationDrift)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var columnCollation = collationDrift ? "utf8mb4_bin" : "utf8mb4_unicode_ci";
        var defaultClause = collationDrift ? string.Empty : "DEFAULT 'prior' ";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` varchar(5) "
            + $"COLLATE {columnCollation} NOT NULL {defaultClause}COMMENT 'canonical') "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `runtime_match_rows` VALUES (1, 'kept');");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        var operation = RuntimeMatchingOperation(textColumn: true);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `runtime_match_rows` WHERE `id` = 1 AND `value` = 'kept';");

        var finalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'runtime_match_rows' AND COLUMN_NAME = 'value' "
            + $"AND COLLATION_NAME = '{columnCollation}' AND COLUMN_DEFAULT IS NULL "
            + "AND IS_NULLABLE = 'NO' AND COLUMN_COMMENT = 'canonical';");

        var report = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        // Assert
        AssertRuntimeMatchingInstrumentation(observer, textColumn: true, operationCount: 1);
        Assert.Equal(new RuntimeMatchingVisits(1, 1, 1, 0, 0, 1), visits);
        if (collationDrift)
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.Equal(collationDrift ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(report).ObservedState);
        Assert.Equal(1, finalColumns);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Invalidates an earlier exact match after raw DDL and DML in the same operation stream.</summary>
    /// <param name="textColumn">Whether the invalidated column is varchar rather than int.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeMatching_RawSqlCannotReuseEarlierMatchingEvidence(
        bool textColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = textColumn ? "varchar(5)" : "int";
        var value = textColumn ? "'kept'" : "7";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` {storeType} NOT NULL "
            + "COMMENT 'canonical') ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `runtime_match_rows` VALUES (1, {value});");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql
            + "SET @runtime_matching_after_rejection = NULL, @runtime_matching_first_repair_visits = NULL;");
        var operation = RuntimeMatchingOperation(textColumn);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(operation);
        _ = builder.Sql("SET @runtime_matching_first_repair_visits = @runtime_matching_repair_visits; "
            + $"ALTER TABLE `runtime_match_rows` MODIFY COLUMN `value` {storeType} NULL COMMENT 'canonical'; "
            + "UPDATE `runtime_match_rows` SET `value` = NULL WHERE `id` = 1;");
        builder.Operations.Add(RuntimeMatchingOperation(textColumn));
        _ = builder.Sql("SET @runtime_matching_after_rejection = 1;");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var firstRepairVisits = await ContextScalarIntAsync(context,
            "SELECT @runtime_matching_first_repair_visits;");
        var laterStatementExecuted = await ContextScalarIntAsync(context,
            "SELECT @runtime_matching_after_rejection IS NOT NULL;");
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `runtime_match_rows` WHERE `id` = 1 AND `value` IS NULL;");

        // Assert
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        AssertRuntimeMatchingInstrumentation(observer, textColumn, operationCount: 2);
        Assert.Equal(new RuntimeMatchingVisits(2, textColumn ? 1 : 0, 1, 1, 0, 0), visits);
        Assert.Equal(0, firstRepairVisits);
        Assert.Equal(0, laterStatementExecuted);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Rejects a foreign database before inspecting even an otherwise matching target column.</summary>
    [Fact]
    public async Task RuntimeMatching_ForeignQualifierRejectsBeforeMatchingCatalog()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignDatabase = new MySqlConnectionStringBuilder(foreignConnectionString).Database;
        await ExecuteSqlAsync(foreignConnectionString,
            "CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` varchar(5) NOT NULL "
            + "COMMENT 'canonical') ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `runtime_match_rows` VALUES (1, 'kept');");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        var operation = RuntimeMatchingOperation(textColumn: true, schema: foreignDatabase);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var preservedRows = await ScalarIntAsync(foreignConnectionString,
            "SELECT COUNT(*) FROM `runtime_match_rows` WHERE `id` = 1 AND BINARY `value` = BINARY 'kept';");
        var localTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'runtime_match_rows';");

        // Assert
        Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        AssertRuntimeMatchingInstrumentation(observer, textColumn: true, operationCount: 1);
        Assert.Equal(new RuntimeMatchingVisits(0, 0, 0, 0, 0, 0), visits);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preservedRows);
        Assert.Equal(0, localTables);
    }

    /// <summary>Visits the final repair predicate only for Different on direct nullable-column paths.</summary>
    /// <param name="operationKind">The explicit EnsureColumn or AlterColumn runtime path.</param>
    /// <param name="liveComment">The matching or drifted physical comment before execution.</param>
    /// <param name="expectedFinalRepairVisits">Zero for Matching and one for a repairable Different state.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, "canonical", 0)]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, "legacy", 1)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, "canonical", 0)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, "legacy", 1)]
    public async Task RuntimeMatching_NonLazyNullableColumnEvaluatesFinalRepairOnlyForDifferent(
        SafeMigrationOperationKind operationKind,
        string liveComment,
        int expectedFinalRepairVisits
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `runtime_match_rows` (`id` int PRIMARY KEY, `value` int NULL "
            + $"COMMENT '{liveComment}') ENGINE=InnoDB; "
            + "INSERT INTO `runtime_match_rows` VALUES (1, 7), (2, NULL);");

        var observer = new RuntimeMatchingProbeInterceptor();
        await using var context = CreateRuntimeMatchingContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(RuntimeMatchingResetSql);
        var target = new ExpectedColumnDefinition("value", typeof(int), isNullable: true,
            storeType: "int", comment: "canonical");

        var previous = new ExpectedColumnDefinition("value", typeof(int), isNullable: true,
            storeType: "int", comment: "legacy");

        SafeMigrationIntent intent = operationKind == SafeMigrationOperationKind.EnsureColumn
            ? new EnsureColumnIntent("runtime_match_rows", target)
            : new AlterColumnIntent("runtime_match_rows", target, previous);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var plan = CaptureMySqlNullEligibilityPlan(context, operation);

        // Act
        await ExecuteOperationsAsync(context, [operation]);
        var visits = await ReadRuntimeMatchingVisitsAsync(context);
        var cleaned = await ContextScalarIntAsync(context,
            "SELECT @doka_sm_state IS NULL AND @doka_sm_repair_ok IS NULL;");
        var canonicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'runtime_match_rows' AND COLUMN_NAME = 'value' "
            + "AND IS_NULLABLE = 'YES' AND COLUMN_COMMENT = 'canonical';");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `runtime_match_rows` WHERE (`id` = 1 AND `value` = 7) "
            + "OR (`id` = 2 AND `value` IS NULL);");

        // Assert
        Assert.False(plan.RequiresLazyStateEvaluation);
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Equal(operationKind, operation.Intent.Kind);
        Assert.Equal(1, observer.FinalRepairInstrumentations);
        Assert.Equal(0, observer.MatchingInstrumentations);
        Assert.Equal(0, observer.CatalogInstrumentations);
        Assert.Equal(0, observer.RowInstrumentations);
        Assert.Equal(new RuntimeMatchingVisits(0, 0, 0, 0, 0, expectedFinalRepairVisits), visits);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, canonicalColumns);
        Assert.Equal(2, preservedRows);
    }

    /// <summary>Uses the normal provider execution path with only a test-owned branch observer.</summary>
    private SafeMigrationDbContext CreateRuntimeMatchingContext(
        string connectionString,
        RuntimeMatchingProbeInterceptor observer
    ) => new(new DbContextOptionsBuilder<SafeMigrationDbContext>()
        .UseMySql(connectionString, Fixture.ServerVersion)
        .UseMySqlSafeMigrations<SafeMigrationDbContext>()
        .AddInterceptors(observer)
        .Options);

    /// <summary>Creates a full required scalar contract without an analysis preflight.</summary>
    private static SafeMigrationOperation RuntimeMatchingOperation(
        bool textColumn,
        string? schema = null
    ) => new(new EnsureColumnIntent("runtime_match_rows", new ExpectedColumnDefinition(
        "value", textColumn ? typeof(string) : typeof(int), isNullable: false,
        storeType: textColumn ? "varchar(5)" : "int", maxLength: textColumn ? 5 : null,
        comment: "canonical"), schema), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Reads only bounded counters that cleanup intentionally does not own.</summary>
    private static async Task<RuntimeMatchingVisits> ReadRuntimeMatchingVisitsAsync(
        DbContext context
    ) => new(
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_catalog_visits;"),
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_transition_visits;"),
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_repair_visits;"),
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_null_visits;"),
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_length_visits;"),
        await ContextScalarIntAsync(context, "SELECT @runtime_matching_final_repair_visits;"));

    /// <summary>Rejects vacuous zero counts when a changed SQL shape prevents instrumentation.</summary>
    private static void AssertRuntimeMatchingInstrumentation(
        RuntimeMatchingProbeInterceptor observer,
        bool textColumn,
        int operationCount
    )
    {
        Assert.Equal(operationCount, observer.MatchingInstrumentations);
        Assert.Equal(operationCount, observer.FinalRepairInstrumentations);
        Assert.Equal(operationCount * (textColumn ? 2 : 1), observer.CatalogInstrumentations);
        Assert.Equal(operationCount * (textColumn ? 2 : 1), observer.RowInstrumentations);
    }

    /// <summary>Contains only branch counts, without retaining SQL, identifiers, or user data.</summary>
    private sealed record RuntimeMatchingVisits(
        int Matching,
        int Transition,
        int PhysicalRepair,
        int NullRows,
        int LengthRows,
        int FinalRepair
    );

    /// <summary>Counts actual CASE and prepared-proof visits without changing the original predicate results.</summary>
    private sealed class RuntimeMatchingProbeInterceptor : DbCommandInterceptor
    {
        /// <summary>Gets the number of exact matching branches found in executed setup commands.</summary>
        public int MatchingInstrumentations { get; private set; }

        /// <summary>Gets the number of final repair-precondition branches found in setup.</summary>
        public int FinalRepairInstrumentations { get; private set; }

        /// <summary>Gets the number of transition and physical-repair branches found in setup.</summary>
        public int CatalogInstrumentations { get; private set; }

        /// <summary>Gets the number of prepared row-proof branches found in setup.</summary>
        public int RowInstrumentations { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            var sql = command.CommandText;
            sql = InstrumentBranch(sql,
                "SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN ",
                " ELSE @doka_sm_state END;", "catalog", out var matchingCount);
            MatchingInstrumentations += matchingCount;

            sql = InstrumentBranch(sql,
                "SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' THEN ",
                " ELSE FALSE END;", "final_repair", out var finalRepairCount);
            FinalRepairInstrumentations += finalRepairCount;

            sql = InstrumentBranch(sql,
                "SET @doka_sm_transition_eligible = CASE WHEN @doka_sm_state IS NULL THEN ",
                " ELSE FALSE END;", "transition", out var transitionCount);
            sql = InstrumentBranch(sql,
                "SET @doka_sm_column_repair_eligible = CASE WHEN @doka_sm_state IS NULL THEN ",
                " ELSE FALSE END, @doka_sm_nullability_blocked = FALSE;", "repair", out var repairCount);
            CatalogInstrumentations += transitionCount + repairCount;

            foreach (var payload in DecodeNullProofMeasurementPayloads(sql))
            {
                var replacement = InstrumentBranch(payload, "SELECT ", " INTO @doka_sm_nullability_blocked",
                    "null", out var nullCount);
                replacement = InstrumentBranch(replacement, "SELECT ", " INTO @doka_sm_data_blocked",
                    "length", out var lengthCount);
                if (nullCount + lengthCount == 0)
                {
                    continue;
                }

                RowInstrumentations += nullCount + lengthCount;
                sql = sql.Replace(Convert.ToHexString(Encoding.UTF8.GetBytes(payload)),
                    Convert.ToHexString(Encoding.UTF8.GetBytes(replacement)), StringComparison.Ordinal);
            }

            command.CommandText = sql;

            return ValueTask.FromResult(result);
        }

        /// <summary>Counts an exact test-targeted branch while preserving its original expression.</summary>
        private static string InstrumentBranch(
            string sql,
            string prefix,
            string suffix,
            string counter,
            out int instrumentations
        )
        {
            instrumentations = 0;
            var start = sql.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return sql;
            }

            start += prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                return sql;
            }

            // WHY: A nested CASE forces a branch-entry observation before evaluating the
            // unchanged predicate. Negative controls prove the counters cannot silently stay zero.
            instrumentations = 1;
            var variable = "@runtime_matching_" + counter + "_visits";

            return sql.Insert(end, " ELSE NULL END")
                .Insert(start, $"CASE WHEN ({variable} := COALESCE({variable}, 0) + 1) > 0 THEN ");
        }
    }
}
