namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies metadata freshness after typed and safe DML can execute globally visible triggers.</summary>
public sealed class SqlServerDmlStructuralFreshnessTests
{
    private const string ConnectionString = "Server=localhost;Database=dml_structure;Integrated Security=true;";

    /// <summary>Every executable typed DML form invalidates unrelated matching metadata only when a trigger exists.</summary>
    /// <param name="writeKind">The typed EF data operation.</param>
    /// <param name="enabled">Whether the database snapshot contains an enabled SQL or CLR DML trigger.</param>
    /// <param name="hasRows">Whether EF emits any data commands.</param>
    [Theory]
    [InlineData("insert", true, true)]
    [InlineData("update", true, true)]
    [InlineData("delete", true, true)]
    [InlineData("insert", false, true)]
    [InlineData("update", false, true)]
    [InlineData("delete", false, true)]
    [InlineData("insert", true, false)]
    [InlineData("update", true, false)]
    [InlineData("delete", true, false)]
    public void TypedDml_GlobalTriggerRiskInvalidatesMatchingIndex(string writeKind, bool enabled, bool hasRows)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDmlEffects(enabled);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer) { CurrentOperationOrdinal = 11 };

        var data = hasRows ? new object?[,] { { 1 } } : new object?[0, 1];
        MigrationOperation write = writeKind switch
        {
            "insert" => new InsertDataOperation { Table = "source", Columns = ["Id"], Values = data },
            "update" => new UpdateDataOperation { Table = "source", KeyColumns = ["Id"], KeyValues = data,
                Columns = ["Id"], Values = data },
            "delete" => new DeleteDataOperation { Table = "source", KeyColumns = ["Id"], KeyValues = data },
            _ => throw new ArgumentOutOfRangeException(nameof(writeKind)),
        };

        var index = Index();
        var matching = Matching();

        // Act
        projection.ObserveProviderPostcondition(write);
        projection.CurrentOperationOrdinal = 12;
        var result = projection.Project(index, matching);

        // Assert
        if (enabled && hasRows)
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
            Assert.Equal("projected_dml_trigger_structure_unknown", result.Code);
            Assert.Equal(11, result.ProviderDeferredOriginOrdinal);
            Assert.False(result.RequiresLiveDataProof);
        }
        else
        {
            Assert.Same(matching, result);
            Assert.Null(result.ProviderDeferredOriginOrdinal);
        }
    }

    /// <summary>Safe model-managed writes invalidate structure only when the accepted decision executes.</summary>
    /// <param name="executes">Whether the accepted seed operation physically writes rather than matching as NoOp.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SafeDml_NoOpRetainsMetadataFreshness(bool executes)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDmlEffects(true);
        analyzer.SetCurrentOperationOrdinal(3);
        var seed = new SafeMigrationOperation(new EnsureModelManagedDataIntent("source", ["Id"], ["int"],
            ["Id"], ["int"], new object?[,] { { 1 } }, null, null), SafeMigrationPolicy.ThrowIfDifferent);

        var seedState = new SafeMigrationProviderAnalysis(executes ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, !executes, "seed_state");

        var index = Index();
        var matching = Matching();

        // Act
        analyzer.ObserveAcceptedOperation(seed, seedState, seedState,
            SafeMigrationDecisionPlanner.Plan(seed.Intent.Kind, seedState.ObservedState, seed.Policy));
        var result = analyzer.ValidateCapturedProjection(index, matching, new EmptyColumns());

        // Assert
        if (executes)
        {
            Assert.NotNull(result);
            Assert.Equal("projected_dml_trigger_structure_unknown", result.Code);
            Assert.Equal(3, result.ProviderDeferredOriginOrdinal);
        }
        else
        {
            Assert.Null(result);
        }
    }

    /// <summary>Trigger risk never turns an invariant permission rejection into a deferred approval.</summary>
    [Fact]
    public void DmlMutation_PreservesInvariantPermissionRejection()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDmlEffects(true);
        analyzer.SetCurrentOperationOrdinal(2);
        var rejected = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, "catalog_metadata_not_visible") { IsInvariantUnsupported = true };

        // Act
        analyzer.ObserveProviderOperation(new DeleteDataOperation { Table = "source", KeyColumns = ["Id"],
            KeyValues = new object?[,] { { 1 } } });
        var result = analyzer.ValidateCapturedProjection(Index(), rejected, new EmptyColumns());

        // Assert
        Assert.NotNull(result);
        Assert.Same(rejected, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Typed ALTER's actual EF default backfill can execute a metadata-mutating UPDATE trigger.</summary>
    /// <param name="hasDefault">Whether the baseline emits an UPDATE to fill NULL values.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedAlter_DefaultBackfillInvalidatesMatchingMetadata(bool hasDefault)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDmlEffects(true);
        var alteration = new AlterColumnOperation { Table = "source", Name = "Value", ClrType = typeof(int),
            ColumnType = "int", IsNullable = false, DefaultValue = hasDefault ? 0 : null,
            OldColumn = new AddColumnOperation { Table = "source", Name = "Value", ClrType = typeof(int),
                ColumnType = "int", IsNullable = true } };

        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer) { CurrentOperationOrdinal = 6 };

        var matching = Matching();

        // Act
        var baseline = context.GetService<ISqlServerSafeMigrationsBaselineGenerator>().Generate([alteration]);
        var sql = string.Join("\n", baseline.Select(static command => command.CommandText));
        projection.ObserveProviderPostcondition(alteration);
        projection.CurrentOperationOrdinal = 7;
        var result = projection.Project(Index(), matching);

        // Assert
        if (hasDefault)
        {
            Assert.Contains("UPDATE [source] SET [Value] = 0", sql, StringComparison.Ordinal);
            Assert.Equal("projected_dml_trigger_structure_unknown", result.Code);
            Assert.Equal(6, result.ProviderDeferredOriginOrdinal);
        }
        else
        {
            Assert.DoesNotContain("UPDATE [source]", sql, StringComparison.Ordinal);
            Assert.Same(matching, result);
        }
    }

    /// <summary>EF's implicit dbo schema ensure emits no DDL and must not replace the actual freshness origin.</summary>
    /// <param name="schema">The spelling EF explicitly treats as the existing default schema.</param>
    /// <param name="earlierDrop">Whether an actual earlier DDL operation already invalidated the snapshot.</param>
    [Theory]
    [InlineData("dbo", false)]
    [InlineData("DBO", false)]
    [InlineData("dbo", true)]
    [InlineData("DBO", true)]
    public void TypedDboEnsure_ZeroCommandBaselineRetainsFreshness(string schema, bool earlierDrop)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.EnabledTrigger);
        var operation = new EnsureSchemaOperation { Name = schema };
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer) { CurrentOperationOrdinal = 2 };

        var matching = Matching();

        // Act
        var baseline = context.GetService<ISqlServerSafeMigrationsBaselineGenerator>().Generate([operation]);
        if (earlierDrop)
        {
            projection.CurrentOperationOrdinal = 1;
            projection.ObserveProviderPostcondition(new DropIndexOperation { Name = "IX_old", Table = "source" });
            projection.CurrentOperationOrdinal = 2;
        }

        projection.ObserveProviderPostcondition(operation);
        projection.CurrentOperationOrdinal = 3;
        var result = projection.Project(Index(), matching);

        // Assert
        Assert.Empty(baseline);
        if (earlierDrop)
        {
            Assert.Equal("projected_ddl_trigger_structure_unknown", result.Code);
            Assert.Equal(1, result.ProviderDeferredOriginOrdinal);
        }
        else
        {
            Assert.Same(matching, result);
            Assert.Null(result.ProviderDeferredOriginOrdinal);
        }
    }

    /// <summary>Certified safe integer and text tightening suppress the UPDATE and retain metadata freshness.</summary>
    /// <param name="text">Whether the approved source/target transition is text rather than integer widening.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeAlter_ProvenNullAbsenceEmitsNoTriggeringBackfill(bool text)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDmlEffects(true);
        var source = text
            ? new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(40)")
            : new ExpectedColumnDefinition("Value", typeof(int), true, "int", defaultValue: SafeMigrationDefaultValue.Literal(0));

        var target = text
            ? new ExpectedColumnDefinition("Value", typeof(string), false, "varchar(80)")
            : new ExpectedColumnDefinition("Value", typeof(long), false, "bigint", defaultValue: SafeMigrationDefaultValue.Literal(0L));

        var operation = new SafeMigrationOperation(new AlterColumnIntent("source", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        var safe = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "proven_source");

        analyzer.SetCurrentOperationOrdinal(2);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation]);
        var sql = string.Join("\n", commands.Select(static command => command.CommandText));
        analyzer.ObserveAcceptedOperation(operation, safe, safe,
            SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, safe.ObservedState, operation.Policy,
                safe.RepairCapability));
        var result = analyzer.ValidateCapturedProjection(Index(), Matching(), new EmptyColumns());

        // Assert
        Assert.Contains("ALTER COLUMN [Value]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [source]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [dbo].[source]", sql, StringComparison.Ordinal);
        Assert.Null(result);
    }

    /// <summary>Creates the provider analyzer from the real SQL Server service registrations.</summary>
    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation Index()
        => new(new EnsureIndexIntent(new ExpectedIndexDefinition("IX_matching", "unrelated",
            [new ExpectedIndexKeyDefinition("Value")])), SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis Matching()
        => new(SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "classified_matching");

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = null;

            return false;
        }
    }
}
