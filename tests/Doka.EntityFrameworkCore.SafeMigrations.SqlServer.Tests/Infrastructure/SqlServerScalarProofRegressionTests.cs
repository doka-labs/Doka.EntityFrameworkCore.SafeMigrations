namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies fresh delayed scalar proofs, private binding scopes, and unchanged classifier branches.</summary>
public sealed class SqlServerScalarProofRegressionTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=scalar_proof_shapes;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    private static readonly int[] s_proofValues = [1, 0, 1];

    /// <summary>Preserves caller cancellation without letting injected capture cancellation abort its caller.</summary>
    /// <param name="cancelCaller">Whether cancellation originates at the caller or the injected capture scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScalarCancellationScopeRetainsOneWayCallerLink(
        bool cancelCaller
    )
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        using var injected = CreateScalarCancellationSource(caller.Token);

        // Act
        if (cancelCaller)
        {
            caller.Cancel();
        }
        else
        {
            injected.Cancel();
        }

        // Assert
        Assert.True(injected.IsCancellationRequested);
        Assert.Equal(cancelCaller, caller.IsCancellationRequested);
        Assert.NotEqual(caller.Token, injected.Token);
    }

    /// <summary>The encoded continuation payload is charged before a delayed classifier is admitted.</summary>
    [Fact]
    public void ContinuationExpansion_RejectsAnOversizedFinalPayload()
    {
        // Arrange
        var state = "N'matching' /*" + string.Concat(Enumerable.Repeat("\\\n", 300_000)) + "*/";
        var rawBytes = Encoding.UTF8.GetByteCount(state);
        var plan = Plan() with { StateExpression = state };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(7, plan));

        // Assert
        Assert.True(rawBytes < SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.Contains(
            "exceeds a bounded query limit",
            Assert.IsType<InvalidOperationException>(failure)
                .Message,
            StringComparison.Ordinal);
    }

    /// <summary>A fitting encoded classifier retains its exact declared payload and original wire ordinal.</summary>
    [Fact]
    public void ContinuationExpansion_AdmitsAFittingFinalPayload()
    {
        // Arrange
        var state = "N'matching' /*" + string.Concat(Enumerable.Repeat("\\\n", 32)) + "*/";
        var plan = Plan() with { StateExpression = state };

        // Act
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(7, plan);

        // Assert
        Assert.True(Encoding.UTF8.GetByteCount(selection) < SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.Contains("NCHAR(10)", selection, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal = 7", selection, StringComparison.Ordinal);
    }

    /// <summary>Every proof writes a fresh local output before its original support or prerequisite gate.</summary>
    [Fact]
    public void ScalarProofs_DeclareFreshOutputsInOriginalGateOrder()
    {
        // Arrange
        var plan = GuardedPlan();
        string[] variables =
        [
            "@doka_physical", "@doka_layout", "@doka_collation",
            "@doka_default", "@doka_filter", "@doka_prerequisite",
        ];

        // Act
        var template = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);

        // Assert
        var previousGate = -1;
        foreach (var variable in variables)
        {
            var type = variable == "@doka_layout"
                ? "nvarchar(128)"
                : "int";

            var declaration = "DECLARE " + variable + " " + type + "; EXEC sys.sp_executesql\nN'SELECT @doka_proof";
            var declarationIndex = template.IndexOf(declaration, StringComparison.Ordinal);
            var outputIndex = template.IndexOf("@doka_proof =\n" + variable + " OUTPUT;", StringComparison.Ordinal);
            var gate = variable switch
            {
                "@doka_layout" => "IF @doka_layout IS NOT NULL",
                "@doka_prerequisite" => "IF @doka_prerequisite = 1",
                _ => "IF COALESCE(" + variable + ", 0) <> 1",
            };

            var gateIndex = template.IndexOf(gate, StringComparison.Ordinal);
            Assert.True(declarationIndex > previousGate);
            Assert.True(outputIndex > declarationIndex);
            Assert.True(gateIndex > outputIndex);
            Assert.Equal(1, Occurrences(template, "DECLARE " + variable + " " + type + ";"));
            previousGate = gateIndex;
        }

        Assert.Equal(6, Occurrences(template, "SELECT @doka_proof ="));
        Assert.Equal(5, Occurrences(template, "@doka_proof int OUTPUT'"));
        Assert.Equal(1, Occurrences(template, "@doka_proof nvarchar(128) OUTPUT'"));
        Assert.Equal(5, Occurrences(template, "ELSE BEGIN "));
        Assert.EndsWith(" END; END; END; END; END;", template, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT EXEC", template, StringComparison.Ordinal);
        Assert.DoesNotContain("DECLARE @doka_physical int =", template, StringComparison.Ordinal);
    }

    /// <summary>Support rejection and layout fallback retain the complete nine-column result contract.</summary>
    [Fact]
    public void ScalarProofFallbacks_RetainNineColumnsAndSupportCodes()
    {
        // Arrange
        var plan = GuardedPlan() with
        {
            AnalysisOuterStateGuardExpression = "0",
            AnalysisOuterStateGuardFailureExpression = "N'different'",
            PrerequisiteFailureCodeExpression = "N'outer_guard_rejected'",
        };

        const string evidence = "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL),\n"
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL)";

        string[] codes =
        [
            SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode,
            SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode,
            SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode,
            SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode,
        ];

        // Act
        var template = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);

        // Assert
        foreach (var code in codes)
        {
            Assert.Contains(
                "SELECT @doka_ordinal,\nN'unsupported', 0, 0, N'" + code + "',\n" + evidence,
                template,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            """
            SELECT @doka_ordinal, N'unsupported',
            0, 0, @doka_layout, CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max),
            NULL), CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL);
            """,
            template,
            StringComparison.Ordinal);

        Assert.Contains(
            """
            ELSE EXEC sys.sp_executesql N'SELECT @doka_ordinal, CASE WHEN COALESCE((1), 0) = 1
            THEN (N''different'')
            ELSE N''prerequisite_missing'' END, 0, 0, N''outer_guard_rejected'',
            CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max),
            NULL), CONVERT(nvarchar(128), NULL);'
            """,
            template,
            StringComparison.Ordinal);

        Assert.Contains("DECLARE @doka_layout nvarchar(128);", template, StringComparison.Ordinal);
    }

    /// <summary>All 2,000 sources reach the 2,002-parameter internal proof without an extra wire ordinal.</summary>
    [Fact]
    public void MaximumSourceBoundary_ForwardsEverySourceAndProofOutput()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var plan = MaximumSourcePlan(mappings);

        // Act
        var template = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);
        var dispatch = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(57, plan, 0);

        // Assert
        Assert.Equal(2_000, plan.AnalysisParameters.Count);
        Assert.Contains("@doka_value1999 int, @doka_proof int OUTPUT'", template, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal = 57, @doka_value0 = @doka_source0_0", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_ordinal = @doka_ordinal0", dispatch, StringComparison.Ordinal);
        foreach (var parameter in plan.AnalysisParameters)
        {
            Assert.Contains(parameter.Name + " = " + parameter.Name + ",", template, StringComparison.Ordinal);
            Assert.Contains(
                parameter.Name
                + " = @doka_source0_"
                + parameter.Name["@doka_value".Length..],
                dispatch,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Proof statements never consume or move the preamble's classifier-private variables.</summary>
    [Fact]
    public void PreambleVariables_RemainInsideTheInnerClassifierScope()
    {
        // Arrange
        var plan = GuardedPlan() with
        {
            CatalogPreambleSql = "DECLARE @private_proof int = 17;",
            StateExpression = "CASE WHEN @private_proof = 17 THEN N'matching' ELSE N'different' END",
        };

        // Act
        var template = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);

        // Assert
        var inner = template.IndexOf("EXEC sys.sp_executesql N'DECLARE @private_proof", StringComparison.Ordinal);
        var fallback = template.IndexOf("ELSE EXEC sys.sp_executesql", inner, StringComparison.Ordinal);
        Assert.True(inner > template.IndexOf("IF @doka_prerequisite = 1", StringComparison.Ordinal));
        Assert.True(fallback > inner);
        Assert.DoesNotContain("@private_proof", template[..inner], StringComparison.Ordinal);
        Assert.DoesNotContain("@private_proof", template[fallback..], StringComparison.Ordinal);
        Assert.Contains("DECLARE @private_proof int = 17;\nSELECT @doka_ordinal", template, StringComparison.Ordinal);
    }

    /// <summary>Runs every engine contract on an open connection to a caller-owned isolated database.</summary>
    /// <param name="connection">The unique owned connection, which this helper neither closes nor disposes.</param>
    /// <param name="connectionString">
    /// The same database's connection string for independent cancellation observation.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    /// <returns>The asynchronous verification operation.</returns>
    internal static async Task VerifyLocalAsync(
        SqlConnection connection,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(connection);
        Assert.Equal(ConnectionState.Open, connection.State);
        await using var context = new SafeMigrationDbContext(connection.ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();

        await VerifyNameAndCollationGatesAsync(connection, cancellationToken: cancellationToken);
        await VerifySupportRejectionsAsync(connection, cancellationToken: cancellationToken);
        await VerifyLayoutAndOuterFallbackAsync(connection, cancellationToken: cancellationToken);
        await VerifyFreshSourcesAndPrivateScopesAsync(connection, mappings, cancellationToken: cancellationToken);
        await VerifyMaximumSourcesAsync(connection, mappings, cancellationToken: cancellationToken);
        await VerifyScalarCancellationAsync(connection, connectionString, cancellationToken);
    }

    /// <summary>Skipped classifier and default scopes must not bind missing objects or invalid collations.</summary>
    /// <param name="connection">The caller-owned open session on which skipped-scope binding is tested.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifyNameAndCollationGatesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string missingState = "(SELECT TOP (1) N'matching' FROM dbo.doka_scalar_proof_missing_object)";
        var absent = Plan() with
        {
            PrerequisiteExpression = "0",
            StateExpression = missingState,
        };

        await AssertResultAsync(
            connection,
            absent,
            11,
            "prerequisite_missing",
            null,
            cancellationToken: cancellationToken);

        const string invalidCollation = "CASE WHEN N'x' COLLATE doka_scalar_proof_invalid_collation = N'x' "
            + "THEN 1 ELSE 0 END";

        var collation = GuardedPlan() with
        {
            ColumnCollationSupportExpression = "0",
            DefaultValueSupportExpression = invalidCollation,
            DefaultValueSupportRequiresDelayedBinding = true,
            StateExpression = "CASE WHEN (" + invalidCollation + ") = 1 THEN N'matching' ELSE N'different' END",
        };

        await AssertResultAsync(
            connection,
            collation,
            12,
            "unsupported",
            SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode,
            cancellationToken: cancellationToken);
    }

    /// <summary>False or NULL support proofs reject before compiling later proofs.</summary>
    /// <param name="connection">The caller-owned open session used to execute the support-rejection plans.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifySupportRejectionsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string invalid = "(SELECT TOP (1) missing FROM dbo.doka_scalar_proof_missing_object)";
        string[] kinds = ["physical", "collation", "default", "filter"];
        foreach (var kind in kinds)
        {
            foreach (var value in new[] { "0", "NULL" })
            {
                var plan = GuardedPlan() with { StateExpression = invalid };
                plan = kind switch
                {
                    "physical" => plan with
                    {
                        PhysicalTableSupportExpression = value,
                        ColumnLayoutFailureExpression = invalid,
                        ColumnCollationSupportExpression = invalid,
                        DefaultValueSupportExpression = invalid,
                        IndexFilterSupportExpression = invalid,
                    },
                    "collation" => plan with
                    {
                        ColumnCollationSupportExpression = value,
                        DefaultValueSupportExpression = invalid,
                        IndexFilterSupportExpression = invalid,
                    },
                    "default" => plan with
                    {
                        DefaultValueSupportExpression = value,
                        IndexFilterSupportExpression = invalid,
                    },
                    "filter" => plan with { IndexFilterSupportExpression = value },
                    _ => throw new UnreachableException(),
                };

                var code = kind switch
                {
                    "physical" => SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode,
                    "collation" => SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode,
                    "default" => SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode,
                    "filter" => SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode,
                    _ => throw new UnreachableException(),
                };

                await AssertResultAsync(
                    connection,
                    plan,
                    13,
                    "unsupported",
                    code,
                    cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>Layout outputs retain their declared width, and outer guard failures retain state and code.</summary>
    /// <param name="connection">The caller-owned open session used to inspect layout and outer-guard results.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifyLayoutAndOuterFallbackAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var layoutCode = "layout_" + new string('x', 150);
        var layout = GuardedPlan() with
        {
            ColumnLayoutFailureExpression = "N'" + layoutCode + "'",
            ColumnCollationSupportExpression = "(SELECT missing FROM dbo.doka_scalar_proof_missing_object)",
        };

        await AssertResultAsync(
            connection,
            layout,
            14,
            "unsupported",
            layoutCode[..128],
            cancellationToken: cancellationToken);

        await AssertResultAsync(connection, GuardedPlan(), 15, "matching", null, cancellationToken: cancellationToken);

        var outer = Plan() with
        {
            AnalysisOuterStateGuardExpression = "0",
            AnalysisOuterStateGuardFailureExpression = "N'different'",
            PrerequisiteFailureCodeExpression = "N'outer_guard_rejected'",
            StateExpression = "(SELECT TOP (1) N'matching' FROM dbo.doka_scalar_proof_missing_object)",
        };

        await AssertResultAsync(
            connection,
            outer,
            16,
            "different",
            "outer_guard_rejected",
            cancellationToken: cancellationToken);
    }

    /// <summary>Native dispatchers re-evaluate equal templates with private preambles and max sources.</summary>
    /// <param name="connection">The caller-owned batch-capable session used for native catalog captures.</param>
    /// <param name="mappings">The provider mappings used to bind distinct numeric and Unicode source values.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifyFreshSourcesAndPrivateScopesAsync(
        SqlConnection connection,
        IRelationalTypeMappingSource mappings,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        Assert.True(connection.CanCreateBatch);
        var numeric = s_proofValues
            .Select((value, ordinal) => BoundPlan(mappings, ordinal, value) with
            {
                PhysicalTableSupportExpression = "CASE WHEN @doka_value0 = 1 THEN 1 ELSE 0 END",
                CatalogPreambleSql = "DECLARE @private_proof int = @doka_value0;",
                StateExpression = "CASE WHEN @private_proof = 1 THEN N'matching' ELSE N'different' END",
            })
            .ToArray();

        Assert.Single(numeric
            .Select(SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate)
            .Distinct());

        var numericResults = new SafeMigrationProviderAnalysis[numeric.Length];
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection,
            null,
            10,
            numeric,
            0,
            numericResults,
            cancellationToken);

        Assert.Equal(SafeMigrationObservedState.Matching, numericResults[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, numericResults[1].ObservedState);
        Assert.Equal(SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode, numericResults[1].Code);
        Assert.Equal(SafeMigrationObservedState.Matching, numericResults[2].ObservedState);

        var source = "O'Brien\u20ac\ud83d\ude00" + new string('x', 9_000) + "tail";
        var sourceHex = Convert.ToHexString(Encoding.Unicode.GetBytes(source));
        var unicode = new[] { source, source.Replace('\u20ac', 'x'), source }
            .Select((value, ordinal) => BoundPlan(mappings, ordinal, value) with
            {
                DefaultValueSupportExpression = "CASE WHEN CONVERT(varbinary(max), @doka_value0) = 0x"
                + sourceHex
                + " THEN 1 ELSE 0 END",
            })
            .ToArray();

        Assert.Single(unicode
            .Select(SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate)
            .Distinct());

        Assert.All(
            unicode,
            plan => Assert.Equal(
                "nvarchar(max)",
                Assert.Single(plan.AnalysisParameters)
                    .Mapping.StoreType));

        var unicodeResults = new SafeMigrationProviderAnalysis[unicode.Length];
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection,
            null,
            10,
            unicode,
            0,
            unicodeResults,
            cancellationToken);

        Assert.Equal(SafeMigrationObservedState.Matching, unicodeResults[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, unicodeResults[1].ObservedState);
        Assert.Equal(SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode, unicodeResults[1].Code);
        Assert.Equal(SafeMigrationObservedState.Matching, unicodeResults[2].ObservedState);

        // WHY: The analyzer already rejects any extra, empty, or non-nine-column result set. Direct reads
        // additionally pin the raw engine shape, independently of the provider's result projection.
        await AssertResultAsync(connection, numeric[0], 17, "matching", null, cancellationToken: cancellationToken);
        await AssertResultAsync(
            connection,
            numeric[1],
            18,
            "unsupported",
            SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode,
            cancellationToken: cancellationToken);
    }

    /// <summary>Every source participates in a real boundary proof, including the final source.</summary>
    /// <param name="connection">The caller-owned open session used to execute the maximum-source proof.</param>
    /// <param name="mappings">The provider mappings used to bind every source in the boundary plan.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifyMaximumSourcesAsync(
        SqlConnection connection,
        IRelationalTypeMappingSource mappings,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var plan = MaximumSourcePlan(mappings);
        await AssertResultAsync(connection, plan, 19, "matching", null, cancellationToken: cancellationToken);
        var changed = plan with
        {
            AnalysisParameters = plan
                .AnalysisParameters
                .Select((parameter, index) =>
                    index == 1_999
                        ? parameter with { Value = -1 }
                        : parameter)
                .ToArray(),
        };

        await AssertResultAsync(
            connection,
            changed,
            20,
            "unsupported",
            SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode,
            cancellationToken: cancellationToken);
    }

    /// <summary>Cancels an admitted scalar query held on an independently owned native table lock.</summary>
    /// <param name="connection">The caller-owned open session used to test native scalar cancellation.</param>
    /// <param name="connectionString">
    /// The original fixture settings, retained before SqlClient masks credentials.
    /// </param>
    /// <param name="cancellationToken">Cancels forward observation and links to the capture operation.</param>
    private static async Task VerifyScalarCancellationAsync(
        SqlConnection connection,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE dbo.scalar_cancellation_gate (id int NOT NULL);"
            + " INSERT dbo.scalar_cancellation_gate VALUES (1);";

        await setup.ExecuteNonQueryAsync(cancellationToken);
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await observer.BeginTransactionAsync(cancellationToken);
        await using var lockCommand = observer.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = "SELECT COUNT(*) FROM dbo.scalar_cancellation_gate WITH (TABLOCKX, HOLDLOCK);";
        await lockCommand.ExecuteScalarAsync(cancellationToken);

        var plan = Plan() with
        {
            // WHY: An explicit locking read cannot be optimized to a catalog-only constant or
            // completed before the observer sees it, even with read-committed snapshot enabled.
            PhysicalTableSupportExpression =
            "(SELECT COUNT(*) FROM dbo.scalar_cancellation_gate WITH (READCOMMITTEDLOCK))",
        };

        using var cancellation = CreateScalarCancellationSource(cancellationToken);
        var results = new SafeMigrationProviderAnalysis[1];
        var execution = SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection,
            null,
            10,
            [plan],
            0,
            results,
            cancellation.Token);

        SqlServerAttentionObservation observation;
        try
        {
            observation = await SqlServerAttentionTestOperation.ObserveAsync(
                execution,
                async callbackCancellationToken =>
                {
                    await using var admission = observer.CreateCommand();
                    admission.Transaction = transaction;
                    admission.CommandText = """
                    SELECT COUNT(*) FROM sys.dm_exec_requests
                    WHERE session_id = @producer AND blocking_session_id = @holder
                    AND wait_type LIKE N'LCK_M_%';
                    """;

                    admission.Parameters.AddWithValue("producer", connection.ServerProcessId);
                    admission.Parameters.AddWithValue("holder", observer.ServerProcessId);
                    while (!execution.IsCompleted)
                    {
                        if (await admission.ExecuteScalarAsync(callbackCancellationToken) is 1)
                        {
                            return;
                        }

                        // WHY: Poll cadence does not impose a machine-dependent qualification deadline.
                        await Task.Delay(10, callbackCancellationToken);
                    }

                    await execution;

                    throw new InvalidOperationException("The scalar proof completed before native lock admission.");
                },
                cancellation,
                cancellationToken);
        }
        finally
        {
            // WHY: The attention helper drains execution before its borrowed session or lock holder is released.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1;";
        var reused = await probe.ExecuteScalarAsync(cancellationToken);

        Assert.Null(observation.ReadinessFailure);
        Assert.Null(observation.CancellationFailure);
        Assert.True(cancellation.IsCancellationRequested);
        if (observation.ExecutionFailure is SqlException sqlFailure)
        {
            // WHY: Native SqlBatch can acknowledge attention as SqlException(0), never timeout(-2).
            Assert.Equal(0, sqlFailure.Number);
        }
        else
        {
            Assert.IsType<OperationCanceledException>(observation.ExecutionFailure, exactMatch: false);
        }

        Assert.Null(results[0]);
        Assert.Equal(1, reused);
    }

    /// <summary>Creates an independently cancelable capture scope that also observes caller cancellation.</summary>
    /// <param name="cancellationToken">The caller whose cancellation must reach the native capture.</param>
    /// <returns>The linked source whose disposal belongs to the caller.</returns>
    private static CancellationTokenSource CreateScalarCancellationSource(
            CancellationToken cancellationToken
        )

        // WHY: Preserve the independent capture scope without allowing its cancellation to cancel the caller.
        => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    /// <summary>Executes one complete classifier and checks its single owned raw result.</summary>
    /// <param name="connection">The caller-owned open session used to execute the delayed classifier.</param>
    /// <param name="plan">The complete runtime plan supplying classifier SQL and bound source parameters.</param>
    /// <param name="ordinal">The operation ordinal emitted by the classifier and required in its result.</param>
    /// <param name="state">The expected raw classifier state.</param>
    /// <param name="code">The expected raw diagnostic code, or null when no code is expected.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task AssertResultAsync(
        SqlConnection connection,
        SqlServerSafeMigrationRuntimePlan plan,
        int ordinal,
        string state,
        string? code,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var actual = await ReadRawResultAsync(connection, plan, ordinal, cancellationToken);
        Assert.Equal(ordinal, actual.Ordinal);
        Assert.Equal(state, actual.State);
        Assert.Equal(code, actual.Code);
    }

    /// <summary>Reads exactly one nine-column row with provider-created lossless source parameters.</summary>
    private static async Task<ScalarResult> ReadRawResultAsync(
        SqlConnection connection,
        SqlServerSafeMigrationRuntimePlan plan,
        int ordinal,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(ordinal, plan);
        command.CommandTimeout = 10;
        foreach (var parameter in plan.AnalysisParameters)
        {
            command.Parameters.Add(
                parameter.Mapping.CreateParameter(
                    command,
                    parameter.ExternalName,
                    parameter.Value,
                    nullable: false));
        }

        Assert.Equal(plan.AnalysisParameters.Count, command.Parameters.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.Equal(9, reader.FieldCount);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var result = new ScalarResult(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(4)
                ? null
                : reader.GetString(4));

        Assert.IsType<int>(reader.GetValue(2));
        Assert.IsType<int>(reader.GetValue(3));
        for (var column = 5; column < 9; column++)
        {
            Assert.True(reader.IsDBNull(column));
        }

        Assert.False(await reader.ReadAsync(cancellationToken));
        Assert.False(await reader.NextResultAsync(cancellationToken));

        return result;
    }

    /// <summary>Creates a synthetic complete classifier with no application-object dependency.</summary>
    private static SqlServerSafeMigrationRuntimePlan Plan()
        => new("N'matching'", "1", SafeMigrationRepairCapability.None, "0") { RequiresDelayedBinding = true };

    /// <summary>Enables all scalar gates while preserving a NULL layout failure and successful prerequisite.</summary>
    private static SqlServerSafeMigrationRuntimePlan GuardedPlan() => Plan() with
    {
        PhysicalTableSupportExpression = "1",
        ColumnLayoutFailureExpression = "CONVERT(nvarchar(128), NULL)",
        ColumnCollationSupportExpression = "1",
        DefaultValueSupportExpression = "1",
        DefaultValueSupportRequiresDelayedBinding = true,
        IndexFilterSupportExpression = "1",
    };

    /// <summary>Captures one lossless source with an ordinal-independent local marker.</summary>
    private static SqlServerSafeMigrationRuntimePlan BoundPlan(
        IRelationalTypeMappingSource mappings,
        int ordinal,
        object value
    )
    {
        var bindings = new SqlServerCatalogParameterBindings(mappings, ordinal);
        bindings.Add(value);

        return Plan() with { AnalysisParameters = bindings.Values };
    }

    /// <summary>Uses every admitted source in one support proof rather than forwarding unused placeholders.</summary>
    private static SqlServerSafeMigrationRuntimePlan MaximumSourcePlan(
        IRelationalTypeMappingSource mappings
    )
    {
        var bindings = new SqlServerCatalogParameterBindings(mappings, 0);
        var comparisons = new List<string>(2_000);
        for (var index = 0; index < 2_000; index++)
        {
            var marker = bindings.Add(index);
            comparisons.Add(marker + " = " + index.ToString(CultureInfo.InvariantCulture));
        }

        return Plan() with
        {
            AnalysisParameters = bindings.Values,
            DefaultValueSupportExpression = "CASE WHEN " + string.Join(" AND ", comparisons) + " THEN 1 ELSE 0 END",
        };
    }

    /// <summary>Counts an exact statement token without conflating different proof variables.</summary>
    private static int Occurrences(
        string text,
        string token
    ) => text.Split(token)
            .Length
        - 1;

    /// <summary>Carries raw engine evidence independently of provider result projection.</summary>
    /// <param name="Ordinal">The original classifier ordinal.</param>
    /// <param name="State">The raw observed-state label.</param>
    /// <param name="Code">The raw nullable classification code.</param>
    private sealed record ScalarResult(
        int Ordinal,
        string State,
        string? Code
    );
}
