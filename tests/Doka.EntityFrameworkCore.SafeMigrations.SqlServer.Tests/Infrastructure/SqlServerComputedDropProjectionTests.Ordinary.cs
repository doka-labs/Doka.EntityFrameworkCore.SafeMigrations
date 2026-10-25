namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises ordinary native blocker identities without requiring a database.</summary>
public sealed partial class SqlServerComputedDropProjectionTests
{
    /// <summary>Only exact accepted native predecessor removals discharge the conditional dependency proof.</summary>
    /// <param name="kind">The native CHECK, primary-key, unique-key or FK removal kind.</param>
    /// <param name="accepted">Whether the predecessor actually established accepted physical absence.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void OrdinaryConditionalProofRequiresAcceptedTypedRemoval(int kind, bool accepted)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var predecessor = ConstraintDrop(kind);
        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        PrepareOrdinary(analyzer, [predecessor, source]);
        var native = Live(SafeMigrationObservedState.Matching);
        analyzer.CaptureProjectedComputedDropRefusals([predecessor, source],
            [native, new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_candidate_dependency")]);

        var projected = Live(accepted ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
        var decision = SafeMigrationDecisionPlanner.Plan(predecessor.Intent.Kind, projected.ObservedState,
            predecessor.Policy);
        var columns = new Source(null, new ExpectedColumnDefinition("L", typeof(long), false, "bigint"));

        // Act
        analyzer.ObserveAcceptedOperation(predecessor, native, projected, decision);
        var result = analyzer.ValidateProjectedOperation(source, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(accepted, decision.ShouldExecute);
        Assert.Equal(accepted ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(accepted ? "projected_column_drop_safe" : "column_drop_dependency", result.Code);
    }

    /// <summary>Repeated native removals consume an accepted tombstone instead of restoring a stale Apply.</summary>
    [Fact]
    public void RepeatedConstraintRemovalKeepsAcceptedAbsence()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var first = ConstraintDrop(0);
        var second = ConstraintDrop(0);
        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        PrepareOrdinary(analyzer, [first, second, source]);
        var native = Live(SafeMigrationObservedState.Matching);
        analyzer.CaptureProjectedComputedDropRefusals([first, second, source],
            [native, native, new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_candidate_dependency")]);

        var columns = new Source(null, new ExpectedColumnDefinition("L", typeof(long), false, "bigint"));

        // Act
        analyzer.ObserveAcceptedOperation(first, native, native,
            SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, native.ObservedState, first.Policy));
        var repeated = analyzer.ValidateCapturedProjection(second, native, columns)
            ?? throw new InvalidOperationException("The exact native predecessor was not retained.");

        var decision = SafeMigrationDecisionPlanner.Plan(second.Intent.Kind, repeated.ObservedState, second.Policy);
        analyzer.ObserveAcceptedOperation(second, native, repeated, decision);
        var result = analyzer.ValidateProjectedOperation(source, native, columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, repeated.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>An accepted member replacement cannot inherit native dependencies of the removed column id.</summary>
    [Fact]
    public void AcceptedSourceReplacementEndsOriginalNativeBlockerLifetime()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var previous = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var definition = new ExpectedColumnDefinition("L", typeof(long), true, "bigint");
        var replacement = new SafeMigrationOperation(new EnsureColumnIntent("nodes", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var native = Live(SafeMigrationObservedState.Matching);
        analyzer.CaptureProjectedComputedDropRefusals([source],
            [new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_dependency")]);

        // Act
        analyzer.ObserveAcceptedOperation(previous, native, native,
            SafeMigrationDecisionPlanner.Plan(previous.Intent.Kind, native.ObservedState, previous.Policy));
        var missing = Live(SafeMigrationObservedState.Missing);
        analyzer.ObserveAcceptedOperation(replacement, missing, missing,
            SafeMigrationDecisionPlanner.Plan(replacement.Intent.Kind, missing.ObservedState, replacement.Policy));
        var result = analyzer.ValidateProjectedOperation(source, native, new Source(null, definition));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>
    /// Native exclusions bind exact constraint owners and retain unrelated default/statistics blockers.
    /// </summary>
    [Fact]
    public void OrdinaryConditionalSqlKeepsNativeIdentityAndRuntimeDependencyGuards()
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        SafeMigrationOperation[] removals =
            [ConstraintDrop(0), ConstraintDrop(1), ConstraintDrop(2), ConstraintDrop(3)];

        // Act
        var plan = catalog.Build(operation, projectedConstraintDropCandidates: removals);

        // Assert
        Assert.Contains("column_drop_candidate_dependency", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("removed_key.unique_index_id=ic.index_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("removed_key.unique_index_id=sc.stats_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("removed_fk.object_id=fkc.constraint_object_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("removed_check.object_id=d.referencing_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("removed_check.object_id=cc.object_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("sys.default_constraints", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("removed_key", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("removed_check", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("removed_fk", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Native dependency retention cannot erase a real provider or managed deferred-origin boundary.</summary>
    /// <param name="providerOrigin">Whether the provider rather than model-managed DML owns the uncertainty.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRefusalPreservesExistingDeferredOrigin(bool providerOrigin)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        analyzer.CaptureProjectedComputedDropRefusals([source],
            [new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_dependency")]);

        var unknown = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_structure_state_unknown")
        {
            ProviderDeferredOriginOrdinal = providerOrigin ? 4 : null,
            ModelManagedLocalOriginOrdinal = providerOrigin ? null : 4,
        };

        // Act
        var result = analyzer.ValidateProjectedOperation(source, unknown, new Source(null, null));

        // Assert
        Assert.Same(unknown, result);
        Assert.Equal(providerOrigin ? 4 : null, result.ProviderDeferredOriginOrdinal);
        Assert.Equal(providerOrigin ? null : 4, result.ModelManagedLocalOriginOrdinal);
    }

    /// <summary>Actual trigger-bearing DML defers native metadata refusal to its original runtime boundary.</summary>
    [Fact]
    public void NativeRefusalAfterTypedDmlRetainsTriggerOrigin()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "column_drop_dependency");

        analyzer.CaptureProjectedComputedDropRefusals([source], [native]);
        analyzer.CaptureProjectedDmlEffects(true);
        analyzer.SetCurrentOperationOrdinal(3);

        // Act
        analyzer.ObserveProviderOperation(new InsertDataOperation
        {
            Table = "other_table",
            Columns = ["Value"],
            Values = new object[,] { { 1 }, },
        });
        var result = analyzer.ValidateCapturedProjection(source, native, new Source(null, null));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("projected_dml_trigger_structure_unknown", result.Code);
        Assert.Equal(3, result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>
    /// Only exact INDEX-class filter dependencies can be conditionally excluded after their owned drop.
    /// </summary>
    [Fact]
    public void FilterDependencyRemovalBindsNativeClassOwnerAndIndexIdentity()
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation, projectedIndexDropCandidates: ["IX_filter"]);

        // Assert
        Assert.Contains("d.referencing_class=7 AND EXISTS", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("candidate.object_id=d.referencing_id AND candidate.index_id=d.referencing_minor_id",
            plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("candidate.name IN(N'IX_filter')", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("d.referencing_class=1 AND generated.object_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.name", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("sys.sql_expression_dependencies", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>
    /// New index or constraint DDL cannot turn a removed old dependency into a known current blocker.
    /// </summary>
    /// <param name="known">Whether the current native metadata proves the new index's member identity.</param>
    /// <param name="kind">The new index, CHECK, primary-key or UNIQUE constraint.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    public void IndependentIndexCreationPreservesExactRemovalOrDefersUnknownIdentity(bool known, int kind)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var predecessor = new SafeMigrationOperation(new DropIndexIntent("IX_L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        PrepareOrdinary(analyzer, [predecessor, source]);
        var nativeIndex = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "classified_matching") { MatchedObjectName = "IX_L", };

        analyzer.CaptureProjectedComputedDropRefusals([predecessor, source],
            [nativeIndex, new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_candidate_dependency")]);

        if (known)
        {
            CaptureOrdinaryBindings(analyzer);
        }

        SafeMigrationIntent intent = kind switch
        {
            0 => new EnsureIndexIntent(new ExpectedIndexDefinition("IX_R", "nodes", [new(column: "R")])),
            1 => new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression(
                "CK_R", "nodes", SafeMigrationSql.Binary(SafeMigrationSql.Identifier("R"),
                    SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0L)))),
            2 => new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("PK_R", "nodes", ["R"])),
            3 => new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("UQ_R", "nodes", ["R"])),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var create = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        var missing = Live(SafeMigrationObservedState.Missing);

        // Act
        analyzer.ObserveAcceptedOperation(predecessor, nativeIndex, nativeIndex,
            SafeMigrationDecisionPlanner.Plan(predecessor.Intent.Kind, nativeIndex.ObservedState, predecessor.Policy));
        analyzer.SetCurrentOperationOrdinal(6);
        analyzer.ObserveAcceptedOperation(create, missing, missing,
            SafeMigrationDecisionPlanner.Plan(create.Intent.Kind, missing.ObservedState, create.Policy));
        var result = analyzer.ValidateProjectedOperation(source, Live(SafeMigrationObservedState.Matching),
            new Source(null, null, indexMissing: true));

        // Assert
        Assert.Equal(known ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.PrerequisiteMissing,
            result.ObservedState);
        Assert.Equal(known ? "projected_column_drop_safe" : "projected_structure_state_unknown", result.Code);
        Assert.Equal(known ? null : 6, result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Native physical-name aliases require actual accepted removal and an unchanged owner epoch.</summary>
    /// <param name="recreated">Whether current identity evidence proves a same-name replacement.</param>
    /// <param name="expired">Whether opaque provider activity expired the native alias certificate.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AcceptedNativeIndexAliasDoesNotRequireClrNameEquality(bool recreated, bool expired)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var predecessor = new SafeMigrationOperation(new DropIndexIntent("ix_l", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        PrepareOrdinary(analyzer, [predecessor, source]);
        var nativeIndex = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "classified_matching") { MatchedObjectName = "IX_L", };

        analyzer.CaptureProjectedComputedDropRefusals([predecessor, source],
            [nativeIndex, new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                "column_drop_candidate_dependency")]);

        // Act
        analyzer.ObserveAcceptedOperation(predecessor, nativeIndex, nativeIndex,
            SafeMigrationDecisionPlanner.Plan(predecessor.Intent.Kind, nativeIndex.ObservedState, predecessor.Policy));
        if (expired)
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;", });
        }

        var result = analyzer.ValidateProjectedOperation(source, Live(SafeMigrationObservedState.Matching),
            new Source(null, null, indexState: recreated
                ? SafeMigrationObservedState.Different : SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(!recreated && !expired
            ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(!recreated && !expired ? "projected_column_drop_safe" : "column_drop_dependency", result.Code);
    }

    private static void CaptureOrdinaryBindings(SqlServerSafeMigrationProviderAnalyzer analyzer)
    {
        using var layout = new DataTable();
        layout.Columns.Add("schema", typeof(string));
        layout.Columns.Add("table", typeof(string));
        layout.Columns.Add("columns", typeof(int));
        layout.Columns.Add("fixed", typeof(int));
        layout.Columns.Add("bits", typeof(int));
        layout.Columns.Add("variables", typeof(int));
        layout.Columns.Add("clustered", typeof(int));
        layout.Columns.Add("known", typeof(bool));
        layout.Rows.Add("dbo", "nodes", 2, 16, 0, 0, 0, true);
        using var layoutReader = layout.CreateDataReader();
        layoutReader.Read();
        analyzer.CaptureProjectedColumnLayout(layoutReader);

        using var bindings = new DataTable();
        bindings.Columns.Add("schema", typeof(string));
        bindings.Columns.Add("table", typeof(string));
        bindings.Columns.Add("name", typeof(string));
        bindings.Columns.Add("identity", typeof(int));
        bindings.Columns.Add("variable", typeof(int));
        bindings.Columns.Add("clustered", typeof(bool));
        bindings.Rows.Add("dbo", "nodes", "L", 1, 0, false);
        bindings.Rows.Add("dbo", "nodes", "R", 2, 0, false);
        using var reader = bindings.CreateDataReader();
        while (reader.Read())
        {
            analyzer.CaptureProjectedColumnLayoutBinding(reader);
        }
    }

    private static SafeMigrationOperation ConstraintDrop(int kind)
        => new(kind switch
        {
            0 => new DropCheckConstraintIntent("CK_L", "nodes"),
            1 => new DropPrimaryKeyIntent("PK_L", "nodes"),
            2 => new DropUniqueConstraintIntent("UQ_L", "nodes"),
            3 => new DropForeignKeyIntent("FK_L", "nodes"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, SafeMigrationPolicy.ThrowIfDifferent);

    private static void PrepareOrdinary(SqlServerSafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations)
    {
        var method = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareComputedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The exact predecessor capture is unavailable.");

        method.Invoke(analyzer, [operations]);
        var constraints = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod(
            "PrepareColumnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The exact constraint predecessor capture is unavailable.");

        constraints.Invoke(analyzer, [operations]);
    }
}
