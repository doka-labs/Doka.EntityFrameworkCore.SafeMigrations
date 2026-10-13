namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies bounded lazy-guard diagnostics without evaluating unavailable row-proof expressions.</summary>
public sealed class MySqlLazyStateGuardDiagnosticsTests
{
    /// <summary>Rejected lazy guards preserve provider codes without changing earlier rejection precedence.</summary>
    /// <param name="native">Whether transport uses native provider batches.</param>
    /// <param name="qualification">Whether the earlier result represents database identity qualification.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RejectedGuardPreservesCustomCodeAndEarlierState(bool native, bool qualification)
    {
        // Arrange
        const string code = "column_native_enum_mode_conflict";
        await using var context = CreateContext();
        var analyzer = (MySqlSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, _) => Results(sql, "unsupported", code));
        var prior = new MySqlSafeMigrationProviderAnalyzer.MySqlShortCircuitState(
            qualification ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.PrerequisiteMissing,
            qualification ? "database_qualifier_mismatch" : "classified_prerequisite_missing");
        MySqlSafeMigrationProviderAnalyzer.MySqlShortCircuitState?[] states = [prior, null];
        var plan = Plan() with
        {
            ClassificationCodeExpression = MySqlCatalogSqlTemplate.Marker(0),
            ParameterValues = [new MySqlCatalogParameterValue(code, null)],
        };

        // Act
        await analyzer.FindStateEvaluationGuardStatesAsync(connection, [plan, plan], states,
            16_384, 71, 0, CancellationToken.None);

        // Assert
        Assert.Equal(prior, states[0]);
        Assert.Equal(SafeMigrationObservedState.Unsupported, states[1]!.Value.State);
        Assert.Equal(code, states[1]!.Value.Code);
        var submission = Assert.Single(connection.Submissions);
        Assert.Contains("SELECT 1, CASE", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 0, CASE", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("ELSE NULL END, CASE WHEN NOT COALESCE", submission.Sql, StringComparison.Ordinal);
        Assert.Equal(code, Assert.Single(submission.Parameters).Value);
        Assert.Equal(71, submission.Timeout);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>NULL diagnostics retain the classified-state fallback; successful guards publish nothing.</summary>
    /// <param name="rejected">Whether the lazy guard actually rejected evaluation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardCodeNullFallbackNeverPublishesSuccessfulGuard(bool rejected)
    {
        // Arrange
        await using var context = CreateContext();
        var analyzer = (MySqlSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var connection = new CatalogStatementTestConnection(false,
            (sql, _) => Results(sql, rejected ? "unsupported" : null, rejected ? null : "unaccepted_code"));
        MySqlSafeMigrationProviderAnalyzer.MySqlShortCircuitState?[] states = [null];

        // Act
        await analyzer.FindStateEvaluationGuardStatesAsync(connection, [Plan()], states,
            16_384, 71, 0, CancellationToken.None);

        // Assert
        if (rejected)
        {
            Assert.Equal("classified_unsupported", states[0]!.Value.Code);
            Assert.Equal(SafeMigrationObservedState.Unsupported, states[0]!.Value.State);
        }
        else
        {
            Assert.Null(states[0]);
        }

        Assert.Contains("THEN (NULL) ELSE NULL END", Assert.Single(connection.Submissions).Sql,
            StringComparison.Ordinal);
    }

    /// <summary>Unresolved live-proof templates never read a missing column to obtain a diagnostic code.</summary>
    /// <param name="marker">The unavailable proof token inside the code expression.</param>
    [Theory]
    [InlineData(MySqlSafeMigrationRuntimePlan.DataProbePlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder)]
    public void UnavailableProofCodeFallsBackWithoutEvaluatingRows(string marker)
    {
        // Arrange
        var plan = Plan() with { ClassificationCodeExpression = $"CASE WHEN {marker} THEN 'unproven' ELSE NULL END" };

        // Act
        var code = plan.RenderStateEvaluationGuardClassificationCodeExpression(
            static _ => throw new InvalidOperationException("No diagnostic parameter was required."));

        // Assert
        Assert.Equal("NULL", code);
    }

    /// <summary>Discarded diagnostics cannot allocate parameters or inflate the guard payload.</summary>
    [Fact]
    public void UnavailableProofCodeDoesNotRenderUnusedParameters()
    {
        // Arrange
        var plan = Plan() with
        {
            ClassificationCodeExpression = $"CASE WHEN {MySqlSafeMigrationRuntimePlan.DataProbePlaceholder} "
                + $"THEN {MySqlCatalogSqlTemplate.Marker(0)} ELSE NULL END",
            ParameterValues = [new MySqlCatalogParameterValue("anonymous_diagnostic", null)],
        };

        var renderedParameter = false;

        // Act
        var code = plan.RenderStateEvaluationGuardClassificationCodeExpression(_ =>
        {
            renderedParameter = true;

            return "@unused_parameter";
        });

        // Assert
        Assert.Equal("NULL", code);
        Assert.False(renderedParameter);
    }

    /// <summary>Quoted proof-shaped literal values and identifiers are not structural proof references.</summary>
    /// <param name="expression">The catalog-only expression containing quoted marker spelling.</param>
    [Theory]
    [InlineData("'" + MySqlSafeMigrationRuntimePlan.DataProbePlaceholder + "'")]
    [InlineData("\"" + MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder + "\"")]
    [InlineData("`" + MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder + "`")]
    public void QuotedMarkerCodeKeepsItsExactCatalogExpression(string expression)
    {
        // Arrange
        var plan = Plan() with { ClassificationCodeExpression = expression };

        // Act
        var code = plan.RenderStateEvaluationGuardClassificationCodeExpression(
            static _ => throw new InvalidOperationException("Quoted markers cannot need a parameter."));

        // Assert
        Assert.Equal(expression, code);
    }

    /// <summary>Only an already-bound required-add status is available during lazy guard diagnostics.</summary>
    /// <param name="bound">Whether the earlier fact probe supplied an immutable status binding.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardCodeRequiresPriorRequiredAddStatusBinding(bool bound)
    {
        // Arrange
        var status = MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder;
        var plan = Plan() with
        {
            ClassificationCodeExpression = $"CASE WHEN {status} = 'unavailable' "
                + "THEN 'column_atomic_add_unavailable' ELSE NULL END",
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan("guard", "unsafe_status_query", false)
            {
                StatusBinding = bound ? "'unavailable'" : null,
            },
        };

        // Act
        var code = plan.RenderStateEvaluationGuardClassificationCodeExpression(
            static _ => throw new InvalidOperationException("Status was already bound."));

        // Assert
        Assert.Equal(bound ? "CASE WHEN 'unavailable' = 'unavailable' "
            + "THEN 'column_atomic_add_unavailable' ELSE NULL END" : "NULL", code);
        Assert.DoesNotContain("unsafe_status_query", code, StringComparison.Ordinal);
    }

    /// <summary>Malformed returned ordinals never become another operation's classification.</summary>
    /// <param name="ordinal">The unowned ordinal returned by the deterministic transport.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    public async Task GuardDiagnosticsRejectUnownedOrdinals(int ordinal)
    {
        // Arrange
        await using var context = CreateContext();
        var analyzer = (MySqlSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var connection = new CatalogStatementTestConnection(false, (sql, _) =>
        {
            var result = Results(sql, "unsupported", "unowned_code");
            result.Rows[0][0] = ordinal;

            return result;
        });

        MySqlSafeMigrationProviderAnalyzer.MySqlShortCircuitState?[] states = [null];

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindStateEvaluationGuardStatesAsync(connection, [Plan()], states,
                16_384, 71, 0, CancellationToken.None));

        // Assert
        Assert.Contains("invalid ordinal", exception.Message, StringComparison.Ordinal);
        Assert.Null(states[0]);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Code parameters participate in the same payload boundary before any query is submitted.</summary>
    [Fact]
    public async Task GuardDiagnosticParametersCannotBypassPayloadBudget()
    {
        // Arrange
        await using var context = CreateContext();
        var analyzer = (MySqlSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var connection = new CatalogStatementTestConnection(false,
            static (_, _) => throw new InvalidOperationException("Oversized diagnostics cannot be submitted."));
        var plan = Plan() with
        {
            ClassificationCodeExpression = MySqlCatalogSqlTemplate.Marker(0),
            ParameterValues = [new MySqlCatalogParameterValue(new string('x', 10_000), null)],
        };

        MySqlSafeMigrationProviderAnalyzer.MySqlShortCircuitState?[] states = [null];

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindStateEvaluationGuardStatesAsync(connection, [plan], states,
                1024, 71, 17, CancellationToken.None));

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.Null(states[0]);
    }

    private static MySqlSafeMigrationRuntimePlan Plan() => new("'unsupported'", "FALSE",
        SafeMigrationRepairCapability.None, "FALSE")
    {
        StateEvaluationGuardExpression = "FALSE",
        StateEvaluationGuardFailureExpression = "'unsupported'",
    };

    private static DbContext CreateContext() => new(new DbContextOptionsBuilder<DbContext>()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11))).UseMySqlSafeMigrations().Options);

    private static DataTable Results(string sql, string? state, string? code)
    {
        var result = new DataTable();
        result.Columns.Add("ordinal", typeof(int));
        result.Columns.Add("state", typeof(string));
        result.Columns.Add("code", typeof(string));
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(sql, "SELECT ([0-9]+), CASE"))
        {
            result.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                (object?)state ?? DBNull.Value, (object?)code ?? DBNull.Value);
        }

        return result;
    }
}
