namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies fresh delayed scalar proofs, private binding scopes, and unchanged classifier branches.</summary>
public sealed class SqlServerScalarProofRegressionTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=scalar_proof_shapes;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    private static readonly int[] s_proofValues = [1, 0, 1];

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
            var type = variable == "@doka_layout" ? "nvarchar(128)" : "int";
            var declaration = "DECLARE " + variable + " " + type + "; EXEC sys.sp_executesql N'SELECT @doka_proof";
            var declarationIndex = template.IndexOf(declaration, StringComparison.Ordinal);
            var outputIndex = template.IndexOf("@doka_proof = " + variable + " OUTPUT;", StringComparison.Ordinal);
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
        Assert.Equal(5, Occurrences(template, "; ELSE BEGIN "));
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

        const string evidence = "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
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
            Assert.Contains("SELECT @doka_ordinal, N'unsupported', 0, 0, N'" + code + "', " + evidence,
                template, StringComparison.Ordinal);
        }

        Assert.Contains("SELECT @doka_ordinal, N'unsupported', 0, 0, @doka_layout, " + evidence,
            template, StringComparison.Ordinal);
        Assert.Contains("ELSE EXEC sys.sp_executesql N'SELECT @doka_ordinal, "
            + "CASE WHEN COALESCE((1), 0) = 1 THEN (N''different'') ELSE N''prerequisite_missing'' END, "
            + "0, 0, N''outer_guard_rejected'', " + evidence + ";'", template, StringComparison.Ordinal);
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
            Assert.Contains(parameter.Name + " = @doka_source0_"
                + parameter.Name["@doka_value".Length..], dispatch, StringComparison.Ordinal);
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
    /// <returns>The asynchronous verification operation.</returns>
    internal static async Task VerifyLocalAsync(SqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Assert.Equal(ConnectionState.Open, connection.State);
        using var context = new SafeMigrationDbContext(connection.ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();

        await VerifyNameAndCollationGatesAsync(connection);
        await VerifySupportRejectionsAsync(connection);
        await VerifyLayoutAndOuterFallbackAsync(connection);
        await VerifyFreshSourcesAndPrivateScopesAsync(connection, mappings);
        await VerifyMaximumSourcesAsync(connection, mappings);
        await VerifyScalarCancellationAsync(connection);
    }

    /// <summary>Skipped classifier and default scopes must not bind missing objects or invalid collations.</summary>
    private static async Task VerifyNameAndCollationGatesAsync(SqlConnection connection)
    {
        const string missingState = "(SELECT TOP (1) N'matching' FROM dbo.doka_scalar_proof_missing_object)";
        var absent = Plan() with { PrerequisiteExpression = "0", StateExpression = missingState };
        await AssertResultAsync(connection, absent, 11, "prerequisite_missing", null);

        const string invalidCollation = "CASE WHEN N'x' COLLATE doka_scalar_proof_invalid_collation = N'x' "
            + "THEN 1 ELSE 0 END";
        var collation = GuardedPlan() with
        {
            ColumnCollationSupportExpression = "0",
            DefaultValueSupportExpression = invalidCollation,
            DefaultValueSupportRequiresDelayedBinding = true,
            StateExpression = "CASE WHEN (" + invalidCollation + ") = 1 THEN N'matching' ELSE N'different' END",
        };

        await AssertResultAsync(connection, collation, 12, "unsupported",
            SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode);
    }

    /// <summary>False or NULL support proofs reject before compiling later proofs.</summary>
    private static async Task VerifySupportRejectionsAsync(SqlConnection connection)
    {
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

                await AssertResultAsync(connection, plan, 13, "unsupported", code);
            }
        }
    }

    /// <summary>Layout outputs retain their declared width, and outer guard failures retain state and code.</summary>
    private static async Task VerifyLayoutAndOuterFallbackAsync(SqlConnection connection)
    {
        var layoutCode = "layout_" + new string('x', 150);
        var layout = GuardedPlan() with
        {
            ColumnLayoutFailureExpression = "N'" + layoutCode + "'",
            ColumnCollationSupportExpression = "(SELECT missing FROM dbo.doka_scalar_proof_missing_object)",
        };
        await AssertResultAsync(connection, layout, 14, "unsupported", layoutCode[..128]);
        await AssertResultAsync(connection, GuardedPlan(), 15, "matching", null);

        var outer = Plan() with
        {
            AnalysisOuterStateGuardExpression = "0",
            AnalysisOuterStateGuardFailureExpression = "N'different'",
            PrerequisiteFailureCodeExpression = "N'outer_guard_rejected'",
            StateExpression = "(SELECT TOP (1) N'matching' FROM dbo.doka_scalar_proof_missing_object)",
        };

        await AssertResultAsync(connection, outer, 16, "different", "outer_guard_rejected");
    }

    /// <summary>Native dispatchers re-evaluate equal templates with private preambles and max sources.</summary>
    private static async Task VerifyFreshSourcesAndPrivateScopesAsync(
        SqlConnection connection,
        IRelationalTypeMappingSource mappings
    )
    {
        Assert.True(connection.CanCreateBatch);
        var numeric = s_proofValues.Select((value, ordinal) => BoundPlan(mappings, ordinal, value) with
        {
            PhysicalTableSupportExpression = "CASE WHEN @doka_value0 = 1 THEN 1 ELSE 0 END",
            CatalogPreambleSql = "DECLARE @private_proof int = @doka_value0;",
            StateExpression = "CASE WHEN @private_proof = 1 THEN N'matching' ELSE N'different' END",
        }).ToArray();

        Assert.Single(numeric.Select(SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate).Distinct());
        var numericResults = new SafeMigrationProviderAnalysis[numeric.Length];
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 10, numeric, 0, numericResults, CancellationToken.None);
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
                    + sourceHex + " THEN 1 ELSE 0 END",
            }).ToArray();

        Assert.Single(unicode.Select(SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate).Distinct());
        Assert.All(unicode, plan => Assert.Equal("nvarchar(max)",
            Assert.Single(plan.AnalysisParameters).Mapping.StoreType));
        var unicodeResults = new SafeMigrationProviderAnalysis[unicode.Length];
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 10, unicode, 0, unicodeResults, CancellationToken.None);
        Assert.Equal(SafeMigrationObservedState.Matching, unicodeResults[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, unicodeResults[1].ObservedState);
        Assert.Equal(SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode, unicodeResults[1].Code);
        Assert.Equal(SafeMigrationObservedState.Matching, unicodeResults[2].ObservedState);

        // WHY: The analyzer already rejects any extra, empty, or non-nine-column result set. Direct reads
        // additionally pin the raw engine shape, independently of the provider's result projection.
        await AssertResultAsync(connection, numeric[0], 17, "matching", null);
        await AssertResultAsync(connection, numeric[1], 18, "unsupported",
            SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode);
    }

    /// <summary>Every source participates in a real boundary proof, including the final source.</summary>
    private static async Task VerifyMaximumSourcesAsync(
        SqlConnection connection,
        IRelationalTypeMappingSource mappings
    )
    {
        var plan = MaximumSourcePlan(mappings);
        await AssertResultAsync(connection, plan, 19, "matching", null);
        var changed = plan with
        {
            AnalysisParameters = plan.AnalysisParameters.Select((parameter, index) =>
                index == 1_999 ? parameter with { Value = -1 } : parameter).ToArray(),
        };

        await AssertResultAsync(connection, changed, 20, "unsupported",
            SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode);
    }

    /// <summary>Cancels a running scalar metadata query with one owned session and no time-delay SQL.</summary>
    private static async Task VerifyScalarCancellationAsync(SqlConnection connection)
    {
        var plan = Plan() with
        {
            PhysicalTableSupportExpression = "(SELECT CASE WHEN SUM(CONVERT(bigint, "
                + "CHECKSUM(a.object_id, b.object_id, c.object_id, d.object_id, e.object_id))) = 0 THEN 1 ELSE 0 END "
                + "FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c "
                + "CROSS JOIN sys.all_objects d CROSS JOIN sys.all_objects e)",
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var results = new SafeMigrationProviderAnalysis[1];
        var stopwatch = Stopwatch.StartNew();
        // WHY: Exercise the actual native capture path. An informational prefix can make SqlClient
        // return a reader before scalar execution; synchronously reading its metadata then loses the
        // ExecuteReaderAsync cancellation scope and tests a different path from catalog capture.
        var failure = await Record.ExceptionAsync(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, 10, [plan], 0, results, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        if (failure is SqlException sqlFailure)
        {
            // WHY: Native SqlBatch can surface acknowledged server cancellation as SqlException(0).
            // Retain that driver behavior, but never accept timeout (-2), binding or other SQL errors.
            Assert.Equal(0, sqlFailure.Number);
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }

        Assert.Null(results[0]);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1;";
        probe.CommandTimeout = 5;
        Assert.Equal(1, await probe.ExecuteScalarAsync());
    }

    /// <summary>Executes one complete classifier and checks its single owned raw result.</summary>
    private static async Task AssertResultAsync(
        SqlConnection connection,
        SqlServerSafeMigrationRuntimePlan plan,
        int ordinal,
        string state,
        string? code
    )
    {
        var actual = await ReadRawResultAsync(connection, plan, ordinal, CancellationToken.None);
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
            command.Parameters.Add(parameter.Mapping.CreateParameter(command, parameter.ExternalName,
                parameter.Value, nullable: false));
        }

        Assert.Equal(plan.AnalysisParameters.Count, command.Parameters.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.Equal(9, reader.FieldCount);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var result = new ScalarResult(reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(4) ? null : reader.GetString(4));
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
    private static SqlServerSafeMigrationRuntimePlan MaximumSourcePlan(IRelationalTypeMappingSource mappings)
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
    private static int Occurrences(string text, string token)
        => text.Split(token, StringSplitOptions.None).Length - 1;

    /// <summary>Carries raw engine evidence independently of provider result projection.</summary>
    /// <param name="Ordinal">The original classifier ordinal.</param>
    /// <param name="State">The raw observed-state label.</param>
    /// <param name="Code">The raw nullable classification code.</param>
    private sealed record ScalarResult(int Ordinal, string State, string? Code);
}
