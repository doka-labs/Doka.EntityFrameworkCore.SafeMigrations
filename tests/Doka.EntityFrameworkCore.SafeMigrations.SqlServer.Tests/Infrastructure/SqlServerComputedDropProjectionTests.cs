namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises accepted native computed dependencies independently of a live engine.</summary>
public sealed partial class SqlServerComputedDropProjectionTests
{
    /// <summary>A sparse accepted generated column still protects its exact authored or live source.</summary>
    /// <param name="authored">Whether the source has an accepted authored definition.</param>
    /// <param name="dependent">Whether the requested DROP targets the computed source.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AcceptedComputedColumnProtectsOnlyItsCurrentSource(bool authored, bool dependent)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var flag = Flag();
        var bindings = new Dictionary<string, int> { ["L"] = 7, ["Payload"] = 8, };
        Capture(analyzer, flag, source, bindings, authored);
        var columns = new Source(flag, authored ? source : null);
        var drop = new SafeMigrationOperation(new DropColumnIntent(dependent ? "L" : "Payload", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = analyzer.ValidateCapturedProjection(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        if (dependent)
        {
            Assert.NotNull(analysis);
            Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
            Assert.Equal("projected_generated_column_drop_dependency", analysis.Code);
        }
        else
        {
            Assert.Null(analysis);
        }
    }

    /// <summary>Native column identities, not CLR case folding, determine a sparse source alias.</summary>
    /// <param name="identity">The independently captured current native identity of the requested spelling.</param>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(-1)]
    [InlineData(0)]
    public void SparseSourceAliasRequiresItsOriginalPositiveNativeIdentity(int identity)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var flag = Flag();
        var bindings = new Dictionary<string, int> { ["L"] = 7, };
        if (identity != 0)
        {
            bindings.Add("l", identity);
        }

        Capture(analyzer, flag, source, bindings, authored: false);
        var drop = new SafeMigrationOperation(new DropColumnIntent("l", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = analyzer.ValidateCapturedProjection(drop, Live(SafeMigrationObservedState.Matching),
            new Source(flag, null));

        // Assert
        Assert.Equal(identity == 7, analysis?.ObservedState == SafeMigrationObservedState.Different);
    }

    /// <summary>A removed or unaccepted generated definition cannot retain a source dependency.</summary>
    /// <param name="replaced">Whether another definition rather than a tombstone occupies the generated name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedOrRejectedComputedPrefixDoesNotBecomeAcceptedEvidence(bool replaced)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var flag = Flag();
        Capture(analyzer, flag, source, new Dictionary<string, int> { ["L"] = 7, }, authored: false);
        var columns = new Source(replaced ? Flag() : null, null);
        var drop = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = analyzer.ValidateCapturedProjection(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Null(analysis);
    }

    /// <summary>The computed member itself is not one of its sources and remains independently removable.</summary>
    [Fact]
    public void ComputedColumnItselfIsNotItsOwnDependency()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var flag = Flag();
        Capture(analyzer, flag, source, new Dictionary<string, int> { ["L"] = 7, }, authored: false);
        var drop = new SafeMigrationOperation(new DropColumnIntent(flag.Name, "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = analyzer.ValidateCapturedProjection(drop, Live(SafeMigrationObservedState.Matching),
            new Source(flag, null));

        // Assert
        Assert.Null(analysis);
    }

    /// <summary>Only an actual native computed reference receives the specific DROP classification code.</summary>
    [Fact]
    public void NativeDropClassifierDistinguishesComputedReferencesFromOtherBlockers()
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.True(plan.RequiresExpressionDependencyRead);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("generated_column_drop_dependency", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("JOIN sys.computed_columns", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("generated.column_id=d.referencing_minor_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("generated_column_drop_other_dependency", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("sys.index_columns", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains(SqlServerSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode,
            plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("sys.index_columns", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("N'SELECT'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
    }

    /// <summary>Owner identity moves only with a certified accepted table rename.</summary>
    /// <param name="accepted">Whether the planner accepted the exact rename.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedOwnerMovesOnlyAfterAcceptedRename(bool accepted)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var flag = Flag();
        Capture(analyzer, flag, source, new Dictionary<string, int> { ["L"] = 7, }, authored: false);
        var rename = new SafeMigrationOperation(new RenameTableIntent("nodes", "renamed", "dbo", "other_schema"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var state = Live(accepted ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Intent.Kind, state.ObservedState, rename.Policy);

        // Act
        analyzer.ObserveAcceptedOperation(rename, state, state, decision);
        var proof = Captures(analyzer)[flag][0];

        // Assert
        Assert.Equal(accepted, decision.ShouldExecute);
        Assert.Equal(accepted ? "renamed" : "nodes", proof.Table);
        Assert.Equal(accepted ? "other_schema" : null, proof.Schema);
        Assert.Equal(accepted, proof.Matches(new Source(flag, null), "renamed", "other_schema"));
        Assert.Equal(!accepted, proof.Matches(new Source(flag, null), "nodes", null));
    }

    /// <summary>Column presence never proves that an existing non-generated native blocker disappeared.</summary>
    /// <param name="generatedDependency">The independent current generated-family membership evidence.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGenericRefusalSurvivesExactSourcePresence(bool? generatedDependency)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "classified_different");

        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);
        var columns = new Source(null, new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            generatedDependency);

        // Act
        var result = analyzer.ValidateProjectedOperation(operation, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(native.Code, result.Code);
    }

    /// <summary>A source reassertion cannot erase an independently observed native generated dependency.</summary>
    /// <param name="complete">Whether current accepted membership proves complete generated absence.</param>
    /// <param name="mixed">Whether the native classifier also observed independent blockers.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeComputedRefusalRequiresCompleteCurrentDischarge(bool complete, bool mixed)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, mixed
                ? "generated_column_drop_other_dependency" : "generated_column_drop_dependency");

        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);

        var columns = new Source(null, new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            complete ? false : null);

        // Act
        var result = analyzer.ValidateProjectedOperation(operation, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(complete && !mixed ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(complete && !mixed ? "projected_generated_column_drop_safe" : native.Code,
            result.Code);
    }

    /// <summary>Only an exact matching DROP can transport complete native surviving generated absence.</summary>
    /// <param name="drop">Whether the certificate belongs to a DROP rather than another operation family.</param>
    /// <param name="matching">Whether the native source is present and safely removable.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NativeGeneratedCompletenessIsKindAndStateBound(bool drop, bool matching)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        SafeMigrationIntent intent = drop ? new DropColumnIntent("L", "nodes")
            : new EnsureColumnIntent("nodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"));

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var native = new SafeMigrationProviderAnalysis(matching
            ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false,
            SqlServerSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode);

        SafeMigrationProviderAnalysis[] results = [native];

        // Act
        var exception = Record.Exception(() => analyzer.CaptureProjectedComputedDropRefusals([operation], results));

        // Assert
        if (drop && matching)
        {
            Assert.Null(exception);
            Assert.Equal("classified_matching", results[0].Code);
            Assert.True(results[0].DropLeavesNoGeneratedColumns);
            Assert.False(native.DropLeavesNoGeneratedColumns);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Same(native, results[0]);
        }
    }

    /// <summary>An accepted source tombstone remains a NoOp despite its original generated dependency.</summary>
    [Fact]
    public void CurrentSourceAbsencePrecedesOriginalNativeRefusal()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_dependency");
        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);
        var missing = Live(SafeMigrationObservedState.Missing);

        // Act
        var result = analyzer.ValidateProjectedOperation(operation, missing, new Source(null, null));

        // Assert
        Assert.Same(missing, result);
    }

    /// <summary>
    /// Only accepted exact native removals with current absent identities discharge conditional blockers.
    /// </summary>
    /// <param name="accepted">Whether the exact predecessor established removal.</param>
    /// <param name="missing">Whether Core still proves the native index name absent.</param>
    /// <param name="recreated">Whether later accepted DDL created another physical blocker.</param>
    /// <param name="nativeMatching">Whether native capture proved the original independent index identity.</param>
    /// <param name="safe">Whether all independent and generated blockers have certified current absence.</param>
    [Theory]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, false, false, false)]
    public void ConditionalIndexDischargeRequiresAcceptedCurrentNativeAbsence(
        bool accepted,
        bool missing,
        bool recreated,
        bool nativeMatching,
        bool safe
    )
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var predecessor = new SafeMigrationOperation(new DropIndexIntent("IX_L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_computedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The conditional native predecessor capture is unavailable.");

        field.SetValue(analyzer, new Dictionary<SafeMigrationOperation, int[]> { [operation] = [0], });
        var nativeIndex = new SafeMigrationProviderAnalysis(nativeMatching
            ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "classified_matching")
        {
            MatchedObjectName = nativeMatching ? "IX_L" : null,
        };

        var nativeDrop = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_named_index_dependency");

        analyzer.CaptureProjectedComputedDropRefusals([predecessor, operation], [nativeIndex, nativeDrop]);
        var source = new Source(null, null, generatedDependency: false, indexMissing: missing);
        var state = Live(accepted ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
        var decision = SafeMigrationDecisionPlanner.Plan(predecessor.Intent.Kind, state.ObservedState,
            predecessor.Policy);

        var recreate = new SafeMigrationOperation(new EnsureIndexIntent(
            new ExpectedIndexDefinition("IX_L", "nodes", [new(column: "L")])), SafeMigrationPolicy.ThrowIfDifferent);

        var absent = Live(SafeMigrationObservedState.Missing);

        // Act
        analyzer.ObserveAcceptedOperation(predecessor, nativeIndex, state, decision);
        if (recreated)
        {
            analyzer.ObserveAcceptedOperation(recreate, absent, absent,
                SafeMigrationDecisionPlanner.Plan(recreate.Intent.Kind, absent.ObservedState, recreate.Policy));
        }

        var result = analyzer.ValidateProjectedOperation(operation, Live(SafeMigrationObservedState.Matching), source);

        // Assert
        Assert.Equal(safe ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(safe ? "projected_generated_column_drop_safe" : "generated_column_drop_other_dependency",
            result.Code);
    }

    /// <summary>Conditional native exclusions never omit standalone statistics or other blocker families.</summary>
    [Fact]
    public void NativeConditionalIndexCertificateExcludesOnlyExactIndexOwnedStatistics()
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation, projectedIndexDropCandidates: ["IX_L"]);

        // Assert
        Assert.Contains("generated_column_drop_named_index_dependency", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("candidate.index_id=sc.stats_id", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("candidate.object_id=sc.object_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("candidate.name IN(N'IX_L')", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("sys.default_constraints", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("sys.foreign_key_columns", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("sys.check_constraints", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.name", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>A new empty analysis cannot retain native refusals from a different invocation.</summary>
    [Fact]
    public async Task EmptyAnalysisClearsPriorComputedDropCertificates()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_dependency");
        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);
        var matching = Live(SafeMigrationObservedState.Matching);

        // Act
        var fresh = await analyzer.AnalyzeAsync(context, Array.Empty<SafeMigrationOperation>());
        var result = analyzer.ValidateProjectedOperation(operation, matching, new Source(null, null));

        // Assert
        Assert.Empty(fresh);
        Assert.Same(matching, result);
    }

    /// <summary>Opaque provider activity cannot reuse a historical zero-generated-set certificate.</summary>
    [Fact]
    public void OpaqueProviderBoundaryExpiresNativeComputedDischarge()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_dependency");
        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);

        // Act
        analyzer.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;", });
        var result = analyzer.ValidateProjectedOperation(operation, Live(SafeMigrationObservedState.Matching),
            new Source(null, null, generatedDependency: false));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("generated_column_drop_dependency", result.Code);
    }

    /// <summary>A fully recreated accepted owner cannot inherit an earlier native object's dependency.</summary>
    [Fact]
    public void RecreatedOwnerDoesNotInheritOriginalNativeBlocker()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operation = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_other_dependency");
        analyzer.CaptureProjectedComputedDropRefusals([operation], [native]);
        var matching = Live(SafeMigrationObservedState.Matching);

        // Act
        var result = analyzer.ValidateProjectedOperation(operation, matching,
            new Source(null, null, generatedDependency: false, newlyCreated: true));

        // Assert
        Assert.Same(matching, result);
    }

    /// <summary>Repeated predecessor names retain one latest exact witness instead of quadratic snapshots.</summary>
    [Fact]
    public void RepeatedIndexPredecessorsKeepOnlyTheLatestExactNameWitness()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = Enumerable.Range(0, 2048).Select(static _ => new SafeMigrationOperation(
            new DropIndexIntent("IX_L", "nodes"), SafeMigrationPolicy.ThrowIfDifferent)).ToList();

        var drop = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        operations.Add(drop);
        var method = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareComputedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The bounded predecessor preparation is unavailable.");
        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_computedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The bounded predecessor inventory is unavailable.");

        // Act
        _ = method.Invoke(analyzer, [operations]);
        var captured = (Dictionary<SafeMigrationOperation, int[]>)(field.GetValue(analyzer)
            ?? throw new InvalidOperationException("The bounded predecessor capture is unavailable."));

        // Assert
        Assert.Equal(2047, Assert.Single(captured[drop]));
        Assert.Single(captured);
    }

    /// <summary>A guarded computed-column removal cannot implicitly remove an unrelated native index.</summary>
    /// <param name="currentState">The current identity result, or null when neutral structure is sparse.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.Different)]
    public void NativeIndexPresenceSurvivesGuardedComputedRemoval(SafeMigrationObservedState? currentState)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var (index, native) = CaptureIndexWitness(analyzer);
        var removal = new SafeMigrationOperation(new DropColumnIntent("HasChildren", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matching = Live(SafeMigrationObservedState.Matching);
        analyzer.ObserveAcceptedOperation(removal, matching, matching,
            SafeMigrationDecisionPlanner.Plan(removal.Intent.Kind, matching.ObservedState, removal.Policy));

        // Act
        var result = analyzer.ValidateCapturedProjection(index, native,
            new Source(null, null, indexState: currentState));

        // Assert
        if (currentState == SafeMigrationObservedState.Different)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(currentState ?? SafeMigrationObservedState.Matching, result.ObservedState);
        }
    }

    /// <summary>Native index fallback expires for both ends of new FK edges, owners and opaque activity.</summary>
    /// <param name="scenario">The recreating, incoming, inline, opaque, new-owner or rejected mutation.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void NativeIndexFallbackRequiresUnexpiredAcceptedOwnerEpoch(int scenario)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var (index, native) = CaptureIndexWitness(analyzer);
        var foreignKey = new ExpectedForeignKeyDefinition("FK_parent", "children", ["ParentL"], "nodes", ["L"],
            principalSchema: scenario == 8 ? "other_schema" : null);

        SafeMigrationIntent intent = scenario switch
        {
            0 => new EnsureIndexIntent(new ExpectedIndexDefinition("IX_L", "nodes", [new(column: "L")])),
            1 => new DropTableIntent("nodes"),
            2 => new DropSchemaIntent("dbo"),
            3 or 7 or 8 => new EnsureForeignKeyIntent(foreignKey),
            4 => new EnsureTableIntent(new ExpectedTableDefinition("children",
                [new("ParentL", typeof(long), true, "bigint")], foreignKeys: [foreignKey]),
                SafeMigrationTableMode.StrictDefinition),
            _ => new DropTableIntent("unrelated"),
        };

        var mutation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var state = Live(scenario == 7 ? SafeMigrationObservedState.Different
            : scenario is 1 or 2 ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing);

        // Act
        if (scenario == 5)
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;", });
        }
        else if (scenario != 6)
        {
            analyzer.ObserveAcceptedOperation(mutation, state, state,
                SafeMigrationDecisionPlanner.Plan(mutation.Intent.Kind, state.ObservedState, mutation.Policy));
        }

        var result = analyzer.ValidateCapturedProjection(index, native,
            new Source(null, null, newlyCreated: scenario == 6));

        // Assert
        if (scenario is 7 or 8)
        {
            Assert.NotNull(result);
            Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
        }
        else
        {
            Assert.Null(result);
        }
    }

    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, false, "classified_" + state.ToString().ToLowerInvariant());

    private static ExpectedColumnDefinition Flag()
        => new("HasChildren", typeof(bool), false, "bit",
            computedColumnSql: "CONVERT(bit,CASE WHEN [L]>0 THEN 1 ELSE 0 END)");

    private static (SafeMigrationOperation Index, SafeMigrationProviderAnalysis Native) CaptureIndexWitness(
        SqlServerSafeMigrationProviderAnalyzer analyzer
    )
    {
        var index = new SafeMigrationOperation(new DropIndexIntent("IX_L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var source = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_computedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native index witness inventory is unavailable.");

        field.SetValue(analyzer, new Dictionary<SafeMigrationOperation, int[]> { [source] = [0], });
        var native = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "classified_matching")
        {
            MatchedObjectName = "IX_L",
        };

        var dependent = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "generated_column_drop_named_index_dependency");

        analyzer.CaptureProjectedComputedDropRefusals([index, source], [native, dependent]);

        return (index, native);
    }

    private static SafeMigrationDbContext Context()
        => new("Server=127.0.0.1,1433;Database=offline_computed_drop;User ID=sa;"
            + "Password=unused;TrustServerCertificate=True");

    private static void Capture(SqlServerSafeMigrationProviderAnalyzer analyzer, ExpectedColumnDefinition flag,
        ExpectedColumnDefinition source, Dictionary<string, int> bindings, bool authored)
    {
        // WHY: Inject only the completed native capture, not an analyzer result.
        // This exercises the real projection hook on ARM without introducing a
        // production test hook or pretending that metadata was queried natively.
        Captures(analyzer).Add(flag,
        [
            new(new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true),
                new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = authored ? source : null, }, "nodes")
            {
                LiveOperandIdentities = authored ? null : new Dictionary<string, int> { ["L"] = 7, },
                CurrentColumnBindings = bindings,
            },
        ]);
    }

    private static Dictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>> Captures(
        SqlServerSafeMigrationProviderAnalyzer analyzer
    )
    {
        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_projectedComputedKeys",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native computed proof inventory is unavailable.");

        return (Dictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>>)
            (field.GetValue(analyzer) ?? throw new InvalidOperationException("The native capture is unavailable."));
    }

    private sealed class Source(ExpectedColumnDefinition? flag, ExpectedColumnDefinition? source,
        bool? generatedDependency = null, bool? indexMissing = null, bool newlyCreated = false,
        SafeMigrationObservedState? indexState = null)
        : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool? HasProjectedGeneratedColumnDependency(string table, string? schema, string column,
            IEqualityComparer<string> columnComparer) => generatedDependency;

        public SafeMigrationProviderAnalysis? GetProjectedIndexIdentityAnalysis(ExpectedIndexDefinition definition,
            SafeMigrationProviderAnalysis liveAnalysis, bool includeConstraintBackingIndexNames = false)
            => indexState is { } state ? Live(state) : indexMissing is { } missing
                ? Live(missing ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching) : null;

        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = flag?.Name == column ? flag : source?.Name == column ? source : null;

            return definition is not null;
        }

        public bool TryGetProjectedTableState(string table, string? schema, out SafeMigrationProjectedTableState state)
        {
            state = new SafeMigrationProjectedTableState(newlyCreated, false, false, false);

            return true;
        }
    }
}
