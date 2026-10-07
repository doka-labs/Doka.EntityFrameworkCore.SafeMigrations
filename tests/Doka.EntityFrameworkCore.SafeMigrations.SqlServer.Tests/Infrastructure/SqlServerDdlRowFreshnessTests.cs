namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies invocation-local DDL effects cannot promote stale row proofs into safe outcomes.</summary>
public sealed class SqlServerDdlRowFreshnessTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=ddl_row_freshness;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Known or unproved DDL effects invalidate row certificates, but a NoOp emits no such event.</summary>
    [Theory]
    [InlineData(false, true, SafeMigrationObservedState.Missing)]
    [InlineData(true, false, SafeMigrationObservedState.Missing)]
    [InlineData(true, true, SafeMigrationObservedState.PrerequisiteMissing)]
    public void CheckRowCertificate_RequiresDdlAbsenceOrNoExecutedDdl(
        bool risk,
        bool executes,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var catalog = Catalog(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = Analysis(SafeMigrationObservedState.Missing);
        analyzer.CaptureProjectedCheckPredicates([check], [missing]);
        analyzer.CaptureProjectedDdlRowDependency(check, catalog.Build(check));
        analyzer.CaptureProjectedDdlRowEffects(risk
            ? SqlServerDdlRowEffectRisk.VisibilityUnproven : SqlServerDdlRowEffectRisk.None);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var state = Analysis(executes ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing);

        // Act
        analyzer.ObserveAcceptedOperation(drop, state, state,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, state.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(check, missing, new EmptyColumns());

        // Assert
        Assert.Equal(expected, result.ObservedState);
        if (risk
            && executes)
        {
            Assert.Equal("projected_ddl_visibility_data_unknown", result.Code);
            Assert.False(result.IsOpaqueProjectionUnknown);
        }
    }

    /// <summary>Metadata widening survives DDL effects; tightening cannot reuse its pre-DDL NULL proof.</summary>
    [Theory]
    [InlineData(false, SafeMigrationRepairCapability.Safe)]
    [InlineData(true, SafeMigrationRepairCapability.None)]
    public void IntegerWidening_OnlyNullProofDependsOnDdlFreshness(
        bool tightens,
        SafeMigrationRepairCapability expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = new SafeMigrationOperation(new AlterColumnIntent("values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), tightens, "int")), SafeMigrationPolicy.RepairIfSafe);

        var safe = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "captured_safe");

        analyzer.CaptureProjectedIntegerWidening(widening, safe, []);
        analyzer.CaptureProjectedDdlRowDependency(widening, Catalog(context).Build(widening));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);

        // Act
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(widening, Analysis(SafeMigrationObservedState.Different),
            new EmptyColumns());

        // Assert
        Assert.Equal(expected, result.RepairCapability);
        if (tightens)
        {
            Assert.Equal("projected_ddl_visibility_data_unknown", result.Code);
        }
    }

    /// <summary>Managed Matching is row state, while a stamped CHECK Matching is a metadata contract.</summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Matching)]
    [InlineData(true, SafeMigrationObservedState.PrerequisiteMissing)]
    public void Matching_MetadataAndManagedRowsHaveDifferentFreshnessContracts(
        bool managed,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var operation = new SafeMigrationOperation(managed
            ? new EnsureModelManagedDataIntent("checked", ["Id"], ["int"], ["Id"], ["int"],
                new object?[,] { { 1 } }, null, null)
            : new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(operation, Catalog(context).Build(operation));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);

        // Act
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(operation, matching, new EmptyColumns());

        // Assert
        Assert.Equal(expected, result.ObservedState);
    }

    /// <summary>An unchanged table rename is planner Apply but emits no DDL, so it must retain row proofs.</summary>
    [Fact]
    public void UnchangedRename_ApplyWithoutPhysicalDdlRetainsCheckProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = Analysis(SafeMigrationObservedState.Missing);
        analyzer.CaptureProjectedCheckPredicates([check], [missing]);
        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        var rename = new SafeMigrationOperation(new RenameTableIntent("checked", "checked", "dbo", "dbo"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Intent.Kind, matching.ObservedState, rename.Policy);

        // Act
        analyzer.ObserveAcceptedOperation(rename, matching, matching, decision);
        var result = analyzer.ValidateProjectedOperation(check, missing, new EmptyColumns());

        // Assert
        Assert.True(decision.ShouldExecute);
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>Text ALTER and row-bound column addition retain their classifier's row-proof ownership.</summary>
    /// <param name="textAlter">Whether the operation validates text instead of an empty-table column addition.</param>
    /// <param name="risk">Whether DDL effects cannot be excluded.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RowBoundColumns_UsePlanFlagForFinalDdlFreshnessVeto(
        bool textAlter,
        bool risk
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var operation = new SafeMigrationOperation(textAlter
            ? new AlterColumnIntent("checked",
                new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(32)"),
                new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(64)"))
            : new EnsureColumnIntent("checked", new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = Catalog(context).Build(operation);
        CaptureEmptyLayout(analyzer);
        analyzer.CaptureProjectedDdlRowDependency(operation, plan);
        analyzer.CaptureProjectedDdlRowEffects(risk
            ? SqlServerDdlRowEffectRisk.VisibilityUnproven : SqlServerDdlRowEffectRisk.None);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var original = textAlter
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.Safe, false, "captured_safe")
            : Analysis(SafeMigrationObservedState.Missing);

        // Act
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(operation, original, new EmptyColumns());

        // Assert
        Assert.True(plan.RequiresLiveDataProof);
        Assert.Equal(risk ? SafeMigrationObservedState.PrerequisiteMissing : original.ObservedState,
            result.ObservedState);
        if (risk)
        {
            Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
            Assert.Equal("projected_ddl_visibility_data_unknown", result.Code);
        }
    }

    /// <summary>EF table creation cannot invent an empty-table CHECK proof when DDL triggers may add rows.</summary>
    /// <param name="risk">Whether DDL row effects are unproved.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedProviderCreateTable_CoreEmptyProjectionRequiresDdlFreshness(
        bool risk
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_created", "created", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(risk
            ? SqlServerDdlRowEffectRisk.VisibilityUnproven : SqlServerDdlRowEffectRisk.None);
        var projection = new SafeMigrationPreflightProjection(
            providerOperationProjection: analyzer, projectedDependencyAnalyzer: analyzer);

        var create = new CreateTableOperation { Name = "created" };
        create.Columns.Add(new AddColumnOperation
        {
            Name = "Value", Table = "created", ClrType = typeof(int), ColumnType = "int",
        });

        // Act
        projection.ObserveProviderPostcondition(create);
        var result = projection.Project(check, Analysis(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(risk ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Missing,
            result.ObservedState);
        if (risk)
        {
            Assert.Equal("projected_ddl_visibility_data_unknown", result.Code);
        }
    }

    /// <summary>Opaque SQL retains Core's deferred-origin classification instead of being recast as DDL.</summary>
    [Fact]
    public void OpaqueProviderSql_DoesNotOverwriteCoreDeferredOrigin()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        var unknown = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_structure_state_unknown")
        {
            IsOpaqueProjectionUnknown = true,
        };

        // Act
        analyzer.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;" });
        var result = analyzer.ValidateProjectedOperation(check, unknown, new EmptyColumns());

        // Assert
        Assert.Same(unknown, result);
        Assert.True(result.IsOpaqueProjectionUnknown);
    }

    /// <summary>Typed rename exclusions follow raw schema equality, including transfer to a default schema.</summary>
    /// <param name="sequence">Whether the provider operation renames a sequence rather than a table.</param>
    /// <param name="newName">The authored target name; null and identical names emit no rename.</param>
    /// <param name="newSchema">The raw target schema, not the safe intent's normalized default.</param>
    /// <param name="emitsDdl">Whether EF emits a rename or schema transfer.</param>
    [Theory]
    [InlineData(false, "source", "application", false)]
    [InlineData(false, null, "application", false)]
    [InlineData(false, "source", null, true)]
    [InlineData(true, "source", "application", false)]
    [InlineData(true, null, "application", false)]
    [InlineData(true, "source", null, true)]
    public void TypedProviderRename_OnlyExactNoDdlShapeRetainsFreshness(
        bool sequence,
        string? newName,
        string? newSchema,
        bool emitsDdl
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        MigrationOperation rename = sequence
            ? new RenameSequenceOperation
            {
                Name = "source", Schema = "application", NewName = newName, NewSchema = newSchema,
            }
            : new RenameTableOperation
            {
                Name = "source", Schema = "application", NewName = newName, NewSchema = newSchema,
            };

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([rename],
            context.GetService<IDesignTimeModel>().Model);

        // Act
        analyzer.ObserveProviderOperation(rename);
        var result = analyzer.ValidateProjectedOperation(check, Analysis(SafeMigrationObservedState.Missing),
            new EmptyColumns());

        // Assert
        Assert.Equal(emitsDdl, commands.Count > 0);
        Assert.Equal(emitsDdl ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Missing,
            result.ObservedState);
    }

    /// <summary>Only a real preceding executable DDL supplies a provider-specific deferred origin.</summary>
    /// <param name="typed">Whether the preceding DDL is owned by ordinary EF rather than SafeMigrations.</param>
    /// <param name="riskValue">The captured trigger or metadata-visibility uncertainty.</param>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void DdlRowUncertainty_RetainsExactOrdinalAndDistinctCause(
        bool typed,
        int riskValue
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var risk = (SqlServerDdlRowEffectRisk)riskValue;
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(risk);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer)
        {
            CurrentOperationOrdinal = 7,
        };

        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy);

        // Act
        if (typed)
        {
            projection.ObserveProviderPostcondition(new DropIndexOperation { Name = "IX_source", Table = "source" });
        }
        else
        {
            projection.Observe(drop, matching, matching, decision);
        }

        projection.CurrentOperationOrdinal = 8;
        var result = analyzer.ValidateProjectedOperation(check, Analysis(SafeMigrationObservedState.Missing),
            new EmptyColumns());

        // Assert
        Assert.Equal(7, result.ProviderDeferredOriginOrdinal);
        Assert.True(result.RequiresLiveDataProof);
        Assert.False(result.IsOpaqueProjectionUnknown);
        Assert.Equal(risk == SqlServerDdlRowEffectRisk.EnabledTrigger
            ? "projected_ddl_trigger_data_unknown" : "projected_ddl_visibility_data_unknown", result.Code);
    }

    /// <summary>Row uncertainty never converts independent catalog or policy refusals into deferred approval.</summary>
    /// <param name="state">The independent catalog failure.</param>
    /// <param name="invariant">Whether the provider also certifies invariant rejection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported, false)]
    [InlineData(SafeMigrationObservedState.Unsupported, true)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, false)]
    [InlineData(SafeMigrationObservedState.Different, false)]
    public void DdlRowUncertainty_PreservesIndependentBlockers(
        SafeMigrationObservedState state,
        bool invariant
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedDdlRowDependency(check, Catalog(context).Build(check));
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.EnabledTrigger);
        analyzer.SetCurrentOperationOrdinal(0);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var original = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false,
            "independent_failure") { IsInvariantUnsupported = invariant };

        // Act
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(check, original, new EmptyColumns());

        // Assert
        Assert.Same(original, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SqlServerSafeMigrationCatalogSqlBuilder Catalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state
    )
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching,
            "classified_state");

    private static void CaptureEmptyLayout(
        SqlServerSafeMigrationProviderAnalyzer analyzer
    )
    {
        using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("schema", typeof(string));
        table.Columns.Add("table", typeof(string));
        table.Columns.Add("columns", typeof(int));
        table.Columns.Add("fixed", typeof(int));
        table.Columns.Add("bits", typeof(int));
        table.Columns.Add("variables", typeof(int));
        table.Columns.Add("clustered", typeof(int));
        table.Columns.Add("known", typeof(bool));
        table.Rows.Add("dbo", "checked", 0, 0, 0, 0, 0, true);
        using var reader = table.CreateDataReader();
        reader.Read();
        analyzer.CaptureProjectedColumnLayout(reader);
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
