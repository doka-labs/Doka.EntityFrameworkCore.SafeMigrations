namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Separates captured column-source lifetimes from accepted index and key mutations.</summary>
public sealed class SafeMigrationIndexDropColumnProofTests
{
    private static readonly string[] s_indexNames = ["IX_values", "IX_values1", "IX_values2"];

    /// <summary>Each accepted widening retains untouched siblings without restoring the removed index.</summary>
    /// <param name="physicalDrop">Whether the index still exists or an interrupted prefix already removed it.</param>
    /// <param name="indexCount">The number of independent index removals before the sibling alterations.</param>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    public void AcceptedIndexDropsPreserveThreeIndependentColumnTransitions(bool physicalDrop, int indexCount)
    {
        // Arrange
        var provider = new IndexDropColumnProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var operations = new[] { Widen("Left"), Widen("Right"), Widen("Position") };
        var drops = s_indexNames.Take(indexCount)
            .Select(name => new SafeMigrationOperation(new DropIndexIntent(name, "items"),
                SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

        var live = CapturedRepair();
        var results = new List<SafeMigrationProviderAnalysis>();

        // Act
        foreach (var drop in drops)
        {
            Observe(projection, drop, physicalDrop ? Matching() : Missing());
        }

        foreach (var operation in operations)
        {
            var result = projection.Project(operation, live);
            results.Add(result);
            Observe(projection, operation, live, result);
        }

        var droppedIndexes = drops.Select(drop => projection.Project(drop, physicalDrop ? Matching() : Missing()))
            .ToArray();

        // Assert
        Assert.All(results, result => Assert.Equal(SafeMigrationRepairCapability.Safe, result.RepairCapability));
        Assert.All(results, result => Assert.False(result.RepairPreservesPhysicalKeys));
        Assert.All(droppedIndexes, index => Assert.Equal(SafeMigrationObservedState.Missing, index.ObservedState));
        Assert.True(provider.ReuseQueries >= operations.Length);
        Assert.Equal(physicalDrop, provider.PreservationQueries > 0);
    }

    /// <summary>An independent boundary cannot be explained away by a later ordinary index removal.</summary>
    /// <param name="boundary">The mutation or unsupported proof that expires the captured source.</param>
    [Theory]
    [InlineData("provider-opt-out")]
    [InlineData("uncertified-index")]
    [InlineData("annotated-drop")]
    [InlineData("opaque-sql")]
    [InlineData("provider-ddl")]
    [InlineData("managed-data")]
    [InlineData("unknown-index-history")]
    [InlineData("changed-source")]
    public void IndexDropCertificateCannotReviveUnprovenColumnHistory(string boundary)
    {
        // Arrange
        var provider = new IndexDropColumnProbe(boundary != "provider-opt-out");
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var drop = new SafeMigrationOperation(new DropIndexIntent(
            boundary == "uncertified-index" ? "IX_unknown" : "IX_values", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        if (boundary == "annotated-drop")
        {
            drop.AddAnnotation("Uncertified:Effect", true);
        }

        if (boundary == "unknown-index-history")
        {
            Observe(projection, new SafeMigrationOperation(new RenameIndexIntent("IX_unknown", "items", "IX_other"),
                SafeMigrationPolicy.ThrowIfDifferent), Matching(), Matching());
        }

        Observe(projection, drop, Matching());
        switch (boundary)
        {
            case "opaque-sql":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
                break;
            case "provider-ddl":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
                break;
            case "managed-data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "items", Columns = ["Left"], Values = new object[,] { { 1 } },
                });
                break;
            case "changed-source":
                Observe(projection, Widen("Left"), CapturedRepair());
                break;
        }

        var operation = boundary == "changed-source"
            ? new SafeMigrationOperation(new AlterColumnIntent("items",
                new ExpectedColumnDefinition("Left", typeof(long), true, "bigint"),
                new ExpectedColumnDefinition("Left", typeof(int), false, "int")), SafeMigrationPolicy.RepairIfSafe)
            : Widen("Left");

        // Act
        var result = projection.Project(operation, CapturedRepair());

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.NotEqual(SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>Independent native data and capability failures remain failures after a certified removal.</summary>
    /// <param name="state">The provider's independently captured rejection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void IndexRemovalDoesNotReplaceIndependentNativeRejection(SafeMigrationObservedState state)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: new IndexDropColumnProbe());
        Observe(projection, new SafeMigrationOperation(new DropIndexIntent("IX_values", "items"),
            SafeMigrationPolicy.ThrowIfDifferent), Matching());

        var live = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false,
            "independent_native_rejection");

        // Act
        var result = projection.Project(Widen("Left"), live);

        // Assert
        Assert.Same(live, result);
    }

    /// <summary>Creates a widening with an immutable, explicitly authored source.</summary>
    private static SafeMigrationOperation Widen(string column) => new(
        new AlterColumnIntent("items", new ExpectedColumnDefinition(column, typeof(long), false, "bigint"),
            new ExpectedColumnDefinition(column, typeof(int), false, "int")), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates independently captured exact source eligibility without inventing key evidence.</summary>
    private static SafeMigrationProviderAnalysis CapturedRepair() => new(
        SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "captured_integer_source");

    /// <summary>Creates a satisfied native presence observation.</summary>
    private static SafeMigrationProviderAnalysis Matching() => new(
        SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "classified_matching");

    /// <summary>Creates independently captured index absence without a physical mutation certificate.</summary>
    private static SafeMigrationProviderAnalysis Missing() => new(
        SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, true, "classified_missing");

    /// <summary>Observes one planner decision without asserting a future operation's result.</summary>
    private static void Observe(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        SafeMigrationProviderAnalysis? analysis = null
    )
    {
        analysis ??= projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);
    }

    /// <summary>Models an opted-in provider while keeping ordinary projected repairs conservative.</summary>
    private sealed class IndexDropColumnProbe(bool optIn = true) : ISafeMigrationProjectedColumnAnalyzer
    {
        /// <summary>Gets the number of explicitly qualified index-drop source reuse requests.</summary>
        public int ReuseQueries { get; private set; }

        /// <summary>Gets the number of accepted physical removal qualification requests.</summary>
        public int PreservationQueries { get; private set; }

        /// <summary>Certifies only the independently known ordinary index identity.</summary>
        public bool PreservesColumnSourcesAfterIndexDrop(
            DropIndexIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            PreservationQueries++;

            return optIn && intent.Name is "IX_values" or "IX_values1" or "IX_values2"
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Matching;
        }

        /// <summary>
        /// Supplies a provider certificate only after Core has checked source and mutation lifetimes.
        /// </summary>
        public bool CanReuseLiveColumnProofAfterIndexDrops(
            AlterColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis,
            ISafeMigrationProjectedColumnSource columns
        )
        {
            ReuseQueries++;

            return optIn && intent.OldDefinition is not null
                && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe;
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;
    }
}
