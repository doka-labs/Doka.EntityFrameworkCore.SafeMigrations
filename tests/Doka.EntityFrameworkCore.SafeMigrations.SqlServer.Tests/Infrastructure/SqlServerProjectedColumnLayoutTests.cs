namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordered layout rejection against the production catalog capture path.</summary>
public sealed class SqlServerProjectedColumnLayoutTests
{
    private const string ConnectionString = "Server=localhost;Database=layout;Integrated Security=true;";

    /// <summary>Only an actual allocation failure may replace generic structural uncertainty.</summary>
    /// <param name="columns">The captured physical column count.</param>
    /// <param name="fixedBytes">The captured fixed allocation.</param>
    /// <param name="known">Whether the physical allocation lineage is proven.</param>
    /// <param name="storeType">The requested column's physical store type.</param>
    /// <param name="failureCode">The expected rejection, or null when uncertainty must remain.</param>
    [Theory]
    [InlineData(1, 8000, true, "char(53)", null)]
    [InlineData(1, 8000, true, "char(54)", "column_fixed_row_limit")]
    [InlineData(1024, 0, true, "char(1)", "column_limit")]
    [InlineData(2, 5004, false, "char(5000)", "column_layout_unproven")]
    public void CapturedLayout_QualifiesUnknownOnlyWhenAdditionFails(
        int columns,
        int fixedBytes,
        bool known,
        string storeType,
        string? failureCode
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, columns, fixedBytes, known);
        var unknown = Unknown();
        var column = Column("Added", storeType);

        // Act
        var result = analyzer.ValidateProjectedOperation(column, unknown, new EmptyColumns());

        // Assert
        if (failureCode is null)
        {
            Assert.Same(unknown, result);
            Assert.True(result.IsOpaqueProjectionUnknown);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, result.ObservedState);
            Assert.Equal(failureCode, result.Code);
            Assert.False(result.IsOpaqueProjectionUnknown);
            Assert.False(result.IsInvariantUnsupported);
        }
    }

    /// <summary>An accepted drop retains allocation and exposes its rejection through Core projection.</summary>
    [Fact]
    public void AcceptedDrop_CoreUnknownRetainsCapturedLayoutRejection()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, 2, 5004, true);
        CaptureBinding(analyzer, "Removed", 2);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer);
        var drop = new SafeMigrationOperation(new DropColumnIntent("Removed", "layout_probe"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Live(SafeMigrationObservedState.Matching);
        projection.Observe(drop, matching, matching, Decision(drop, matching));
        var column = Column("Added", "char(5000)");

        // Act
        var result = projection.Project(column, Live(SafeMigrationObservedState.Unsupported));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, result.ObservedState);
        Assert.Equal("column_layout_unproven", result.Code);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Decision(column, result).Action);
        Assert.False(result.IsOpaqueProjectionUnknown);
    }

    /// <summary>Unknown state without captured layout cannot be reclassified as a provider rejection.</summary>
    [Fact]
    public void MissingCapture_PreservesUnknownInsteadOfInventingLayoutEvidence()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var unknown = Unknown();
        var column = Column("Added", "char(5000)");

        // Act
        var result = analyzer.ValidateProjectedOperation(column, unknown, new EmptyColumns());

        // Assert
        Assert.Same(unknown, result);
    }

    /// <summary>Invariant and unrelated classifications take precedence over a captured allocation failure.</summary>
    /// <param name="state">The original provider classification.</param>
    /// <param name="code">The original provider diagnostic.</param>
    /// <param name="opaque">Whether the original classification carries opaque uncertainty.</param>
    /// <param name="invariant">Whether the operation contract is unsupported independently of state.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported, "unsupported_contract", false, true)]
    [InlineData(SafeMigrationObservedState.Unsupported, "projected_structure_state_unknown", false, false)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, "projected_data_state_unknown", true, false)]
    [InlineData(SafeMigrationObservedState.Different, "classified_different", false, false)]
    [InlineData(SafeMigrationObservedState.Matching, "classified_matching", false, false)]
    public void CapturedFailure_DoesNotOverwriteOtherClassifications(
        SafeMigrationObservedState state,
        string code,
        bool opaque,
        bool invariant
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, 2, 5004, false);
        var analysis = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false, code)
        {
            IsOpaqueProjectionUnknown = opaque,
            IsInvariantUnsupported = invariant,
        };

        var column = Column("Added", "char(5000)");

        // Act
        var result = analyzer.ValidateProjectedOperation(column, analysis, new EmptyColumns());

        // Assert
        Assert.Same(analysis, result);
    }

    /// <summary>A bound existing column cannot be treated as a missing physical allocation.</summary>
    [Fact]
    public void ExistingBinding_PreservesUnknownWithoutAllocationRejection()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, 2, 5004, false);
        CaptureBinding(analyzer, "Added", 2);
        var unknown = Unknown();
        var column = Column("Added", "char(5000)");

        // Act
        var result = analyzer.ValidateProjectedOperation(column, unknown, new EmptyColumns());

        // Assert
        Assert.Same(unknown, result);
    }

    /// <summary>A rejected drop never invalidates the captured proof consumed by a later addition.</summary>
    [Fact]
    public void RejectedDrop_DoesNotInvalidateAllocationProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, 2, 5004, true);
        CaptureBinding(analyzer, "Removed", 2);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer);
        var drop = new SafeMigrationOperation(new DropColumnIntent("Removed", "layout_probe"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var rejected = Live(SafeMigrationObservedState.Different);
        projection.Observe(drop, rejected, rejected, Decision(drop, rejected));
        var column = Column("Added", "char(100)");
        var missing = Live(SafeMigrationObservedState.Missing);

        // Act
        var result = projection.Project(column, missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, Decision(column, result).Action);
    }

    /// <summary>Raw SQL bypasses captured allocation rejection and leaves the runtime guard authoritative.</summary>
    /// <param name="invariant">Whether the live rejection is independent of mutable database state.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawSql_CoreBoundaryPreservesOnlyInvariantRejections(bool invariant)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        CaptureLayout(analyzer, 2, 5004, true);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer);
        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        var column = Column("Added", "char(5000)");
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, "column_fixed_row_limit")
        {
            IsInvariantUnsupported = invariant,
        };

        // Act
        var result = projection.Project(column, live);

        // Assert
        if (invariant)
        {
            Assert.Same(live, result);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Decision(column, result).Action);
        }
        else
        {
            Assert.True(result.IsOpaqueProjectionUnknown);
            Assert.Equal("projected_structure_state_unknown", result.Code);
        }
    }

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation Column(
        string name,
        string storeType
    )
        => new(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition(name, typeof(string), true, storeType)), SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, false, "classified_live");

    private static SafeMigrationProviderAnalysis Unknown()
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false,
            "projected_structure_state_unknown")
        {
            IsOpaqueProjectionUnknown = true,
        };

    private static SafeMigrationDecision Decision(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    ) => SafeMigrationDecisionPlanner.Plan(
        operation.Intent.Kind, analysis.ObservedState, operation.Policy, analysis.RepairCapability);

    private static void CaptureLayout(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        int columns,
        int fixedBytes,
        bool known
    )
    {
        using var table = new DataTable();
        table.Columns.Add("schema", typeof(string));
        table.Columns.Add("table", typeof(string));
        table.Columns.Add("columns", typeof(int));
        table.Columns.Add("fixed", typeof(int));
        table.Columns.Add("bits", typeof(int));
        table.Columns.Add("variables", typeof(int));
        table.Columns.Add("clustered", typeof(int));
        table.Columns.Add("known", typeof(bool));
        table.Rows.Add("dbo", "layout_probe", columns, fixedBytes, 0, 0, 0, known);

        using var reader = table.CreateDataReader();

        reader.Read();
        analyzer.CaptureProjectedColumnLayout(reader);
    }

    private static void CaptureBinding(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        string name,
        int identity
    )
    {
        using var table = new DataTable();
        table.Columns.Add("schema", typeof(string));
        table.Columns.Add("table", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("identity", typeof(int));
        table.Columns.Add("variable", typeof(int));
        table.Columns.Add("clustered", typeof(bool));
        table.Rows.Add("dbo", "layout_probe", name, identity, 0, false);

        using var reader = table.CreateDataReader();

        reader.Read();
        analyzer.CaptureProjectedColumnLayoutBinding(reader);
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
