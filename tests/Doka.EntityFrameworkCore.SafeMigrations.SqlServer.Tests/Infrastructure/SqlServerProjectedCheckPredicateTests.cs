namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies CHECK row-proof lifetime across ordered value-preserving and adversarial mutations.</summary>
public sealed class SqlServerProjectedCheckPredicateTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=projected_check;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Only accepted lossless ALTERs preserve the physical integer predicate's captured truth.</summary>
    [Theory]
    [InlineData("widening", true)]
    [InlineData("rename", false)]
    [InlineData("drop", false)]
    [InlineData("sql", false)]
    [InlineData("unrelated-managed-dml", false)]
    public void PredicateProof_RequiresRetainedIdentityAndGloballyUnchangedRows(
        string mutation,
        bool reusable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "classified_missing") { RequiresLiveDataProof = true };

        analyzer.CaptureProjectedCheckPredicates([check, check], [missing, missing]);
        var unknown = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_structure_state_unknown")
        {
            IsOpaqueProjectionUnknown = true,
        };

        // Act
        if (mutation == "sql")
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "UPDATE dbo.other_items SET Value=1;" });
        }
        else
        {
            var operation = new SafeMigrationOperation(mutation switch
            {
                "widening" => new AlterColumnIntent("items",
                    new ExpectedColumnDefinition("Value", typeof(long), true, "bigint"),
                    new ExpectedColumnDefinition("Value", typeof(int), true, "int")),
                "rename" => new RenameColumnIntent("Value", "items", "Other"),
                "drop" => new DropColumnIntent("Value", "items"),
                "unrelated-managed-dml" => new EnsureModelManagedDataIntent("other_items", ["Id"], ["int"],
                    ["Id"], ["int"], new object?[,] { { 1 } }, null, null),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            }, mutation == "unrelated-managed-dml"
                ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.RepairIfSafe);

            var observed = mutation == "widening"
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.Safe, false, "classified_different")
                : mutation == "unrelated-managed-dml" ? missing
                    : new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None, true, "classified_matching");

            var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, observed.ObservedState,
                operation.Policy, observed.RepairCapability);

            analyzer.ObserveAcceptedOperation(operation, observed, observed, decision);
        }

        var result = analyzer.ValidateProjectedOperation(check, unknown, new EmptyColumns());

        // Assert
        Assert.Equal(reusable ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            result.ObservedState);
    }

    /// <summary>A replacement certificate needs the exact accepted local CHECK drop, not another named drop.</summary>
    [Theory]
    [InlineData("none", false)]
    [InlineData("wrong-name", false)]
    [InlineData("wrong-table", false)]
    [InlineData("wrong-kind", false)]
    [InlineData("exact", true)]
    public void ReplacementPredicate_RequiresExactAcceptedLocalDrop(
        string dropKind,
        bool reusable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=1")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "classified_missing") { RequiresLiveDataProof = true };

        analyzer.CaptureProjectedCheckReplacement(check, missing);
        var unknown = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_structure_state_unknown");

        // Act
        if (dropKind != "none")
        {
            var drop = new SafeMigrationOperation(dropKind == "wrong-kind"
                ? new DropIndexIntent("CK_items", "items")
                : new DropCheckConstraintIntent(dropKind == "wrong-name" ? "CK_other" : "CK_items",
                    dropKind == "wrong-table" ? "other_items" : "items"), SafeMigrationPolicy.ThrowIfDifferent);

            var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, true, "classified_matching");

            analyzer.ObserveAcceptedOperation(drop, matching, matching,
                SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy,
                    matching.RepairCapability));
        }

        var result = analyzer.ValidateProjectedOperation(check, unknown, new EmptyColumns());

        // Assert
        Assert.Equal(reusable ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            result.ObservedState);
    }

    /// <summary>Replacement captures stay segmented and reject occupied nonlocal candidate classifications.</summary>
    [Theory]
    [InlineData(true, "missing", true)]
    [InlineData(false, "missing", true)]
    [InlineData(true, "different", false)]
    public async Task ReplacementCapture_IsBoundedAndDoesNotEraseForeignOccupancy(
        bool native,
        string state,
        bool reusable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        await using var connection = new SqlServerCatalogTestConnection(native)
        {
            TransformClassifierTable = table =>
            {
                foreach (DataRow row in table.Rows)
                {
                    row["state"] = state;
                }
            },
        };

        var operations = Enumerable.Range(0, 513).Select(index => new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("CK_items", "items_" + index,
                "[Value]>=1")), SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

        var occupied = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "classified_different");

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "classified_matching");

        var ordered = operations.SelectMany(operation => new[]
        {
            new SafeMigrationOperation(new DropCheckConstraintIntent("CK_items",
                ((EnsureCheckConstraintIntent)operation.Intent).Definition.Table),
                SafeMigrationPolicy.ThrowIfDifferent),
            operation,
        }).ToArray();

        var repeated = ordered.Concat(ordered).ToArray();
        var drop = ordered[^2];
        var unknown = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_structure_state_unknown");

        // Act
        await analyzer.ReadProjectedCheckReplacementsAsync(connection, null, repeated,
            repeated.Select(operation => operation.Intent is DropCheckConstraintIntent ? matching : occupied).ToArray(),
            71, CancellationToken.None);
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy,
                matching.RepairCapability));
        var result = analyzer.ValidateProjectedOperation(operations[^1], unknown, new EmptyColumns());

        // Assert
        Assert.Equal(reusable ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            result.ObservedState);
        Assert.All(connection.BatchPayloadBytes,
            size => Assert.InRange(size, 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
        Assert.All(connection.ObservedTimeouts, timeout => Assert.Equal(71, timeout));
    }

    /// <summary>Real Core projection forwards both valid and FALSE replacement rows after the exact drop.</summary>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void CoreProjection_DropAndReplacementRetainsCompleteNewPredicateResult(
        SafeMigrationObservedState candidateState
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=1")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var captured = new SafeMigrationProviderAnalysis(candidateState, SafeMigrationRepairCapability.None,
            false, "new_predicate_result") { RequiresLiveDataProof = true };

        analyzer.CaptureProjectedCheckReplacement(check, captured);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer, projectedColumnAnalyzer: analyzer);

        var drop = new SafeMigrationOperation(new DropCheckConstraintIntent("CK_items", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "classified_matching");

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "classified_different");

        // Act
        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy,
                matching.RepairCapability));
        var result = projection.Project(check, live);

        // Assert
        Assert.Equal(candidateState, result.ObservedState);
        Assert.Equal("new_predicate_result", result.Code);
    }

    /// <summary>A named drop never turns stale row truth after global DML or opaque SQL into an empty proof.</summary>
    [Theory]
    [InlineData("managed")]
    [InlineData("sql")]
    public void CoreProjection_ReplacementAfterGlobalMutationRequiresFreshRows(
        string mutation
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=1")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "captured_missing") { RequiresLiveDataProof = true };

        analyzer.CaptureProjectedCheckReplacement(check, missing);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer, projectedColumnAnalyzer: analyzer);

        var drop = new SafeMigrationOperation(new DropCheckConstraintIntent("CK_items", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "classified_matching");

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "classified_different") { RequiresLiveDataProof = true };

        // Act
        if (mutation == "sql")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "UPDATE dbo.other_items SET Value=0;" });
        }
        else
        {
            var data = new SafeMigrationOperation(new EnsureModelManagedDataIntent("other_items", ["Id"], ["int"],
                ["Id"], ["int"], new object?[,] { { 1 } }, null, null), SafeMigrationPolicy.ThrowIfDifferent);

            projection.Observe(data, missing, missing,
                SafeMigrationDecisionPlanner.Plan(data.Intent.Kind, missing.ObservedState, data.Policy));
        }

        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = projection.Project(check, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal(mutation == "sql" ? "projected_structure_state_unknown" : "projected_data_state_unknown",
            result.Code);
        Assert.Equal(mutation == "sql", result.IsOpaqueProjectionUnknown);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing,
            SafeMigrationDecisionPlanner.Plan(check.Intent.Kind, result.ObservedState, check.Policy).Action);
    }

    /// <summary>Opaque replacements reuse only a captured empty table, never named-drop Missing alone.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CoreProjection_OpaqueReplacementRequiresEmptyOnlyCertificate(
        bool populated,
        bool sourceMatching
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        await using var connection = new SqlServerCatalogTestConnection
        {
            TransformClassifierTable = table =>
            {
                foreach (DataRow row in table.Rows)
                {
                    row["state"] = populated ? "data_blocked" : "missing";
                }
            },
        };

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]/0>0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var drop = new SafeMigrationOperation(new DropCheckConstraintIntent("CK_items", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "classified_matching");

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "classified_different");

        if (sourceMatching)
        {
            live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, true, "classified_matching") { MatchedObjectName = "CK_items" };
        }

        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer, projectedColumnAnalyzer: analyzer);

        // Act
        await analyzer.ReadProjectedCheckReplacementsAsync(connection, null, [drop, check], [matching, live],
            72, CancellationToken.None);
        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = projection.Project(check, live);

        // Assert
        Assert.Equal(populated ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Missing,
            result.ObservedState);
        Assert.NotEmpty(connection.RecordedStatements);
        Assert.DoesNotContain("WHERE NOT ([Value]/0>0)", connection.RecordedStatements[0], StringComparison.Ordinal);
    }

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = null;

            return false;
        }
    }
}
