namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Captures the complete bounded stress classifiers, including guards and source parameter facets.</summary>
public sealed class SqlServerFullCatalogCaptureTests
{
    /// <summary>Preserves every captured result and optionally replays both SQL shapes on an explicit local server.</summary>
    [Fact]
    public async Task FullCapture_RetainsCompleteClassifiersAndIndependentParameters()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1;Database=unused;User ID=unused;Password=unused;TrustServerCertificate=true",
            registerSafeMigrations: false);

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        LargeMigrationStressContract.Populate(migration, LargeMigrationStressDialect.SqlServer);
        var operations = migration.Operations.OfType<SafeMigrationOperation>().ToArray();
        var results = new SafeMigrationProviderAnalysis[SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture];
        var plans = BuildPlans(context, operations, results);
        await using var connection = new SqlServerCatalogTestConnection();
        var diagnosticRoot = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_DIAGNOSTIC_CONNECTION");

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 180, plans, 0, results, CancellationToken.None);

        var standalone = BuildStandaloneStatements(plans, connection);
        if (!string.IsNullOrWhiteSpace(diagnosticRoot))
        {
            // WHY: Local emulation is useful for SQL correctness and relative experiments, not native
            // qualification. Ordinary test runs stay offline; only this explicit opt-in creates databases.
            await SqlServerLocalCatalogDiagnostics.RunAsync(
                diagnosticRoot, connection.RecordedStatements, standalone, connection.RecordedParameters);
        }

        // Assert
        Assert.Equal(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture, plans.Length);
        Assert.All(results, Assert.NotNull);
        Assert.Null(plans[3]);
        Assert.Equal(SafeMigrationObservedState.Unsupported, results[3].ObservedState);
        Assert.True(results[3].IsInvariantUnsupported);
        Assert.Equal(1, connection.BatchExecutions);
        Assert.InRange(connection.RecordedStatements.Count, 1,
            SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);
        Assert.True(connection.RecordedStatements.Sum(statement => statement.Length)
            < standalone.Sum(statement => statement.Length));
        Assert.Contains(connection.RecordedStatements,
            statement => statement.StartsWith("DECLARE @doka_template", StringComparison.Ordinal));
        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    /// <summary>Mirrors the real analyzer's source binding, transition catalog and known fixture occupancy.</summary>
    private static SqlServerSafeMigrationRuntimePlan?[] BuildPlans(
        SafeMigrationDbContext context,
        SafeMigrationOperation[] operations,
        SafeMigrationProviderAnalysis[] results
    )
    {
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var helper = context.GetService<ISqlGenerationHelper>();
        var expectedTables = SafeMigrationExpectedTableConstraints.FromOperations(
            operations, static schema => schema ?? "dbo");

        var plans = new SqlServerSafeMigrationRuntimePlan?[SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture];
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            var operation = operations[ordinal];
            var bindings = operation.Intent is ModelManagedDataIntent
                ? new SqlServerCatalogParameterBindings(mappings, ordinal) : null;

            var builder = new SqlServerSafeMigrationCatalogSqlBuilder(mappings, helper,
                sourceParameter: bindings is null ? null : (value, _) => bindings.Add(value));

            var expected = operation.Intent is EnsureTableIntent table
                ? expectedTables.GetValueOrDefault((table.Definition.Schema, table.Definition.Table)) : null;

            // WHY: The diagnostic fixture initially owns exactly these three tables. Production reads
            // occupancy first; generating full absent-table matchers here would measure a different path.
            var absent = operation.Intent is EnsureTableIntent target
                && target.Definition.Table is not ("sqlserver_stress_target" or "sqlserver_stress_alter"
                    or "sqlserver_stress_managed");

            var plan = builder.Build(operation, expected, absent);
            if (plan.IsStaticallyUnsupported)
            {
                // WHY: Production classifies these operations without SQL. The diagnostic must keep
                // their result ordinals but must not introduce an extra cheap classifier into timing.
                results[ordinal] = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                    SafeMigrationRepairCapability.None, false, plan.UnsupportedCode ?? "classified_unsupported")
                {
                    IsInvariantUnsupported = true,
                };

                continue;
            }

            if (bindings is not null && bindings.Values.Count > 0)
            {
                var guard = builder.BuildModelManagedDataAnalysisGuard((ModelManagedDataIntent)operation.Intent);
                plan = plan with
                {
                    AnalysisParameters = bindings.Values,
                    AnalysisOuterStateGuardExpression = guard.Guard,
                    AnalysisOuterStateGuardFailureExpression = guard.Failure,
                };
            }

            plans[ordinal] = plan;
        }

        return plans;
    }

    /// <summary>Rebuilds the previous literal dispatch shape from captured plans, without rewriting generated SQL.</summary>
    private static string[] BuildStandaloneStatements(
        SqlServerSafeMigrationRuntimePlan?[] plans,
        SqlServerCatalogTestConnection connection
    )
    {
        var statements = connection.RecordedStatements.ToArray();
        for (var index = 0; index < statements.Length; index++)
        {
            if (!statements[index].StartsWith("DECLARE @doka_template", StringComparison.Ordinal))
            {
                continue;
            }

            var slots = connection.RecordedParameters[index]
                .Where(parameter => parameter.ParameterName.StartsWith("@doka_ordinal", StringComparison.Ordinal))
                .Select(parameter => Convert.ToInt32(parameter.Value, CultureInfo.InvariantCulture)).ToArray();

            // WHY: This fixture uses fewer than 2,000 source bindings per classifier. The captured
            // ordinal parameters therefore identify every dispatcher and retain its original slot order.
            statements[index] = string.Join("\n", slots.Select((ordinal, slot) =>
                SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(ordinal, plans[ordinal]!, slot)));
        }

        return statements;
    }
}
