namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies statement-local classifier text sharing without sharing source values or live evidence.</summary>
public sealed class SqlServerSharedCatalogTemplateTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=shared_catalog_templates;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>A full statement shares one body while retaining every source, ordinal, and result.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EqualTemplatesWithDifferentValues_RetainIndependentBindingsAndResults(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        const int count = 32;
        const int captureStart = 101;
        var plans = Enumerable.Range(0, count).Select(index => ParameterizedPlan(mappings, index, index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[captureStart + count];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, captureStart, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.Equal(1, DeclarationCount(statement));
        Assert.Equal(count, InvocationCount(statement));
        Assert.DoesNotContain("@doka_template1", statement, StringComparison.Ordinal);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Equal(64, parameters.Length);
        Assert.Equal(parameters.Length, parameters.Select(parameter => parameter.ParameterName).Distinct().Count());
        for (var index = 0; index < count; index++)
        {
            var slot = index.ToString(CultureInfo.InvariantCulture);
            var ordinal = Assert.Single(parameters, parameter => parameter.ParameterName == "@doka_ordinal" + slot);
            var source = Assert.Single(parameters,
                parameter => parameter.ParameterName == "@doka_source" + slot + "_0");
            Assert.Equal(captureStart + index, Assert.IsType<int>(ordinal.Value));
            Assert.Equal(index, Assert.IsType<int>(source.Value));
            Assert.Contains("@doka_ordinal = @doka_ordinal" + slot
                + ", @doka_value0 = @doka_source" + slot + "_0;", statement, StringComparison.Ordinal);
            Assert.Equal(State(captureStart + index), results[captureStart + index].ObservedState);
        }

        Assert.All(results.Take(captureStart), Assert.Null);
        Assert.Equal(count, connection.RowsRead);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandExecutions);
    }

    /// <summary>Equal state text cannot merge classifiers with different lossless source declarations.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EqualStateTextWithDifferentDefinitions_DeclaresSeparateTemplates(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        SqlServerSafeMigrationRuntimePlan?[] plans =
        [
            ParameterizedPlan(mappings, 0, 1),
            ParameterizedPlan(mappings, 1, 1m),
            ParameterizedPlan(mappings, 2, 1.00m),
        ];

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.Equal(3, DeclarationCount(statement));
        Assert.Contains("EXEC sys.sp_executesql @doka_template0, N'@doka_ordinal int, @doka_value0 int'",
            statement, StringComparison.Ordinal);
        Assert.Contains("EXEC sys.sp_executesql @doka_template1, N'@doka_ordinal int, @doka_value0 decimal(38,0)'",
            statement, StringComparison.Ordinal);
        Assert.Contains("EXEC sys.sp_executesql @doka_template2, N'@doka_ordinal int, @doka_value0 decimal(38,2)'",
            statement, StringComparison.Ordinal);
        Assert.Single(plans.Select(plan => plan!.StateExpression).Distinct());
        var parameters = Assert.Single(connection.RecordedParameters);
        var integer = Assert.Single(parameters, parameter => parameter.ParameterName == "@doka_source0_0");
        var decimalWhole = Assert.Single(parameters, parameter => parameter.ParameterName == "@doka_source1_0");
        var decimalFine = Assert.Single(parameters, parameter => parameter.ParameterName == "@doka_source2_0");
        Assert.Equal(SqlDbType.Int, integer.SqlDbType);
        Assert.Equal(SqlDbType.Decimal, decimalWhole.SqlDbType);
        Assert.Equal(SqlDbType.Decimal, decimalFine.SqlDbType);
        Assert.Equal((byte)38, decimalWhole.Precision);
        Assert.Equal((byte)38, decimalFine.Precision);
        Assert.Equal((byte)0, decimalWhole.Scale);
        Assert.Equal((byte)2, decimalFine.Scale);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State), results.Select(result => result.ObservedState));
        Assert.Equal(3, connection.RowsRead);
    }

    /// <summary>The next statement redeclares its body and restarts only its private transport namespace.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThirtyThreeValueDistinctPlans_ResetStatementLocalNamespaces(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        const int count = 33;
        const int captureStart = 41;
        var plans = Enumerable.Range(0, count).Select(index => ParameterizedPlan(mappings, index, index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[captureStart + count];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, captureStart, results, CancellationToken.None);

        // Assert
        Assert.Equal(2, connection.RecordedStatements.Count);
        Assert.All(connection.RecordedStatements, statement =>
        {
            Assert.StartsWith("DECLARE @doka_template0 nvarchar(max)", statement, StringComparison.Ordinal);
            Assert.Equal(1, DeclarationCount(statement));
            Assert.DoesNotContain("@doka_template1", statement, StringComparison.Ordinal);
        });

        Assert.Equal(32, InvocationCount(connection.RecordedStatements[0]));
        Assert.Equal(1, InvocationCount(connection.RecordedStatements[1]));
        Assert.Equal(64, connection.RecordedParameters[0].Length);
        var secondParameters = connection.RecordedParameters[1];
        Assert.Equal(2, secondParameters.Length);
        Assert.Equal(captureStart + 32, Assert.Single(secondParameters,
            parameter => parameter.ParameterName == "@doka_ordinal0").Value);
        Assert.Equal(32, Assert.Single(secondParameters,
            parameter => parameter.ParameterName == "@doka_source0_0").Value);
        Assert.Contains("@doka_ordinal = @doka_ordinal0, @doka_value0 = @doka_source0_0;",
            connection.RecordedStatements[1], StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source32_0", connection.RecordedStatements[1], StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(captureStart, count).Select(State),
            results.Skip(captureStart).Select(result => result.ObservedState));
        Assert.Equal(count, connection.RowsRead);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 2, connection.CommandExecutions);
    }

    /// <summary>Shared text preserves both dynamic literal escape levels and complete Unicode characters.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuotedUnicodeTemplate_IsDeclaredExactlyOnceWithoutChangingItsBody(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var first = ParameterizedPlan(mappings, 0, 7) with
        {
            StateExpression = "N'missing' /* O'Brien\u20ac\ud83d\ude00 */",
            CatalogPreambleSql = "DECLARE @proof nvarchar(30) = N'O''Brien\u20ac\ud83d\ude00';",
        };

        var second = first with { AnalysisParameters = ParameterizedPlan(mappings, 1, 8).AnalysisParameters };
        var expected = Declaration(first) + Invocation(0, "int") + "\n" + Invocation(1, "int");
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [first, second], 0, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.Equal(expected, statement);
        Assert.Contains("O''''Brien\u20ac\ud83d\ude00", statement, StringComparison.Ordinal);
        Assert.Contains("N''''O''''''''Brien\u20ac\ud83d\ude00''''", statement, StringComparison.Ordinal);
        Assert.Equal(1, DeclarationCount(statement));
        Assert.Equal(2, InvocationCount(statement));
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), Encoding.UTF8.GetByteCount(statement));
        Assert.Equal(2, connection.RowsRead);
        Assert.Equal(State(0), results[0].ObservedState);
        Assert.Equal(State(1), results[1].ObservedState);
    }

    /// <summary>Sharing charges quoted UTF-8 text once but still charges each complete source payload.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    /// <param name="sourceCharacters">The width of each independently bound Unicode source.</param>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 350_000)]
    [InlineData(false, 350_000)]
    public async Task SharedQuotedUnicodeBody_UsesExactCombinedPayloadBudget(
        bool nativeBatch,
        int sourceCharacters
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plans = Enumerable.Range(0, 2).Select(index => ParameterizedPlan(mappings, index,
            new string('x', sourceCharacters) + index.ToString(CultureInfo.InvariantCulture)) with
        {
            StateExpression = LargeQuotedUnicodeState(),
        }).ToArray();

        var expectedShared = Declaration(plans[0]) + Invocation(0, "nvarchar(max)")
            + "\n" + Invocation(1, "nvarchar(max)");
        var completeSharedBytes = Encoding.UTF8.GetByteCount(expectedShared)
            + plans.Sum(plan => plan.AnalysisParameters.Sum(parameter => parameter.PayloadBytes) + 128);
        var expectedExecutions = sourceCharacters == 1 ? 1 : 2;
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(expectedExecutions, connection.RecordedStatements.Count);
        Assert.Equal(nativeBatch ? expectedExecutions : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : expectedExecutions, connection.CommandExecutions);
        Assert.Equal(sourceCharacters == 1,
            completeSharedBytes <= SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        for (var index = 0; index < connection.RecordedStatements.Count; index++)
        {
            var statement = connection.RecordedStatements[index];
            var ordinals = connection.RecordedParameters[index]
                .Where(parameter => parameter.ParameterName.StartsWith("@doka_ordinal", StringComparison.Ordinal))
                .Select(parameter => Assert.IsType<int>(parameter.Value)).ToArray();
            var payload = Encoding.UTF8.GetByteCount(statement) + ordinals.Sum(ordinal =>
                plans[ordinal].AnalysisParameters.Sum(parameter => parameter.PayloadBytes) + 128);

            Assert.Equal(1, DeclarationCount(statement));
            Assert.True(Encoding.UTF8.GetByteCount(statement) > 3_000_000);
            Assert.InRange(payload, 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        }

        Assert.Equal(2, connection.RowsRead);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State), results.Select(result => result.ObservedState));
    }

    /// <summary>An admitted shared body cannot hide an individually oversized later classifier.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedSecondSource_CannotUseSharingToBypassIndividualAdmission(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var first = ParameterizedPlan(mappings, 0, "small") with { StateExpression = LargeQuotedUnicodeState() };
        var second = first with
        {
            AnalysisParameters = ParameterizedPlan(mappings, 1, new string('x', 600_000)).AnalysisParameters,
        };

        var standaloneBytes = Encoding.UTF8.GetByteCount(
            SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(1, second, 1))
            + second.AnalysisParameters.Sum(parameter => parameter.PayloadBytes) + 128;
        var sharedInvocationBytes = Encoding.UTF8.GetByteCount(Invocation(1, "nvarchar(max)"))
            + second.AnalysisParameters.Sum(parameter => parameter.PayloadBytes) + 128;
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [first, second], 0, results, CancellationToken.None));

        // Assert
        Assert.True(standaloneBytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.InRange(sharedInvocationBytes, 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.Contains("operation 1 exceeds a bounded query limit", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions + connection.CommandExecutions);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.All(results, Assert.Null);
    }

    /// <summary>Captures one lossless source under the same local classifier text for value-distinct plans.</summary>
    private static SqlServerSafeMigrationRuntimePlan ParameterizedPlan(
        IRelationalTypeMappingSource mappings,
        int ordinal,
        object value
    )
    {
        var bindings = new SqlServerCatalogParameterBindings(mappings, ordinal);
        bindings.Add(value);

        return new SqlServerSafeMigrationRuntimePlan(
            "CASE WHEN @doka_value0 IS NULL THEN N'missing' ELSE N'matching' END",
            "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
            AnalysisParameters = bindings.Values,
        };
    }

    /// <summary>Supplies text whose nested quote escaping and UTF-8 bytes dominate its character count.</summary>
    private static string LargeQuotedUnicodeState()
        => "N'missing' /*" + new string('\'', 430_000) + new string('\u20ac', 430_000) + "*/";

    /// <summary>Builds the exact statement-local Unicode literal expected for a complete classifier body.</summary>
    private static string Declaration(SqlServerSafeMigrationRuntimePlan plan)
        => "DECLARE @doka_template0 nvarchar(max) = N'"
            + SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan)
                .Replace("'", "''", StringComparison.Ordinal) + "';\n";

    /// <summary>Names the ordinal and source parameters belonging to one statement-local dispatcher slot.</summary>
    private static string Invocation(int slot, string sourceType)
    {
        var suffix = slot.ToString(CultureInfo.InvariantCulture);

        return "EXEC sys.sp_executesql @doka_template0, N'@doka_ordinal int, @doka_value0 "
            + sourceType + "', @doka_ordinal = @doka_ordinal" + suffix
            + ", @doka_value0 = @doka_source" + suffix + "_0;";
    }

    /// <summary>Counts complete outer template declarations rather than dynamic setup variables.</summary>
    private static int DeclarationCount(string statement)
        => statement.Split("DECLARE @doka_template", StringSplitOptions.None).Length - 1;

    /// <summary>Counts only outer template dispatchers, excluding the nested guarded classifier invocation.</summary>
    private static int InvocationCount(string statement)
        => statement.Split("EXEC sys.sp_executesql @doka_template", StringSplitOptions.None).Length - 1;

    /// <summary>Mirrors the recording connection's original-ordinal classification contract.</summary>
    private static SafeMigrationObservedState State(int ordinal) => (ordinal % 3) switch
    {
        0 => SafeMigrationObservedState.Missing,
        1 => SafeMigrationObservedState.Matching,
        2 => SafeMigrationObservedState.Different,
        _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
    };
}
