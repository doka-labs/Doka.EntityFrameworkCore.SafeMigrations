namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerGuardRenderingAllocationTests
{
    /// <summary>Retains every guard byte across direct, deferred, preamble and repair-policy paths.</summary>
    [Theory]
    [InlineData(false, null, false, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(true, null, false, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(false, "", false, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(false, "SELECT N'O''Brien';\n", false, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(true, "SELECT N'\uD83D\uDE80';\n", true, SafeMigrationPolicy.ThrowIfDifferent)]
    public void PreparedGuard_AllFragmentsMatchIndependentOriginalReference(
        bool delayed,
        string? preamble,
        bool managedData,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = CreateOperation(policy, managedData);
        var plan = CreatePlan(delayed, preamble);
        var baseline = new[] { CreateCommand(context, "SELECT N'O''Brien \uD83D\uDE80';\n") };
        var repair = new[] { CreateCommand(context, "EXEC sys.sp_executesql N'SELECT N''repair''''ed'';';\n") };
        var reference = OriginalGuardBody(operation, plan, baseline, repair).ToString();
        var expected = ReferenceScope(reference);

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(
            operation, plan, new IndexedOnlyCommands(baseline), new IndexedOnlyCommands(repair));

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(reference, SqlServerGuardedSqlTestContract.DecodeScope(actual));
        Assert.Contains("EXEC sys.sp_executesql", reference, StringComparison.Ordinal);
        Assert.Contains("THROW 51005, N'doka_sm_postcondition:", reference, StringComparison.Ordinal);
        Assert.All(baseline.Concat(repair), static command => Assert.False(command.TransactionSuppressed));
    }

    /// <summary>Real catalog plans and EF baselines retain the complete generated managed-data guard.</summary>
    [Fact]
    public void GeneratedManagedGuard_CompleteBodyMatchesOriginalReference()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, true);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var plan = catalog.Build(operation);
        var mutation = catalog.BuildModelManagedDataMutationSql((ModelManagedDataIntent)operation.Intent,
            scopeInUseMessage: SafeMigrationDiagnosticMessages.FormatRuntime(operation,
                "doka_sm_identity_scope_in_use", 0));
        var baseline = context.GetService<ISqlServerSafeMigrationsBaselineGenerator>()
            .Generate([new SqlOperation { Sql = mutation }], context.Model);

        var expected = OriginalGuardBody(operation, plan, baseline, [], 0).ToString();

        // Act
        var actual = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Contains("SET IDENTITY_INSERT", actual, StringComparison.Ordinal);
        Assert.Contains("after cancellation/timeout", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Measures prepared rendering only, retaining the former exact-sized outer scope and product budgets.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedGuard_AllocatesLessThanOriginalActionAndScalarIntermediates(
        bool delayed
    )
    {
        // Arrange
        const int renderCount = 32;
        var operation = CreateOperation(SafeMigrationPolicy.RepairIfSafe, false);
        var expression = "CASE WHEN "
            + string.Join(" AND ", Enumerable.Repeat("N'O''Brien \uD83D\uDE80' = N'O''Brien \uD83D\uDE80'", 128))
            + " THEN 1 ELSE 0 END";

        var plan = CreatePlan(delayed, null) with
        {
            RepairPrecondition = expression,
            ExecutionPostcondition = expression,
        };

        Func<string> current = () => SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);
        Func<string> original = () => SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(
            OriginalGuardBody(operation, plan, [], []));

        var expected = ReferenceScope(OriginalGuardBody(operation, plan, [], []).ToString());

        // WHY: Both measurements begin with identical immutable prepared inputs.
        // Warm rendering and the planner before excluding input preparation from
        // the per-thread allocation count; no machine-specific timing is asserted.
        current();
        original();

        // Act
        var currentResult = MeasureAllocations(current, renderCount);
        var originalResult = MeasureAllocations(original, renderCount);

        // Assert
        Assert.Equal(expected, currentResult.Sql);
        Assert.Equal(expected, originalResult.Sql);
        Assert.InRange(currentResult.Bytes, 1, originalResult.Bytes - 1);
    }

    /// <summary>
    /// Retains the former interpolation behavior for wrapped null predicates without flattening scopes.
    /// </summary>
    [Fact]
    public void PreparedGuard_WrappedNullPredicatesKeepOriginalBytes()
    {
        // Arrange
        var operation = CreateOperation(SafeMigrationPolicy.RepairIfSafe, false);
        var plan = CreatePlan(true, null) with
        {
            RepairPrecondition = null!,
            Postcondition = null!,
            ExecutionPostcondition = null,
        };

        var expected = ReferenceScope(OriginalGuardBody(operation, plan, [], []).ToString());

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Contains("COALESCE((), 0)", SqlServerGuardedSqlTestContract.DecodeScope(actual),
            StringComparison.Ordinal);
    }

    /// <summary>Rejects a required unwrapped expression while optional preambles still accept null.</summary>
    [Fact]
    public void PreparedGuard_RequiredDeferredExpressionNullStillFails()
    {
        // Arrange
        var operation = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false);
        var plan = CreatePlan(true, null) with { StateExpression = null! };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));

        // Assert
        Assert.Equal("expression", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }

    /// <summary>
    /// Preserves joined statement boundaries, nested quoting and empty entries in one isolated scope.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1025)]
    public void IdentifierScope_CompleteStatementListMatchesIndependentReference(
        int count
    )
    {
        // Arrange
        var statements = Enumerable.Range(0, count).Select(static index => (index % 3) switch
        {
            0 => "SELECT N'O''Brien \uD83D\uDE80';\n",
            1 => string.Empty,
            _ => "EXEC sys.sp_executesql N'SELECT N''\uD834\uDD1E''''nested'';';\n",
        }).ToArray();

        var body = string.Join("\n", statements);
        var expected = ReferenceScope(body);

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.BuildIsolatedIdentifierGuardSql(statements);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(body, SqlServerGuardedSqlTestContract.DecodeScope(actual));
        Assert.Equal(body, string.Join("\n", statements));
    }

    /// <summary>Preserves the original null-entry joining contract without exposing a caller-visible scope.</summary>
    [Fact]
    public void IdentifierScope_NullEntryRetainsEmptyStatementBoundary()
    {
        // Arrange
        string[] statements = ["SELECT N'quoted';", null!, "SELECT 2;"];
        var body = string.Join("\n", statements);

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.BuildIsolatedIdentifierGuardSql(statements);

        // Assert
        Assert.Equal(ReferenceScope(body), actual);
        Assert.Equal("SELECT N'quoted';\n\nSELECT 2;", SqlServerGuardedSqlTestContract.DecodeScope(actual));
    }

    /// <summary>Rejects an absent completed statement collection before allocating a private scope.</summary>
    [Fact]
    public void IdentifierScope_NullCollectionFailsBeforeRendering()
    {
        // Arrange
        IReadOnlyList<string>? statements = null;

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.BuildIsolatedIdentifierGuardSql(statements!));

        // Assert
        Assert.Equal("statements", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }

    /// <summary>Counts prepared identifier composition against the former joined body and growing buffer.</summary>
    [Fact]
    public void IdentifierScope_AllocatesLessThanJoinedBodyAndLiteralBuffer()
    {
        // Arrange
        const int renderCount = 32;
        var statements = Enumerable.Repeat("SELECT N'O''Brien \uD83D\uDE80';\n", 4096).ToArray();
        var expected = ReferenceScope(string.Join("\n", statements));
        Func<string> current = () => SqlServerSafeMigrationsSqlGenerator.BuildIsolatedIdentifierGuardSql(statements);
        Func<string> original = () => OriginalIdentifierScope(statements);

        // WHY: The same completed statement list is prepared before counting.
        // Only the removed joining and literal-buffer work differs between paths.
        current();
        original();

        // Act
        var currentResult = MeasureAllocations(current, renderCount);
        var originalResult = MeasureAllocations(original, renderCount);

        // Assert
        Assert.Equal(expected, currentResult.Sql);
        Assert.Equal(expected, originalResult.Sql);
        Assert.InRange(currentResult.Bytes, 1, originalResult.Bytes - 1);
    }
}
