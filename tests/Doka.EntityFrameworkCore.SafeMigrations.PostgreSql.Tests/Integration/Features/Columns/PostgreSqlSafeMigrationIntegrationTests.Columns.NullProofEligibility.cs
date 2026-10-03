namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    private const string NullProofSentinelSql = "CREATE FUNCTION null_proof_sentinel() RETURNS boolean "
        + "LANGUAGE plpgsql AS $null_proof$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P1901', "
        + "MESSAGE = 'null_proof_branch_executed'; END $null_proof$;";

    /// <summary>Proves valid NOT NULL metadata excludes row queries in matching and repairable-drift paths.</summary>
    /// <param name="varchar">Whether the contract uses varchar rather than timestamp.</param>
    /// <param name="drift">Whether only the comment and default need repair.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullProofEligibility_ValidatedRequiredColumnSkipsInlineAndRuntimeRows(
        bool varchar,
        bool drift
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = varchar ? "character varying(800)" : "timestamp without time zone";
        var value = varchar ? "'preserved'" : "'2026-01-02 03:04:05.123456'";
        var comment = drift ? "legacy" : "canonical";
        var defaultClause = drift ? $" DEFAULT {value}" : string.Empty;
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE proof_eligibility_rows (id integer PRIMARY KEY, value {storeType} NOT NULL{defaultClause}); "
            + $"COMMENT ON COLUMN proof_eligibility_rows.value IS '{comment}'; "
            + $"INSERT INTO proof_eligibility_rows VALUES (1, {value}); " + NullProofSentinelSql);

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var inlineState = await ExecuteInlineNullProofSentinelAsync(context, plan);
        var runtimeException = await Record.ExceptionAsync(() =>
            ExecuteRuntimeNullProofSentinelAsync(context, operation, plan));
        var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var preservedRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM proof_eligibility_rows WHERE id = 1 AND value = {value};");
        var canonicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'proof_eligibility_rows' "
            + "AND column_name = 'value' AND is_nullable = 'NO' AND column_default IS NULL;");

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        Assert.Equal(drift ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Equal(drift ? "different" : "matching", inlineState);
        Assert.Null(runtimeException);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay).ObservedState);
        Assert.Equal(1, preservedRows);
        Assert.Equal(1, canonicalColumns);
    }

    /// <summary>Retains fresh NULL proofs for genuinely nullable columns, including rejection and repair.</summary>
    /// <param name="containsNull">Whether a NULL row must reject the operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullProofEligibility_NullableColumnExecutesBothProofPaths(
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var value = containsNull ? "NULL" : "'preserved'";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE proof_eligibility_rows (id integer PRIMARY KEY, value character varying(800) NULL); "
            + $"INSERT INTO proof_eligibility_rows VALUES (1, {value}); " + NullProofSentinelSql);

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: true);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var inlineException = await Record.ExceptionAsync(() => ExecuteInlineNullProofSentinelAsync(context, plan));
        var sentinelException = await Record.ExceptionAsync(() =>
            ExecuteRuntimeNullProofSentinelAsync(context, operation, plan));
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var preservedRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM proof_eligibility_rows WHERE id = 1 AND value "
            + (containsNull ? "IS NULL;" : "= 'preserved';"));

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(inlineException).SqlState);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(sentinelException).SqlState);
        if (containsNull)
        {
            Assert.Equal("P1003", Assert.IsType<PostgresException>(runtimeException).SqlState);
        }
        else
        {
            Assert.Null(runtimeException);
        }

        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(replay).ObservedState);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Rejects invalid NOT NULL as matching and validates clean data during safe repair.</summary>
    /// <remarks>PostgreSQL 14-17 use an unvalidated CHECK because invalid NOT NULL first exists in 18.</remarks>
    /// <param name="containsNull">Whether the pre-existing row invalidates the new NOT NULL contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullProofEligibility_NotValidContractRequiresRowsAndCannotFalselyMatch(
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var value = containsNull ? "NULL" : "'preserved'";
        var constraint = Fixture.ServerVersion.Major >= 18
            ? "NOT NULL value NOT VALID"
            : "CHECK (value IS NOT NULL) NOT VALID";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE proof_eligibility_rows (id integer PRIMARY KEY, value character varying(800) NULL); "
            + $"COMMENT ON COLUMN proof_eligibility_rows.value IS 'canonical'; "
            + $"INSERT INTO proof_eligibility_rows VALUES (1, {value}); "
            + $"ALTER TABLE proof_eligibility_rows ADD CONSTRAINT proof_not_null {constraint}; "
            + NullProofSentinelSql);

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: true);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var declaredNotNull = await ScalarIntAsync(connectionString,
            "SELECT attnotnull::integer FROM pg_catalog.pg_attribute "
            + "WHERE attrelid = 'proof_eligibility_rows'::regclass AND attname = 'value';");
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var inlineException = await Record.ExceptionAsync(() => ExecuteInlineNullProofSentinelAsync(context, plan));
        var sentinelException = await Record.ExceptionAsync(() =>
            ExecuteRuntimeNullProofSentinelAsync(context, operation, plan));
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var constraintValidated = Fixture.ServerVersion.Major >= 18
            ? await ScalarIntAsync(connectionString,
                "SELECT convalidated::integer FROM pg_catalog.pg_constraint WHERE conname = 'proof_not_null';")
            : -1;

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        Assert.Equal(Fixture.ServerVersion.Major >= 18 ? 1 : 0, declaredNotNull);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.Contains(analysis.Differences, static difference => difference.Facet == "column_nullability");
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(inlineException).SqlState);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(sentinelException).SqlState);
        if (containsNull)
        {
            Assert.Equal("P1003", Assert.IsType<PostgresException>(runtimeException).SqlState);
        }
        else
        {
            Assert.Null(runtimeException);
        }

        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(replay).ObservedState);
        if (Fixture.ServerVersion.Major >= 18)
        {
            Assert.Equal(containsNull ? 0 : 1, constraintValidated);
        }
    }

    /// <summary>Does not suppress descendant row evidence from parent-only NO INHERIT metadata.</summary>
    /// <param name="containsNull">Whether only the child contains a NULL value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullProofEligibility_NoInheritParentRequiresFreshDescendantRows(
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var childValue = containsNull ? "NULL" : "'child'";
        var constraint = Fixture.ServerVersion.Major >= 18
            ? "NOT NULL value NO INHERIT"
            : "CHECK (value IS NOT NULL) NO INHERIT";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE proof_eligibility_rows (id integer, value character varying(800) NULL); "
            + "CREATE TABLE proof_eligibility_child () INHERITS (proof_eligibility_rows); "
            + "COMMENT ON COLUMN proof_eligibility_rows.value IS 'canonical'; "
            + "INSERT INTO proof_eligibility_rows VALUES (1, 'parent'); "
            + $"INSERT INTO proof_eligibility_child VALUES (2, {childValue}); "
            + $"ALTER TABLE proof_eligibility_rows ADD CONSTRAINT proof_parent_not_null {constraint}; "
            + NullProofSentinelSql);

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: true);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var inlineException = await Record.ExceptionAsync(() => ExecuteInlineNullProofSentinelAsync(context, plan));
        var sentinelException = await Record.ExceptionAsync(() =>
            ExecuteRuntimeNullProofSentinelAsync(context, operation, plan));
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preservedParentRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM ONLY proof_eligibility_rows WHERE id = 1 AND value = 'parent';");
        var preservedChildRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM proof_eligibility_child WHERE id = 2 AND value "
            + (containsNull ? "IS NULL;" : "= 'child';"));

        // Assert
        var analysis = Assert.Single(providerAnalysis);
        var expectedState = containsNull ? SafeMigrationObservedState.DataBlocked
            : Fixture.ServerVersion.Major >= 18 ? SafeMigrationObservedState.Matching
            : SafeMigrationObservedState.Different;

        Assert.Equal(expectedState, analysis.ObservedState);
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(inlineException).SqlState);
        Assert.Equal("P1901", Assert.IsType<PostgresException>(sentinelException).SqlState);
        if (containsNull)
        {
            Assert.Equal("P1003", Assert.IsType<PostgresException>(runtimeException).SqlState);
        }
        else
        {
            Assert.Null(runtimeException);
        }

        Assert.Equal(1, preservedParentRows);
        Assert.Equal(1, preservedChildRows);
    }

    /// <summary>Invalidates inherited row evidence after child DML while retaining proven leaf metadata.</summary>
    /// <param name="inherited">Whether a child is outside the parent's NOT NULL declaration.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullProofEligibility_ProjectedDmlInvalidatesOnlyRowDependentMatching(
        bool inherited
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var nullability = inherited ? "NULL" : "NOT NULL";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE proof_eligibility_rows (id integer, value character varying(800) {nullability}); "
            + "COMMENT ON COLUMN proof_eligibility_rows.value IS 'canonical'; "
            + "INSERT INTO proof_eligibility_rows VALUES (1, 'parent');");
        if (inherited)
        {
            var constraint = Fixture.ServerVersion.Major >= 18
                ? "NOT NULL value NO INHERIT"
                : "CHECK (value IS NOT NULL) NO INHERIT";
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE proof_eligibility_child () INHERITS (proof_eligibility_rows); "
                + "INSERT INTO proof_eligibility_child VALUES (2, 'child'); "
                + $"ALTER TABLE proof_eligibility_rows ADD CONSTRAINT proof_parent_not_null {constraint};");
        }

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: true);
        var projection = new SafeMigrationPreflightProjection();

        // Act
        var liveResults = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var live = liveResults.Single();
        projection.ObserveProviderPostcondition(new InsertDataOperation
        {
            Table = inherited ? "proof_eligibility_child" : "proof_eligibility_rows",
            Columns = ["id", "value"],
            Values = new object?[,] { { 3, inherited ? null : "next", }, },
        });
        var projected = projection.Project(operation, live);
        await ExecuteSqlAsync(connectionString, inherited
            ? "INSERT INTO proof_eligibility_child VALUES (3, NULL);"
            : "INSERT INTO proof_eligibility_rows VALUES (3, 'next');");
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var repeatedResults = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        // Assert
        Assert.Single(liveResults);
        Assert.Equal(inherited, live.RequiresLiveDataProof);
        Assert.Equal(inherited && Fixture.ServerVersion.Major < 18
                ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            live.ObservedState);
        Assert.Equal(inherited ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Matching,
            projected.ObservedState);
        if (inherited)
        {
            Assert.Equal("projected_data_state_unknown", projected.Code);
            Assert.Equal("P1003", Assert.IsType<PostgresException>(runtimeException).SqlState);
        }
        else
        {
            Assert.Null(runtimeException);
        }

        Assert.Equal(inherited ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(repeatedResults).ObservedState);
    }

    /// <summary>Does not reuse a successful analysis proof after intervening DML.</summary>
    [Fact]
    public async Task NullProofEligibility_RechecksRowsAfterPreflightDml()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE proof_eligibility_rows (id integer PRIMARY KEY, value character varying(800) NULL); "
            + "INSERT INTO proof_eligibility_rows VALUES (1, 'preserved');");

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: true);

        // Act
        var preflight = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "INSERT INTO proof_eligibility_rows VALUES (2, NULL);");
        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var repeatedAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var insertedNullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM proof_eligibility_rows WHERE id = 2 AND value IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(preflight).ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, Assert.Single(preflight).RepairCapability);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(runtimeException).SqlState);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(repeatedAnalysis).ObservedState);
        Assert.Equal(1, insertedNullRows);
    }

    /// <summary>Rechecks a NULL-only timestamp proof after a concurrent writer releases the repair lock.</summary>
    [Fact]
    public async Task NullProofEligibility_TimestampRepairRechecksAfterBlockedLock()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connectionString = await Fixture.CreateDatabaseAsync(cancellation.Token);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE proof_eligibility_rows (id integer PRIMARY KEY, value timestamp without time zone NULL); "
            + "INSERT INTO proof_eligibility_rows VALUES (1, '2026-01-02 03:04:05.123456');");

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: false);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync(cancellation.Token);
        await using var transaction = await writer.BeginTransactionAsync(cancellation.Token);
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO proof_eligibility_rows VALUES (2, NULL);";
            await insert.ExecuteNonQueryAsync(cancellation.Token);
        }

        // Act
        var execution = Task.Run(() => ExecuteOperationsAsync(context, [operation], cancellation.Token),
            CancellationToken.None);
        await WaitForBlockedPostgreSqlNarrowingCommandAsync(
            connectionString, "proof_eligibility_rows", cancellation.Token);
        await transaction.CommitAsync(cancellation.Token);
        var exception = await Record.ExceptionAsync(() => execution);
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], cancellation.Token);
        var nullableColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'proof_eligibility_rows' "
            + "AND column_name = 'value' AND is_nullable = 'YES';");
        var insertedNullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM proof_eligibility_rows WHERE id = 2 AND value IS NULL;");

        // Assert
        Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(analysis).ObservedState);
        Assert.Equal(1, nullableColumns);
        Assert.Equal(1, insertedNullRows);
    }

    /// <summary>Keeps identifier-dependent row statements unplanned for missing tables and columns.</summary>
    /// <param name="tableExists">Whether only the target column is missing.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullProofEligibility_MissingTargetsKeepRowSqlBehindBindingGuard(
        bool tableExists
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, NullProofSentinelSql);
        if (tableExists)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE proof_eligibility_rows (id integer);");
        }

        await using var context = CreateContext(connectionString);
        var operation = BuildNullProofEligibilityOperation(varchar: false);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);
        var runtimeException = await Record.ExceptionAsync(() =>
            ExecuteRuntimeNullProofSentinelAsync(context, operation, plan));

        // Assert
        Assert.Equal(tableExists ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            Assert.Single(analysis).ObservedState);
        if (tableExists)
        {
            Assert.Null(runtimeException);
        }
        else
        {
            Assert.Equal("P1004", Assert.IsType<PostgresException>(runtimeException).SqlState);
        }
    }

    /// <summary>Creates the generic contract used by live metadata and row-boundary probes.</summary>
    private static SafeMigrationOperation BuildNullProofEligibilityOperation(
        bool varchar
    ) => new(new EnsureColumnIntent("proof_eligibility_rows", new ExpectedColumnDefinition(
        "value", varchar ? typeof(string) : typeof(DateTime), false,
        storeType: varchar ? "character varying(800)" : "timestamp without time zone",
        maxLength: varchar ? 800 : null, comment: "canonical")), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Builds real catalog predicates without retaining a live catalog snapshot.</summary>
    private static PostgreSqlSafeMigrationRuntimePlan BuildNullProofEligibilityPlan(
        DbContext context,
        SafeMigrationOperation operation
    ) => new PostgreSqlSafeMigrationCatalogSqlBuilder(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>())
        .Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true);

    /// <summary>Proves the inline CASE branch excludes row SQL using a volatile raising function.</summary>
    private static async Task<string> ExecuteInlineNullProofSentinelAsync(
        DbContext context,
        PostgreSqlSafeMigrationRuntimePlan plan
    )
    {
        var sentinelPlan = plan with
        {
            NullabilityDataProbe = plan.NullabilityDataProbe! with { BlockedExpression = "null_proof_sentinel()" },
        };

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (" + sentinelPlan.RenderStateExpression(false, false) + "), ("
            + sentinelPlan.RenderRepairPrecondition(false, false) + ");";

        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("The inline classifier returned no state.");
    }

    /// <summary>Substitutes the same raising function inside the generated PL/pgSQL row-reading branch.</summary>
    private static async Task ExecuteRuntimeNullProofSentinelAsync(
        DbContext context,
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan plan
    )
    {
        var generated = Assert.Single(
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = generated.CommandText.Replace(
            plan.NullabilityDataProbe!.BlockedExpression, "null_proof_sentinel()", StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
