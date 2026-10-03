namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Compares complete prepared guards with the original rendering shape and allocation work.</summary>
public sealed class SqlServerGuardRenderingAllocationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=guard_rendering;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    private static readonly SafeMigrationObservedState[] s_states = Enum.GetValues<SafeMigrationObservedState>();

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
        Assert.Contains("THROW 51005, N'doka_sm_postcondition'", reference, StringComparison.Ordinal);
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
        var mutation = catalog.BuildModelManagedDataMutationSql((ModelManagedDataIntent)operation.Intent);
        var baseline = context.GetService<ISqlServerSafeMigrationsBaselineGenerator>()
            .Generate([new SqlOperation { Sql = mutation }], context.Model);

        var expected = OriginalGuardBody(operation, plan, baseline, []).ToString();

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

    /// <summary>
    /// Preserves provider order, exact SQL, suppression flags and delegated execution without enumeration.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void CommandWrapping_IndexesCompletedBatchAndRetainsEveryProviderBoundary(
        int commandCount,
        bool prepend
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var connection = context.GetService<IRelationalConnection>();
        var commands = Enumerable.Range(0, commandCount).Select(index => new RecordingCommand(
            dependencies.CommandBuilderFactory.Create().Append($"SELECT N'O''Brien {index}';\n").Build(),
            context, dependencies, index + 7, (index % 2) == 1)).ToArray();

        var source = new IndexedOnlyCommands(commands);
        var prefix = CreateCommand(context, "SELECT N'ordinary prefix';\n");
        var destination = new List<MigrationCommand> { prefix };
        IReadOnlyDictionary<string, object?> parameters = new Dictionary<string, object?> { ["sentinel"] = 7 };
        var sourceOffset = prepend ? 0 : 1;
        var prefixOffset = prepend ? commandCount : 0;

        // Act
        if (prepend)
        {
            SqlServerSafeMigrationsSqlGenerator.PrependGuardedCommands(
                destination, source, dependencies, analyzer, metadata);
        }
        else
        {
            SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
                destination, source, dependencies, analyzer, metadata);
        }

        var results = destination.Skip(sourceOffset).Take(commandCount)
            .Select(command => command.ExecuteNonQuery(connection, parameters)).ToArray();

        // Assert
        Assert.Same(prefix, destination[prefixOffset]);
        Assert.Equal(commandCount + 1, destination.Count);
        Assert.Equal(Enumerable.Range(7, commandCount), results);
        for (var index = 0; index < commandCount; index++)
        {
            var wrapped = Assert.IsType<SqlServerSafeMigrationGuardedCommand>(destination[index + sourceOffset]);
            Assert.Equal(commands[index].CommandText, wrapped.CommandText);
            Assert.Equal(commands[index].TransactionSuppressed, wrapped.TransactionSuppressed);
            Assert.Same(commands[index].CommandLogger, wrapped.CommandLogger);
            Assert.Equal(1, commands[index].Executions);
            Assert.Same(parameters, commands[index].ReceivedParameters);
        }
    }

    /// <summary>Rejects cached indexed wrappers after quarantine without invoking their provider command.</summary>
    [Fact]
    public void CommandWrapping_QuarantineStillPreventsDelegatedExecution()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var command = new RecordingCommand(dependencies.CommandBuilderFactory.Create().Append("SELECT 7;").Build(),
            context, dependencies, 7, false);

        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var connection = context.GetService<IRelationalConnection>();
        var destination = new List<MigrationCommand>();
        SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            destination, new IndexedOnlyCommands([command]), dependencies, analyzer, metadata);

        analyzer.QuarantineConnection();

        // Act
        var failure = Record.Exception(() => destination[0].ExecuteNonQuery(connection));

        // Assert
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Equal(0, command.Executions);
        Assert.Equal(ConnectionState.Closed, connection.DbConnection.State);
    }

    /// <summary>Counts prepared wrapping against the former captured selector without measuring list growth.</summary>
    [Fact]
    public void CommandWrapping_AllocatesLessThanOriginalCapturedSelector()
    {
        // Arrange
        const int renderCount = 128;
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var source = Array.AsReadOnly(new[] { CreateCommand(context, "SELECT 7;\n") });
        var currentDestinations = Enumerable.Range(0, renderCount)
            .Select(_ => new List<MigrationCommand>(source.Count)).ToArray();

        var originalDestinations = Enumerable.Range(0, renderCount)
            .Select(_ => new List<MigrationCommand>(source.Count)).ToArray();

        Action<int> current = index => SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            currentDestinations[index], source, dependencies, analyzer, metadata);

        Action<int> original = index => originalDestinations[index].AddRange(source.Select(command =>
            new SqlServerSafeMigrationGuardedCommand(command, dependencies, analyzer, metadata)));

        // WHY: Prepare both destination capacities and warm wrapper creation
        // before counting. The former per-operation selector is the only extra
        // allocation work under comparison; neither path executes SQL.
        SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            new List<MigrationCommand>(source.Count), source, dependencies, analyzer, metadata);

        new List<MigrationCommand>(source.Count).AddRange(source.Select(command =>
            new SqlServerSafeMigrationGuardedCommand(command, dependencies, analyzer, metadata)));

        // Act
        var currentBytes = MeasureWrappingAllocations(current, renderCount);
        var originalBytes = MeasureWrappingAllocations(original, renderCount);

        // Assert
        Assert.InRange(currentBytes, 1, originalBytes - 1);
        Assert.All(currentDestinations.Concat(originalDestinations), destination =>
        {
            var wrapped = Assert.IsType<SqlServerSafeMigrationGuardedCommand>(Assert.Single(destination));
            Assert.Equal(source[0].CommandText, wrapped.CommandText);
            Assert.Equal(source[0].TransactionSuppressed, wrapped.TransactionSuppressed);
            Assert.Same(source[0].CommandLogger, wrapped.CommandLogger);
        });
    }

    /// <summary>Retains original references and order while ignoring ordinary, null and design-time entries.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeExtraction_MixedStreamMatchesOriginalFilterWithoutMutatingInput(
        bool includeSafeOperations
    )
    {
        // Arrange
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            new SqlOperation { Sql = "SELECT 7;" },
            null!,
        };

        if (includeSafeOperations)
        {
            operations.Add(CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false));
        }

        operations.Add(new SqlOperation { Sql = "SELECT 9;" });
        if (includeSafeOperations)
        {
            operations.Add(CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, true));
        }

        var expected = operations.OfType<SafeMigrationOperation>().ToArray();
        var original = operations.ToArray();

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, expected.Length);

        // Assert
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index], actual[index]);
        }

        Assert.Equal(original.Length, operations.Count);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Same(original[index], operations[index]);
        }

        if (!includeSafeOperations)
        {
            Assert.Same(Array.Empty<SafeMigrationOperation>(), actual);
        }
    }

    /// <summary>Returns an independent exact-sized array while retaining the original safe object reference.</summary>
    [Fact]
    public void SafeExtraction_ResultArrayMutationDoesNotChangeSourceStream()
    {
        // Arrange
        var original = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false);
        var replacement = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, true);
        var operations = new List<MigrationOperation> { original };

        // Act
        var extracted = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, 1);
        var reference = extracted[0];
        extracted[0] = replacement;

        // Assert
        Assert.Same(original, reference);
        Assert.Same(original, Assert.Single(operations));
        Assert.Same(replacement, Assert.Single(extracted));
    }

    /// <summary>
    /// Rejects a mismatched preflight count instead of losing operations or returning null array slots.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SafeExtraction_InexactCountFailsClosedWithoutInputMutation(
        int count
    )
    {
        // Arrange
        var safe = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false);
        var ordinary = new SqlOperation { Sql = "SELECT 7;" };
        var operations = new List<MigrationOperation> { ordinary, safe };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, count));

        // Assert
        Assert.Contains("preflight count", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Same(ordinary, operations[0]);
        Assert.Same(safe, operations[1]);
        Assert.Equal(2, operations.Count);
    }

    /// <summary>Rejects negative and oversized counts before extraction can allocate an invalid output.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void SafeExtraction_OutOfRangeCountFailsBeforeAllocation(
        int count
    )
    {
        // Arrange
        var operations = new List<MigrationOperation> { new SqlOperation { Sql = "SELECT 7;" } };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, count));

        // Assert
        Assert.Equal("safeOperationCount", Assert.IsType<ArgumentOutOfRangeException>(failure).ParamName);
    }

    /// <summary>
    /// Counts prepared 1000-operation extraction against the original filtering and array growth path.
    /// </summary>
    [Fact]
    public void SafeExtraction_PreparedThousandOperationListAllocatesLessThanOriginalFilter()
    {
        // Arrange
        const int operationCount = 1000;
        const int renderCount = 32;
        var operations = Enumerable.Range(0, operationCount)
            .Select(_ => (MigrationOperation)CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false)).ToList();

        var snapshot = operations.ToArray();
        var currentResult = Array.Empty<SafeMigrationOperation>();
        var originalResult = Array.Empty<SafeMigrationOperation>();

        // WHY: The source list, intents and exact preflight count are prepared
        // before measuring. Only extraction is counted, and both code paths are
        // warmed so first-use initialization cannot masquerade as a saving.
        currentResult = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, operationCount);
        originalResult = operations.OfType<SafeMigrationOperation>().ToArray();

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < renderCount; iteration++)
        {
            currentResult = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, operationCount);
        }

        var currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < renderCount; iteration++)
        {
            originalResult = operations.OfType<SafeMigrationOperation>().ToArray();
        }

        var originalBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.InRange(currentBytes, 1, originalBytes - 1);
        Assert.Equal(operationCount, currentResult.Length);
        Assert.Equal(operationCount, originalResult.Length);
        Assert.Equal(operationCount, operations.Count);
        for (var index = 0; index < operationCount; index++)
        {
            Assert.Same(snapshot[index], currentResult[index]);
            Assert.Same(snapshot[index], originalResult[index]);
            Assert.Same(snapshot[index], operations[index]);
        }
    }

    /// <summary>Preserves a valid marker and the exact position of ordinary commands around a safe operation.</summary>
    [Fact]
    public void SafeExtraction_GenerationRetainsMixedMarkerAndCommandOrder()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            new SqlOperation { Sql = "SELECT 7;" },
            CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false),
            new SqlOperation { Sql = "SELECT 9;" },
        };

        var snapshot = operations.ToArray();

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);

        // Assert
        Assert.Equal(4, commands.Count);
        Assert.StartsWith("EXEC sys.sp_executesql N'", commands[0].CommandText, StringComparison.Ordinal);
        Assert.Contains("SELECT 7;", commands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_state", commands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("SELECT 9;", commands[3].CommandText, StringComparison.Ordinal);
        Assert.All(commands, static command => Assert.IsType<SqlServerSafeMigrationGuardedCommand>(command));
        for (var index = 0; index < snapshot.Length; index++)
        {
            Assert.Same(snapshot[index], operations[index]);
        }
    }

    /// <summary>
    /// Preserves fail-closed unsupported preflight despite a marker and preceding ordinary operation.
    /// </summary>
    [Fact]
    public void SafeExtraction_UnsupportedMixedStreamStillFailsBeforeReturningCommands()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationDesignTimeServicesRequiredOperation());
        builder.Sql("SELECT 7;");
        builder.EnsureIndex(new ExpectedIndexDefinition("IX_unsupported", "items",
            [new ExpectedIndexKeyDefinition("Id")], method: "hash"), SafeMigrationPolicy.ThrowIfDifferent);

        var snapshot = builder.Operations.ToArray();

        // Act
        var failure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model));

        // Assert
        Assert.Contains("unsupported", Assert.IsType<NotSupportedException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(snapshot.Length, builder.Operations.Count);
        for (var index = 0; index < snapshot.Length; index++)
        {
            Assert.Same(snapshot[index], builder.Operations[index]);
        }
    }

    /// <summary>Retains the existing noninitial design-time marker rejection after safe extraction.</summary>
    [Fact]
    public void SafeExtraction_NoninitialMarkerRemainsRejected()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operations = new List<MigrationOperation>
        {
            CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false),
            new SafeMigrationDesignTimeServicesRequiredOperation(),
        };

        var snapshot = operations.ToArray();

        // Act
        var failure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, context.Model));

        // Assert
        Assert.Contains("first migration operation", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Same(snapshot[0], operations[0]);
        Assert.Same(snapshot[1], operations[1]);
        Assert.Equal(snapshot.Length, operations.Count);
    }

    /// <summary>
    /// Creates a quoted fixture intent without catalog access or input allocations during measurement.
    /// </summary>
    private static SafeMigrationOperation CreateOperation(
        SafeMigrationPolicy policy,
        bool managedData
    )
    {
        if (managedData)
        {
            return new SafeMigrationOperation(new EnsureModelManagedDataIntent("O'Brien", ["Id"], ["int"],
                ["Id", "Caption"], ["int", "nvarchar(80)"],
                new object?[,] { { 7, "O'Brien \uD83D\uDE80" } }, null, null), policy);
        }

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.EnsureColumn("O'Brien", new ExpectedColumnDefinition("Caption", typeof(string), true,
            "nvarchar(80)"), policy);

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Exercises all optional support fragments and fixed scalar templates with quoted expressions.</summary>
    private static SqlServerSafeMigrationRuntimePlan CreatePlan(
        bool delayed,
        string? preamble
    )
        => new("N'matching'", "1", SafeMigrationRepairCapability.Safe, "1")
        {
            PhysicalTableSupportExpression = "CASE WHEN N'O''Brien' = N'O''Brien' THEN 1 ELSE 0 END",
            ColumnLayoutFailureExpression = "CAST(NULL AS nvarchar(32))",
            ColumnCollationSupportExpression = "1",
            DefaultValueSupportExpression = "CASE WHEN N'\uD83D\uDE80' = N'\uD83D\uDE80' THEN 1 ELSE 0 END",
            DefaultValueSupportRequiresDelayedBinding = delayed,
            IndexFilterSupportExpression = "1",
            PrerequisiteExpression = "1",
            StateEvaluationGuardExpression = "1",
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            RequiresDelayedBinding = delayed,
            CatalogPreambleSql = preamble,
            PostApplySql = "SELECT N'post O''Brien';\n",
        };

    /// <summary>Prepares a non-transaction-suppressed provider command before the measured rendering path.</summary>
    private static MigrationCommand CreateCommand(
        DbContext context,
        string sql
    )
    {
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var command = dependencies.CommandBuilderFactory.Create().Append(sql).Build();

        return new MigrationCommand(command, context, dependencies.Logger);
    }

    /// <summary>Retains the pre-change guard construction, including its action string and scalar wrappers.</summary>
    private static StringBuilder OriginalGuardBody(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repair
    )
    {
        var actionCase = OriginalActionCase(operation, plan.RepairCapability);
        var builder = new StringBuilder(1024);
        if (operation.Intent is EnsureModelManagedDataIntent)
        {
            builder.Append("-- Script clients must recover IDENTITY_INSERT or close this session "
                + "after cancellation/timeout.\n");
        }

        builder.Append("IF COALESCE(")
            .Append("HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')")
            .Append(", 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");

        if (plan.PhysicalTableSupportExpression is { } physical)
        {
            builder.Append("IF COALESCE((").Append(physical)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.ColumnLayoutFailureExpression is { } layout)
        {
            builder.Append("IF (").Append(layout)
                .Append(") IS NOT NULL\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.ColumnCollationSupportExpression is { } collation)
        {
            builder.Append("IF COALESCE((").Append(collation)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.DefaultValueSupportExpression is { } defaultValue)
        {
            if (plan.DefaultValueSupportRequiresDelayedBinding)
            {
                builder.Append("DECLARE @doka_default_supported int;\n");
                OriginalScalar(builder, defaultValue, "int", "@doka_default_supported", string.Empty);
                defaultValue = "@doka_default_supported";
            }

            builder.Append("IF COALESCE((").Append(defaultValue)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.IndexFilterSupportExpression is { } filter)
        {
            builder.Append("IF COALESCE((").Append(filter)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        // WHY: Both fixture intents have a null schema, so their original guard
        // requires the dbo admission boundary before evaluating prepared state.
        builder.Append("IF COALESCE(SCHEMA_NAME(), N'') <> N'dbo'\nBEGIN\n")
            .Append("    THROW 51004, N'doka_sm_prerequisite_missing', 1;\nEND;\n")
            .Append("DECLARE @doka_state nvarchar(32);\n")
            .Append("DECLARE @doka_action nvarchar(32);\n")
            .Append("DECLARE @doka_repair_ok int;\n")
            .Append("IF COALESCE((").Append(plan.PrerequisiteExpression).Append("), 0) <> 1\n")
            .Append("BEGIN\n    SET @doka_state = N'prerequisite_missing';\n")
            .Append("    SET @doka_repair_ok = 0;\nEND\nELSE IF COALESCE((")
            .Append(plan.StateEvaluationGuardExpression)
            .Append("), 0) <> 1\nBEGIN\n")
            .Append("    SET @doka_state = (")
            .Append(plan.StateEvaluationGuardFailureExpression ?? "N'unsupported'")
            .Append(");\n    SET @doka_repair_ok = 0;\nEND\nELSE\nBEGIN\n");

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
        {
            OriginalScalar(builder, plan.StateExpression, "nvarchar(32)", "@doka_state", "    ",
                plan.CatalogPreambleSql);

            OriginalScalar(builder, $"COALESCE(({plan.RepairPrecondition}), 0)", "int", "@doka_repair_ok", "    ");
        }
        else
        {
            builder.Append("    SET @doka_state = (").Append(plan.StateExpression).Append(");\n")
                .Append("    SET @doka_repair_ok = COALESCE((")
                .Append(plan.RepairPrecondition).Append("), 0);\n");
        }

        builder.Append("END;\n")
            .Append("SET @doka_action = ").Append(actionCase).Append(";\n")
            .Append("IF @doka_action = N'reject_different'\nBEGIN\n")
            .Append("    THROW 51001, N'doka_sm_different', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_unsupported'\nBEGIN\n")
            .Append("    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_data_blocked'\nBEGIN\n")
            .Append("    THROW 51003, N'doka_sm_data_blocked', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_prerequisite_missing'\nBEGIN\n")
            .Append("    THROW 51004, N'doka_sm_prerequisite_missing', 1;\nEND;\n")
            .Append("IF @doka_action = N'apply'\nBEGIN\n");

        OriginalCommands(builder, baseline);
        builder.Append("END\nELSE IF @doka_action = N'repair'\nBEGIN\n");
        OriginalCommands(builder, repair);
        builder.Append("END;\n")
            .Append("IF @doka_action IN (N'apply', N'repair')\nBEGIN\n");
        if (plan.PostApplySql is { Length: > 0 } postApply)
        {
            OriginalDynamic(builder, postApply, "    ");
        }

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
        {
            builder.Append("    DECLARE @doka_postcondition int;\n");
            OriginalScalar(builder, $"COALESCE(({plan.ExecutionPostcondition ?? plan.Postcondition}), 0)",
                "int", "@doka_postcondition", "    ");

            builder.Append("    IF @doka_postcondition <> 1\n");
        }
        else
        {
            builder.Append("    IF COALESCE((")
                .Append(plan.ExecutionPostcondition ?? plan.Postcondition)
                .Append("), 0) <> 1\n");
        }

        builder.Append("    BEGIN\n")
            .Append("        THROW 51005, N'doka_sm_postcondition', 1;\n")
            .Append("    END;\nEND;");

        return builder;
    }

    /// <summary>Retains the original intermediate action string and its complete policy decision mapping.</summary>
    private static string OriginalActionCase(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var builder = new StringBuilder("CASE @doka_state ");
        foreach (var state in s_states)
        {
            var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, state,
                operation.Policy, repairCapability);

            var stateCode = state switch
            {
                SafeMigrationObservedState.Missing => "missing",
                SafeMigrationObservedState.Matching => "matching",
                SafeMigrationObservedState.Different => "different",
                SafeMigrationObservedState.Unsupported => "unsupported",
                SafeMigrationObservedState.DataBlocked => "data_blocked",
                SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
                SafeMigrationObservedState.TransitionReady => "transition_ready",
                _ => throw new UnreachableException(),
            };

            builder.Append("WHEN N'").Append(stateCode).Append("' THEN ");
            if (decision.Action == SafeMigrationAction.Repair)
            {
                builder.Append("CASE WHEN @doka_repair_ok = 1 THEN N'repair' ")
                    .Append("ELSE N'reject_different' END ");
            }
            else
            {
                var actionCode = decision.Action switch
                {
                    SafeMigrationAction.Apply => "apply",
                    SafeMigrationAction.NoOp => "no_op",
                    SafeMigrationAction.RejectDifferent => "reject_different",
                    SafeMigrationAction.RejectUnsupported => "reject_unsupported",
                    SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
                    SafeMigrationAction.RejectPrerequisiteMissing => "reject_prerequisite_missing",
                    _ => throw new UnreachableException(),
                };

                builder.Append("N'").Append(actionCode).Append("' ");
            }
        }

        return builder.Append("ELSE N'reject_unsupported' END").ToString();
    }

    /// <summary>
    /// Retains the original scalar interpolation and streamed literal work for allocation comparison.
    /// </summary>
    private static void OriginalScalar(
        StringBuilder builder,
        string expression,
        string type,
        string target,
        string indentation,
        string? preamble = null
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        builder.Append(indentation).Append("EXEC sys.sp_executesql N'");
        OriginalEscape(builder, preamble.AsSpan());
        builder.Append("\nSET @doka_value = (");
        OriginalEscape(builder, expression.AsSpan());
        builder.Append(");', N'@doka_value ").Append(type)
            .Append(" OUTPUT', @doka_value = ").Append(target).Append(" OUTPUT;\n");
    }

    /// <summary>Retains each deferred DDL scope and the empty-command assignment from the original guard.</summary>
    private static void OriginalCommands(
        StringBuilder builder,
        IReadOnlyList<MigrationCommand> commands
    )
    {
        if (commands.Count == 0)
        {
            builder.Append("    SET @doka_repair_ok = @doka_repair_ok;\n");

            return;
        }

        foreach (var command in commands)
        {
            OriginalDynamic(builder, command.CommandText, "    ");
        }
    }

    /// <summary>Retains the original required dynamic-command validation and literal rendering work.</summary>
    private static void OriginalDynamic(
        StringBuilder builder,
        string sql,
        string indentation
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        builder.Append(indentation).Append("EXEC sys.sp_executesql N'");
        OriginalEscape(builder, sql.AsSpan());
        builder.Append("';\n");
    }

    /// <summary>Retains streamed escaping so the comparison adds no artificial replaced-string allocations.</summary>
    private static void OriginalEscape(
        StringBuilder builder,
        ReadOnlySpan<char> sql
    )
    {
        int offset;
        while ((offset = sql.IndexOf('\'')) >= 0)
        {
            builder.Append(sql.Slice(0, offset)).Append("''");
            sql = sql.Slice(offset + 1);
        }

        builder.Append(sql);
    }

    /// <summary>Retains the removed complete identifier-body join and streamed growing literal buffer.</summary>
    private static string OriginalIdentifierScope(
        IReadOnlyList<string> statements
    )
    {
        var body = string.Join("\n", statements);
        var builder = new StringBuilder();
        OriginalDynamic(builder, body, string.Empty);

        return builder.ToString();
    }

    /// <summary>Checks final outer quoting independently from the product's exact-size copy implementation.</summary>
    private static string ReferenceScope(
        string body
    ) => "EXEC sys.sp_executesql N'" + body.Replace("'", "''", StringComparison.Ordinal) + "';\n";

    /// <summary>Counts only prepared guard rendering on the executing thread, excluding delegate creation.</summary>
    private static (long Bytes, string Sql) MeasureAllocations(
        Func<string> render,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sql = string.Empty;
        for (var index = 0; index < count; index++)
        {
            sql = render();
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        return (bytes, sql);
    }

    /// <summary>Counts prepared command wrapping while excluding fixture and destination allocations.</summary>
    private static long MeasureWrappingAllocations(
        Action<int> wrap,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            wrap(index);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Exposes a completed batch whose enumeration would reveal an allocation-path regression.</summary>
    private sealed class IndexedOnlyCommands : IReadOnlyList<MigrationCommand>
    {
        private readonly IReadOnlyList<MigrationCommand> _commands;

        public IndexedOnlyCommands(
            IReadOnlyList<MigrationCommand> commands
        )
        {
            _commands = commands;
        }

        public int Count => _commands.Count;
        public MigrationCommand this[int index] => _commands[index];

        public IEnumerator<MigrationCommand> GetEnumerator()
            => throw new InvalidOperationException("The prepared batch must be indexed without enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Records delegated provider execution without opening the fixture connection.</summary>
    private sealed class RecordingCommand : MigrationCommand
    {
        private readonly int _result;

        public RecordingCommand(
            IRelationalCommand command,
            DbContext context,
            MigrationsSqlGeneratorDependencies dependencies,
            int result,
            bool transactionSuppressed
        ) : base(command, context, dependencies.Logger, transactionSuppressed)
        {
            _result = result;
        }

        public int Executions { get; private set; }
        public IReadOnlyDictionary<string, object?>? ReceivedParameters { get; private set; }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null
        )
        {
            Executions++;
            ReceivedParameters = parameterValues;

            return _result;
        }
    }
}
