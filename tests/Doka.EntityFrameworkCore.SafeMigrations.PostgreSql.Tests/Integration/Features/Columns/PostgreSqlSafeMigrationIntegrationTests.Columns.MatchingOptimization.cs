namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Preserves qualified collation through mutable-facet and varchar-length repairs.</summary>
    /// <param name="qualified">Whether Npgsql needs a companion schema-qualified COLLATE statement.</param>
    /// <param name="lengthTransition">Whether the reviewed target widens the existing varchar type.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CollationRepair_PreservesExactOidDefaultCommentAndRows(
        bool qualified,
        bool lengthTransition
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var collationSql = qualified ? "repair_collation.shared" : "\"C\"";
        var collation = qualified ? new SafeMigrationCollationIdentifier("shared", "repair_collation")
            : new SafeMigrationCollationIdentifier("C");

        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA repair_collation; CREATE COLLATION repair_collation.shared FROM \"C\"; "
            + $"CREATE TABLE matching_optimization_rows (value character varying(32) COLLATE {collationSql} "
            + "NOT NULL DEFAULT 'legacy'); COMMENT ON COLUMN matching_optimization_rows.value IS 'legacy'; "
            + "INSERT INTO matching_optimization_rows VALUES ('preserved');");
        await using var context = CreateContext(connectionString);
        var targetLength = lengthTransition ? 64 : 32;
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, $"character varying({targetLength})",
                maxLength: targetLength, collation: collation,
                defaultValue: SafeMigrationDefaultValue.Literal("canonical"), comment: "canonical")),
            SafeMigrationPolicy.RepairIfSafe);

        var oidBefore = await ScalarIntAsync(connectionString,
            "SELECT attcollation::integer FROM pg_catalog.pg_attribute "
            + "WHERE attrelid = 'matching_optimization_rows'::regclass AND attname = 'value';");

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replayFailure = failure is null
            ? await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation])) : failure;

        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        var oidAfter = await ScalarIntAsync(connectionString,
            "SELECT attcollation::integer FROM pg_catalog.pg_attribute "
            + "WHERE attrelid = 'matching_optimization_rows'::regclass AND attname = 'value';");

        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_rows WHERE value = 'preserved';");

        // Assert
        Assert.Null(failure);
        Assert.Null(replayFailure);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analysis).ObservedState);
        Assert.Equal(oidBefore, oidAfter);
        Assert.Equal(1, preserved);
    }

    /// <summary>Rejects repair into a different physical collation even when values fit.</summary>
    [Fact]
    public async Task CollationRepair_DifferentPhysicalOidCannotAuthorizeRepair()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA repair_collation; CREATE COLLATION repair_collation.shared FROM \"C\"; "
            + "CREATE TABLE matching_optimization_rows (value character varying(32) COLLATE \"C\" NOT NULL); "
            + "INSERT INTO matching_optimization_rows VALUES ('preserved');");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(64)",
                maxLength: 64, collation: new SafeMigrationCollationIdentifier("shared", "repair_collation"))),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_rows WHERE value = 'preserved';");

        // Assert
        Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal(1, preserved);
    }

    /// <summary>Reports an absent collation as a mismatch with diagnostic evidence.</summary>
    /// <param name="qualified">Whether the missing identity carries an explicit schema.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollationLookup_MissingIdentityRetainsDifferentEvidenceAndRejects(
        bool qualified
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE matching_optimization_rows (value text COLLATE \"C\" NULL); "
            + "INSERT INTO matching_optimization_rows VALUES ('preserved');");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "text",
                collation: new SafeMigrationCollationIdentifier("absent_expected_collation",
                    qualified ? "pg_catalog" : null))), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_rows WHERE value = 'preserved';");

        // Assert
        var assessment = Assert.Single(analysis);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Contains(assessment.Differences, difference => difference.Facet == "column_collation");
        Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal(1, preserved);
    }

    /// <summary>Proves complete matching targets skip repair qualification using a raising catalog sentinel.</summary>
    /// <param name="varchar">Whether the runtime plan contains narrowing qualification.</param>
    /// <param name="alter">Whether the operation carries a distinct old column contract.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MatchingRuntime_CompleteTargetSkipsRepairQualificationAndReplays(
        bool varchar,
        bool alter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = varchar ? "character varying(32)" : "timestamp without time zone";
        var value = varchar ? "'preserved'" : "'2026-01-02 03:04:05.123456'";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE matching_optimization_rows (value {storeType} NOT NULL); "
            + $"INSERT INTO matching_optimization_rows VALUES ({value}); " + NullProofSentinelSql);
        await using var context = CreateContext(connectionString);
        var target = new ExpectedColumnDefinition(
            "value", varchar ? typeof(string) : typeof(DateTime), false, storeType);

        SafeMigrationIntent intent = alter
            ? new AlterColumnIntent("matching_optimization_rows", target,
                new ExpectedColumnDefinition("value", varchar ? typeof(string) : typeof(DateTime), true,
                    varchar ? "character varying(64)" : storeType))
            : new EnsureColumnIntent("matching_optimization_rows", target);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var first = await Record.ExceptionAsync(() =>
            ExecuteRepairQualificationSentinelAsync(context, operation, plan));

        var replay = await Record.ExceptionAsync(() =>
            ExecuteRepairQualificationSentinelAsync(context, operation, plan));

        var preserved = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM matching_optimization_rows WHERE value = {value};");

        // Assert
        Assert.Null(first);
        Assert.Null(replay);
        Assert.Equal(1, preserved);
    }

    /// <summary>Requires descendant NULL proof even when the parent metadata matches.</summary>
    [Fact]
    public async Task MatchingRuntime_ParentOnlyNotNullCannotSkipDescendantProof()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var constraint = Fixture.ServerVersion.Major >= 18
            ? "NOT NULL value NO INHERIT"
            : "CHECK (value IS NOT NULL) NO INHERIT";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE matching_optimization_rows (value character varying(32) NULL); "
            + "CREATE TABLE matching_optimization_child () INHERITS (matching_optimization_rows); "
            + "INSERT INTO matching_optimization_rows VALUES ('preserved'); "
            + "INSERT INTO matching_optimization_child VALUES (NULL); "
            + $"ALTER TABLE matching_optimization_rows ADD CONSTRAINT parent_not_null {constraint}; "
            + NullProofSentinelSql);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(32)")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var sentinelFailure = await Record.ExceptionAsync(() =>
            ExecuteRepairQualificationSentinelAsync(context, operation, plan));

        var realFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_child WHERE value IS NULL;");

        // Assert
        Assert.Equal("P1901", Assert.IsType<PostgresException>(sentinelFailure).SqlState);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(realFailure).SqlState);
        Assert.Equal(1, nullRows);
    }

    /// <summary>Does not accept parent-only matching without old-contract authority for descendant rows.</summary>
    /// <param name="containsNull">Whether the descendant violates the target NULL contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingRuntime_AlterParentOnlyNotNullRequiresOldContractAuthority(
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
            "CREATE TABLE matching_optimization_rows (value character varying(32) NULL); "
            + "CREATE TABLE matching_optimization_child () INHERITS (matching_optimization_rows); "
            + "INSERT INTO matching_optimization_rows VALUES ('parent'); "
            + $"INSERT INTO matching_optimization_child VALUES ({childValue}); "
            + $"ALTER TABLE matching_optimization_rows ADD CONSTRAINT parent_not_null {constraint};");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(32)"),
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(32)")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = BuildNullProofEligibilityPlan(context, operation);

        // Act
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var inline = context.Database.GetDbConnection().CreateCommand();
        inline.CommandText = "SELECT (" + plan.RenderStateExpression() + ");";
        var inlineState = Convert.ToString(await inline.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_child WHERE value "
            + (containsNull ? "IS NULL;" : "= 'child';"));

        // Assert
        var expectedState = Fixture.ServerVersion.Major >= 18 || !containsNull
            ? SafeMigrationObservedState.Different : SafeMigrationObservedState.DataBlocked;

        Assert.Equal(expectedState, Assert.Single(analysis).ObservedState);
        Assert.Equal(expectedState == SafeMigrationObservedState.Different ? "different" : "data_blocked",
            inlineState);
        if (Fixture.ServerVersion.Major >= 18
            || containsNull)
        {
            Assert.Equal(Fixture.ServerVersion.Major >= 18 ? "P1001" : "P1003",
                Assert.IsType<PostgresException>(failure).SqlState);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, preserved);
    }

    /// <summary>Rechecks a matching no-op after later column DDL and DML in the same operation stream.</summary>
    [Fact]
    public async Task MatchingRuntime_RechecksTargetAfterInterveningColumnMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE matching_optimization_rows (value timestamp without time zone NOT NULL); "
            + "INSERT INTO matching_optimization_rows VALUES ('2026-01-02 03:04:05');");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("matching_optimization_rows",
            new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp without time zone")),
            SafeMigrationPolicy.RepairIfSafe);

        MigrationOperation[] operations =
        [
            operation,
            new SqlOperation
            {
                Sql = "ALTER TABLE matching_optimization_rows ALTER COLUMN value DROP NOT NULL; "
                    + "INSERT INTO matching_optimization_rows VALUES (NULL);",
            },
            operation,
        ];

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM matching_optimization_rows WHERE value IS NULL;");

        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [operation], CancellationToken.None);

        // Assert
        Assert.Equal("P1003", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal(1, nullRows);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(analysis).ObservedState);
    }

    /// <summary>Replaces repair qualification with a volatile failure while retaining real metadata.</summary>
    /// <param name="context">The isolated context supplying the actual runtime connection.</param>
    /// <param name="operation">The reviewed operation whose real guards remain active.</param>
    /// <param name="plan">The actual catalog plan with only repair qualification instrumented.</param>
    /// <returns>A task completing after the rendered guard executes.</returns>
    private static async Task ExecuteRepairQualificationSentinelAsync(
        DbContext context,
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan plan
    )
    {
        var sentinel = plan with
        {
            DataProbe = plan.DataProbe is null ? null
                : plan.DataProbe with { TransitionInvariantExpression = "null_proof_sentinel()" },
            NullabilityDataProbe = plan.NullabilityDataProbe is null ? null
                : plan.NullabilityDataProbe with { RepairInvariantExpression = "null_proof_sentinel()" },
        };

        var renderer = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod("BuildGuardedSql",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The PostgreSQL runtime guard renderer was not found.");

        var sql = (string)renderer.Invoke(null,
            [operation, sentinel, Array.Empty<MigrationCommand>(), Array.Empty<MigrationCommand>()])!;

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
