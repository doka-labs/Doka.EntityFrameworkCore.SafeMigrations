namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies live-parent prerequisites for new children and new operands on existing children.</summary>
public sealed partial class SqlServerStandaloneForeignKeyProjectionTests
{
    /// <summary>Checks schema-name occupancy before requalifying either missing FK state.</summary>
    /// <param name="state">The provider classification before ordered standalone validation.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing)]
    public async Task StandaloneForeignKey_OccupiedNameRejectsBothMissingStates(SafeMigrationObservedState state)
    {
        // Arrange
        await using var connection = new GraphConnection(true, "C");
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        var projected = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false,
            state == SafeMigrationObservedState.PrerequisiteMissing
                ? "projected_structure_state_unknown" : "classified_missing");

        // Act
        var result = graph.ValidateProjectedOperation(ensure, projected, projection);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("projected_constraint_name_occupied", result.Code);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Decision(ensure, result).Action);
        Assert.False(result.RequiresLiveDataProof);
    }

    /// <summary>
    /// Preserves ordinary Missing and complete captured storage/row proof when the requested name is free.
    /// </summary>
    /// <param name="state">The provider classification before ordered standalone validation.</param>
    /// <param name="code">The expected preserved or requalified classifier.</param>
    /// <param name="requiresLiveDataProof">Whether requalification retains the native runtime data guard.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, "classified_missing", false)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, "projected_foreign_key_prerequisites_ready", true)]
    public async Task StandaloneForeignKey_FreeNamePreservesMissingAndRequalifiedProof(
        SafeMigrationObservedState state,
        string code,
        bool requiresLiveDataProof
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        var projected = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false,
            state == SafeMigrationObservedState.PrerequisiteMissing
                ? "projected_structure_state_unknown" : "classified_missing");

        // Act
        var result = graph.ValidateProjectedOperation(ensure, projected, projection);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
        Assert.Equal(code, result.Code);
        Assert.Equal(SafeMigrationAction.Apply, Decision(ensure, result).Action);
        Assert.Equal(requiresLiveDataProof, result.RequiresLiveDataProof);
    }

    /// <summary>An accepted empty new owner can add operands before referencing an untouched native parent.</summary>
    /// <param name="composite">Whether an earlier CREATE operand participates in the composite FK.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseForeignKey_NewEmptyOwnerAndLaterNullableOperandRetainLiveParentPrerequisites(
        bool composite
    )
    {
        // Arrange
        string[] principal = composite ? ["TenantId", "Id"] : ["Id"];

        await using var connection = new GraphConnection(true, principalColumns: principal);
        var create = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition(composite ? "TenantId" : "Id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition));

        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var index = Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("IX_child", "child",
            [new ExpectedIndexKeyDefinition("ParentId")])));

        var ensure = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("FK_child", "child",
            composite ? ["TenantId", "ParentId"] : ["ParentId"], "parent", principal)));
        var graph = await ReadGraph(connection, [create, add, index, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        Accept(projection, create, Missing());
        Accept(projection, add, Missing());
        Accept(projection, index, Missing());

        // Act
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, Decision(ensure, analysis).Action);
    }

    /// <summary>Future, rejected, removed, or opaque owners cannot activate a missing-owner hypothesis.</summary>
    /// <param name="mutation">The ordered owner boundary invalidating the sparse certificate.</param>
    [Theory]
    [InlineData("future")]
    [InlineData("rejected")]
    [InlineData("drop")]
    [InlineData("opaque")]
    public async Task SparseForeignKey_NewOwnerRequiresCurrentAcceptedLifetime(
        string mutation
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]), SafeMigrationTableMode.StrictDefinition));

        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, add, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        if (mutation == "rejected")
        {
            Accept(projection, create, Live(SafeMigrationObservedState.Different));
        }
        else if (mutation != "future")
        {
            Accept(projection, create, Missing());
        }

        Accept(projection, add, Missing());
        if (mutation == "drop")
        {
            Accept(projection, Operation(new DropTableIntent("child")), Live(SafeMigrationObservedState.Matching));
        }
        else if (mutation == "opaque")
        {
            graph.ObserveProviderOperation(new SqlOperation { Sql = "UPDATE dbo.child SET Id = Id;", });
        }

        // Act
        var candidates = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Null(candidates);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Decision(ensure, analysis).Action);
    }


    /// <summary>A rejected or existence-only proposal cannot replace an accepted operand's physical proof.</summary>
    /// <param name="existenceOnly">Whether the later different proposal is an accepted existence-only no-op.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SparseForeignKey_PreservesAcceptedDefinitionAcrossLaterDifferentProposal(
        bool existenceOnly
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var proposal = new SafeMigrationOperation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(string), true, "nvarchar(20)")),
            existenceOnly ? SafeMigrationPolicy.ExistenceOnly : SafeMigrationPolicy.ThrowIfDifferent);

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [add, proposal, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        Accept(projection, add, Missing());

        // Act
        var proposed = projection.Project(proposal, Missing());
        var decision = Decision(proposal, proposed);
        projection.Observe(proposal, Missing(), proposed, decision);
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, proposed.ObservedState);
        Assert.Equal(existenceOnly ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, Decision(ensure, analysis).Action);
    }

    /// <summary>Independent composite alternatives cannot certify an unaccepted valid proposal.</summary>
    /// <param name="acceptedCompatible">Whether every actual accepted operand has compatible native storage.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SparseCompositeAlternatives_BoundCaptureAndRequireEveryActualAcceptedDefinition(
        bool acceptedCompatible
    )
    {
        // Arrange
        const int count = 16;
        var names = Enumerable.Range(0, count).Select(static ordinal => "Id" + ordinal).ToArray();
        await using var connection = new GraphConnection(true, existingChild: true, principalColumns: names);
        var operations = new List<SafeMigrationOperation>();
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var compatible = acceptedCompatible || ordinal != 0;
            operations.Add(Operation(new EnsureColumnIntent("child",
                new ExpectedColumnDefinition(names[ordinal], compatible ? typeof(int) : typeof(string), true,
                    compatible ? "int" : "nvarchar(20)"))));
            operations.Add(new SafeMigrationOperation(new EnsureColumnIntent("child",
                new ExpectedColumnDefinition(names[ordinal], compatible ? typeof(string) : typeof(int), true,
                    compatible ? "nvarchar(20)" : "int")), SafeMigrationPolicy.ExistenceOnly));
        }

        var ensure = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("FK_child", "child",
            names, "parent", names)));

        operations.Add(ensure);
        var graph = await ReadGraph(connection, operations);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        foreach (var operation in operations.Take(count * 2))
        {
            var accepted = projection.Project(operation, Missing());
            projection.Observe(operation, Missing(), accepted, Decision(operation, accepted));
        }

        // Act
        var candidates = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());
        var captures = connection.Statements.Sum(static sql => System.Text.RegularExpressions.Regex.Count(sql,
            @"CONVERT\(bit, CASE WHEN [01] = 1"));

        // Assert
        Assert.Equal(count * 2, captures);
        Assert.Equal(acceptedCompatible ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        if (acceptedCompatible)
        {
            Assert.NotNull(candidates);
            Assert.Single(candidates);
        }
        else
        {
            Assert.Null(candidates);
        }

        Assert.All(connection.Statements, static sql => Assert.InRange(Encoding.UTF8.GetByteCount(sql), 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    /// <summary>The isolated three-operation stream fails without the live bridge and succeeds only with it.</summary>
    /// <param name="liveBridge">Whether independently captured live endpoint facts are available to Core.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseForeignKey_CounterfactualRetainsOriginalMissingPrerequisite(
        bool liveBridge
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var index = Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("IX_child", "child",
            [new ExpectedIndexKeyDefinition("ParentId")])));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [add, index, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: liveBridge ? graph : null);

        Accept(projection, add, Missing());
        Accept(projection, index, Missing());

        // Act
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(liveBridge ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
        Assert.Equal(liveBridge ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            Decision(ensure, analysis).Action);
    }

    /// <summary>Equal authored CLR/null-collation facets cannot override failed native qualification.</summary>
    [Fact]
    public async Task SparseForeignKey_AllAuthoredDefinitionsStillRequireCapturedNativeCompatibility()
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true, captureReadyOverride: false);
        var principal = Operation(new EnsureColumnIntent("parent",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")));

        var key = Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("PK_parent", "parent",
            ["Id"])));

        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [principal, key, add, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        var columnSource = (ISafeMigrationProjectedColumnSource)projection;

        Accept(projection, principal, Live(SafeMigrationObservedState.Matching));
        Accept(projection, key, Live(SafeMigrationObservedState.Matching));
        Accept(projection, add, Missing());

        // Act
        var requiresNative = graph.RequiresLiveForeignKeyQualification((EnsureForeignKeyIntent)ensure.Intent);
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.True(requiresNative);
        Assert.True(columnSource.TryGetProjectedColumn("parent", null, "Id", out _));
        Assert.True(columnSource.TryGetProjectedColumn("child", null, "ParentId", out _));
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Decision(ensure, analysis).Action);
    }

    /// <summary>Proves captured sparse endpoints independently of a synthetic parent convergence operation.</summary>
    /// <param name="hasKey">Whether the live principal owns a usable candidate key.</param>
    /// <param name="compatible">Whether the projected dependent matches captured principal storage.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NewNullableColumnAndLivePrincipal_CaptureMetadataWithoutParentConvergence(
        bool hasKey,
        bool compatible
    )
    {
        // Arrange
        await using var connection = new GraphConnection(hasKey, existingChild: true);
        var definition = new ExpectedColumnDefinition("ParentId", compatible ? typeof(int) : typeof(long),
            true, compatible ? "int" : "bigint");

        var add = Operation(new EnsureColumnIntent("child", definition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [add, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        Accept(projection, add, Missing());

        // Act
        var candidates = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);
        var principalPresent = graph.HasLiveColumn("parent", null, "Id");
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.True(principalPresent);
        Assert.Equal(hasKey && compatible ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        if (compatible)
        {
            Assert.NotNull(candidates);
            Assert.Equal(hasKey ? 1 : 0, candidates.Count);
            if (hasKey)
            {
                var candidate = Assert.Single(candidates);
                Assert.Equal(SafeMigrationDatabaseObjectKind.PrimaryKey, candidate.Kind);
                Assert.Equal("PK_parent", candidate.PhysicalName);
                Assert.Equal("PK_parent", candidate.BackingIndexName);
            }
        }
        else
        {
            Assert.Null(candidates);
        }
    }

    /// <summary>Initial native endpoint facts cannot survive accepted storage drops or an opaque boundary.</summary>
    /// <param name="mutation">The ordered mutation invalidating one independently captured prerequisite.</param>
    [Theory]
    [InlineData("parent-drop-key")]
    [InlineData("parent-drop-column")]
    [InlineData("parent-alter-column")]
    [InlineData("parent-drop-table")]
    [InlineData("child-drop-column")]
    [InlineData("child-rename-table")]
    [InlineData("raw-sql")]
    public async Task SparseLivePrerequisites_DoNotSurviveAcceptedMutation(
        string mutation
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var changes = Mutations(mutation);
        var graph = await ReadGraph(connection, new[] { add }.Concat(changes).Append(ensure).ToArray());
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, add, Missing());
        foreach (var change in changes)
        {
            Accept(projection, change, change.Intent is AlterColumnIntent
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.Safe, false, "classified_different")
                : Live(SafeMigrationObservedState.Matching));
        }

        if (mutation == "raw-sql")
        {
            graph.ObserveProviderOperation(new SqlOperation { Sql = "UPDATE dbo.child SET ParentId=999;" });
        }

        // Act
        var candidates = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);

        // Assert
        Assert.True(candidates is null || candidates.Count == 0);
    }

    /// <summary>Capturing a planned operand never substitutes for its accepted ordered definition.</summary>
    /// <param name="accepted">Whether the exact ADD has an accepted decision.</param>
    /// <param name="sameDefinition">Whether the accepted definition equals the metadata capture operand.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SparseLivePrerequisites_RequireAcceptedExactOperand(
        bool accepted,
        bool sameDefinition
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [add, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        if (accepted)
        {
            var replacement = sameDefinition ? add : Operation(new EnsureColumnIntent("child",
                new ExpectedColumnDefinition("ParentId", typeof(long), true, "bigint")));

            Accept(projection, replacement, Missing());
        }

        // Act
        var candidates = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);

        // Assert
        Assert.Null(candidates);
    }

    /// <summary>Newly NULL operands cannot erase schema-global name occupancy from the graph.</summary>
    /// <param name="kind">The existing native schema object occupying the requested FK name.</param>
    [Theory]
    [InlineData("F")]
    [InlineData("PK")]
    [InlineData("UQ")]
    [InlineData("C")]
    [InlineData("U")]
    public async Task SparseForeignKey_PreservesSchemaNameCollisionPriority(
        string kind
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, kind, existingChild: true);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [add, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        Accept(projection, add, Missing());

        // Act
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal("projected_constraint_name_occupied", analysis.Code);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Decision(ensure, analysis).Action);
    }

    /// <summary>Sparse FKs reuse bounded capture without retaining unrelated authored columns.</summary>
    [Fact]
    public async Task SparseForeignKeys_ShareBoundedMetadataAndIgnoreUnrelatedColumns()
    {
        // Arrange
        const int count = 65;
        await using var connection = new GraphConnection(true, existingChild: true);
        var operations = new List<SafeMigrationOperation>();
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var table = "child_" + ordinal;
            operations.Add(Operation(new EnsureColumnIntent(table,
                new ExpectedColumnDefinition("Unrelated", typeof(string), true, "nvarchar(max)"))));
            operations.Add(Operation(new EnsureColumnIntent(table,
                new ExpectedColumnDefinition("ParentId", typeof(int), true, "int"))));
            operations.Add(Operation(new EnsureForeignKeyIntent(ForeignKey(table))));
        }

        // Act
        await ReadGraph(connection, operations);

        // Assert
        Assert.Equal(1, connection.Statements.Count(static sql => sql.StartsWith("SELECT i.object_id",
            StringComparison.Ordinal)));
        Assert.Equal(3, connection.Statements.Count(static sql => sql.Contains("CONVERT(bit, CASE WHEN 1 = 1",
            StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.Statements, static sql => sql.Contains("Unrelated", StringComparison.Ordinal));
        Assert.All(connection.Statements, static sql => Assert.InRange(Encoding.UTF8.GetByteCount(sql), 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    /// <summary>Native constraint and index aliases disappear together with either accepted drop.</summary>
    /// <param name="keyKind">The actual native candidate-key kind.</param>
    /// <param name="dropBacking">Whether the accepted drop targets its backing index name.</param>
    [Theory]
    [InlineData("PK", false)]
    [InlineData("PK", true)]
    [InlineData("UQ", false)]
    [InlineData("UQ", true)]
    [InlineData("IX", true)]
    public async Task SparseLiveCandidateKey_RemovesEveryAliasOfTheDroppedBackingIdentity(
        string keyKind,
        bool dropBacking
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, existingChild: true, keyKind: keyKind);
        var add = Operation(new EnsureColumnIntent("child",
            new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var drop = Operation(dropBacking ? new DropIndexIntent("IX_parent", "parent")
            : keyKind == "PK" ? new DropPrimaryKeyIntent("PK_parent", "parent")
            : new DropUniqueConstraintIntent("UQ_parent", "parent"));

        var graph = await ReadGraph(connection, [add, drop, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph,
            livePrerequisiteSource: graph);

        Accept(projection, add, Missing());

        // Act
        var original = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);
        Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
        var afterDrop = graph.GetLiveForeignKeyCandidateKeys((EnsureForeignKeyIntent)ensure.Intent, projection);
        var analysis = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SafeMigrationLiveCandidateKey>>(original));
        Assert.Equal(keyKind == "PK" ? SafeMigrationDatabaseObjectKind.PrimaryKey
            : keyKind == "UQ" ? SafeMigrationDatabaseObjectKind.UniqueConstraint
            : SafeMigrationDatabaseObjectKind.Index,
            candidate.Kind);
        Assert.Equal(keyKind == "IX" ? "IX_parent" : keyKind + "_parent", candidate.PhysicalName);
        Assert.Equal(keyKind == "IX" ? null : "IX_parent", candidate.BackingIndexName);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<SafeMigrationLiveCandidateKey>>(afterDrop));
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// Only a present candidate key with captured compatible storage can discharge Core's missing snapshot.
    /// </summary>
    /// <param name="hasKey">Whether the live principal has a surviving unique candidate key.</param>
    /// <param name="compatible">Whether the authored child's integer storage matches the live principal.</param>
    /// <param name="ready">Whether the resulting FK has a complete ordered prerequisite proof.</param>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task NewChildAndLivePrincipal_RequireCapturedKeyAndStorage(
        bool hasKey,
        bool compatible,
        bool ready
    )
    {
        // Arrange
        await using var connection = new GraphConnection(hasKey);
        var child = Child(compatible ? "int" : "bigint");
        var create = Operation(new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        var foreign = ForeignKey();
        var ensure = Operation(new EnsureForeignKeyIntent(foreign));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(ready ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            result.ObservedState);
        Assert.Equal(ready ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            Decision(ensure, result).Action);
        Assert.Equal(1, connection.Statements.Count(static sql => sql.StartsWith("SELECT i.object_id",
            StringComparison.Ordinal)));
        Assert.Equal(1, connection.Statements.Count(static sql => sql.StartsWith("SELECT 0, CONVERT(bit",
            StringComparison.Ordinal)));
        if (ready)
        {
            Assert.Equal("projected_foreign_key_prerequisites_ready", result.Code);
            Assert.True(result.RequiresLiveDataProof);
            Assert.False(result.IsOpaqueProjectionUnknown);
        }
    }

    /// <summary>A captured future source cannot authorize a foreign key before that child was accepted.</summary>
    /// <param name="matchingChild">Whether a matching NoOp, rather than no earlier decision, precedes the FK.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnacceptedOrMatchingChild_CannotActivateCapturedPrerequisites(
        bool matchingChild
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        if (matchingChild)
        {
            Accept(projection, create, Live(SafeMigrationObservedState.Matching));
        }

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
    }

    /// <summary>Mutated column identities and rows cannot retain an empty-child FK certificate.</summary>
    /// <param name="mutation">The accepted operation that invalidates one captured prerequisite.</param>
    [Theory]
    [InlineData("child-type")]
    [InlineData("child-drop-column")]
    [InlineData("child-rename-column")]
    [InlineData("child-rename-table")]
    [InlineData("parent-drop-column")]
    [InlineData("parent-replace-column")]
    [InlineData("parent-alter-column")]
    [InlineData("parent-drop-table")]
    [InlineData("parent-rename-table")]
    [InlineData("parent-drop-key")]
    [InlineData("parent-replace-key-other-columns")]
    [InlineData("managed-data")]
    [InlineData("raw-sql")]
    public async Task AcceptedMutation_DoesNotReuseCapturedStorageOrRowProof(
        string mutation
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var changes = Mutations(mutation);
        var graph = await ReadGraph(connection, new[] { create }.Concat(changes).Append(ensure).ToArray());
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        foreach (var change in changes)
        {
            var state = change.Intent is EnsureColumnIntent or EnsurePrimaryKeyIntent
                or EnsureModelManagedDataIntent ? Missing()
                : change.Intent is AlterColumnIntent
                    ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.Safe, false, "classified_different")
                    : Live(SafeMigrationObservedState.Matching);

            Accept(projection, change, state);
        }

        if (mutation == "raw-sql")
        {
            var sql = new SqlOperation { Sql = "UPDATE dbo.child SET ParentId=999;" };
            projection.ObserveProviderPostcondition(sql);
        }

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.NotEqual(SafeMigrationObservedState.Missing, result.ObservedState);
        Assert.NotEqual(SafeMigrationAction.Apply, Decision(ensure, result).Action);
    }

    /// <summary>An accepted equivalent key replacement remains valid on unchanged captured principal columns.</summary>
    [Fact]
    public async Task EquivalentAcceptedPrincipalKeyReplacement_RetainsStorageProof()
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var drop = Operation(new DropPrimaryKeyIntent("PK_parent", "parent"));
        var replacement = Operation(new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("PK_replacement", "parent", ["Id"])));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, drop, replacement, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
        Accept(projection, replacement, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, Decision(ensure, result).Action);
    }

    /// <summary>Any existing schema object retains the runtime's schema-global name precedence.</summary>
    /// <param name="kind">The physical sys.objects kind already occupying the desired FK name.</param>
    [Theory]
    [InlineData("F")]
    [InlineData("PK")]
    [InlineData("UQ")]
    [InlineData("C")]
    [InlineData("U")]
    [InlineData("V")]
    [InlineData("P")]
    public async Task ExistingSchemaObjectName_BlocksNewChildForeignKey(
        string kind
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, kind);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("projected_constraint_name_occupied", result.Code);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Decision(ensure, result).Action);
    }

    /// <summary>Only a matching accepted drop of the exact captured owner and object kind frees the name.</summary>
    /// <param name="owner">The table targeted by the accepted DROP.</param>
    /// <param name="kind">The constraint family targeted by the DROP.</param>
    /// <param name="occupiedKind">The physical constraint family owning the live namespace entry.</param>
    /// <param name="ready">Whether the captured physical FK was actually removed.</param>
    [Theory]
    [InlineData("other", "F", "F", true)]
    [InlineData("parent", "F", "F", false)]
    [InlineData("other", "C", "F", false)]
    [InlineData("other", "C", "C", true)]
    [InlineData("parent", "C", "C", false)]
    public async Task ExistingNamespaceOwner_RequiresExactAcceptedDrop(
        string owner,
        string kind,
        string occupiedKind,
        bool ready
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true, occupiedKind);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var drop = Operation(kind == "F" ? new DropForeignKeyIntent("FK_child", owner)
            : new DropCheckConstraintIntent("FK_child", owner));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, drop, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        Accept(projection, drop, Live(SafeMigrationObservedState.Matching));

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(ready ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Previously accepted table and cross-kind constraints also own their schema-global names.</summary>
    /// <param name="kind">The accepted schema object kind occupying the future FK name.</param>
    /// <param name="sameChild">Whether the constraint belongs to the FK source rather than an unrelated table.</param>
    [Theory]
    [InlineData("PK", true)]
    [InlineData("PK", false)]
    [InlineData("UQ", true)]
    [InlineData("UQ", false)]
    [InlineData("C", true)]
    [InlineData("C", false)]
    [InlineData("U", false)]
    public async Task AcceptedSchemaObjectName_BlocksNewChildForeignKey(
        string kind,
        bool sameChild
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var table = kind == "U" ? "FK_child" : sameChild ? "child" : "other_new";
        var occupied = Operation(new EnsureTableIntent(OccupiedTable(table, kind),
            SafeMigrationTableMode.StrictDefinition));

        var create = sameChild ? occupied
            : Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));

        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        SafeMigrationOperation[] operations = sameChild ? [create, ensure] : [occupied, create, ensure];
        var graph = await ReadGraph(connection, operations);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        if (!sameChild)
        {
            Accept(projection, occupied, Missing());
        }

        Accept(projection, create, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
    }

    /// <summary>Transfers preserve current accepted ownership without resurrecting a previously dropped name.</summary>
    /// <param name="dropBeforeTransfer">Whether the original owning key was removed before ALTER SCHEMA.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaTransfer_MovesOnlySurvivingConstraintNames(
        bool dropBeforeTransfer
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var owner = Operation(new EnsureTableIntent(OccupiedTable("other_new", "PK"),
            SafeMigrationTableMode.StrictDefinition));

        var drop = Operation(new DropPrimaryKeyIntent("FK_child", "other_new"));
        var rename = Operation(new RenameTableIntent("other_new", "other_new", newSchema: "target"));
        var create = Operation(new EnsureTableIntent(Child(schema: "target"), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey(schema: "target")));
        var graph = await ReadGraph(connection, [owner, drop, rename, create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, owner, Missing());
        if (dropBeforeTransfer)
        {
            Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
        }

        Accept(projection, rename, Live(SafeMigrationObservedState.Matching));
        Accept(projection, create, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(dropBeforeTransfer ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>A successful capture belongs to its dependent definition, not just a reused FK object.</summary>
    [Fact]
    public async Task ReusedForeignKeyDefinition_DoesNotRebindEarlierCompatibleCapture()
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var first = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var drop = Operation(new DropTableIntent("child"));
        var second = Operation(new EnsureTableIntent(Child("bigint"), SafeMigrationTableMode.StrictDefinition));
        var graph = await ReadGraph(connection, [first, ensure, drop, second, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, first, Missing());
        Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
        Accept(projection, second, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Decision(ensure, result).Action);
    }

    /// <summary>A later successful standalone capture cannot certify an incompatible earlier inline operand.</summary>
    /// <param name="compatibleFirst">Whether the original inline child has the same captured integer storage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureStandaloneCapture_RequiresOriginalInlineDependentStorage(
        bool compatibleFirst
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var foreign = ForeignKey();
        var first = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child",
            Child(compatibleFirst ? "int" : "bigint").Columns, foreignKeys: [foreign]),
            SafeMigrationTableMode.StrictDefinition));

        var drop = Operation(new DropTableIntent("child"));
        var second = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(foreign));
        var graph = await ReadGraph(connection, [first, drop, second, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);

        // Act
        var result = projection.Project(first, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(compatibleFirst ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
    }

    /// <summary>Inline FK storage evidence does not treat a later comment change as physical incompatibility.</summary>
    [Fact]
    public async Task FutureStandaloneCapture_NonStorageFacetDoesNotInvalidateInlineStorage()
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var foreign = ForeignKey();
        var first = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child", Child().Columns,
            foreignKeys: [foreign]), SafeMigrationTableMode.StrictDefinition));

        var drop = Operation(new DropTableIntent("child"));
        var second = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int", comment: "later")]),
            SafeMigrationTableMode.StrictDefinition));

        var ensure = Operation(new EnsureForeignKeyIntent(foreign));
        var graph = await ReadGraph(connection, [first, drop, second, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);

        // Act
        var result = projection.Project(first, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>Reuse of an FK instance retains each accepted standalone source's own full certificate.</summary>
    /// <param name="useReplacement">Whether the FK is assessed for the replacement child with its new comment.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedStandaloneForeignKey_NonStorageFacetKeepsEachAuthoredSourceProof(
        bool useReplacement
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var first = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var drop = Operation(new DropTableIntent("child"));
        var second = Operation(new EnsureTableIntent(new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int", comment: "later")]),
            SafeMigrationTableMode.StrictDefinition));

        var graph = await ReadGraph(connection, [first, ensure, drop, second, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, first, Missing());
        if (useReplacement)
        {
            Accept(projection, ensure, Missing());
            Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
            Accept(projection, second, Missing());
        }

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>An accepted standalone FK owns its name until an exact accepted drop removes it.</summary>
    /// <param name="dropExactOwner">Whether the namespace-owning foreign key is removed before the new Ensure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedStandaloneForeignKey_RequiresExactOwnerRemoval(
        bool dropExactOwner
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var owner = Operation(new EnsureTableIntent(Child(table: "other_new"),
            SafeMigrationTableMode.StrictDefinition));

        var first = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("FK_child", "other_new",
            ["ParentId"], "parent", ["Id"])));

        var drop = Operation(new DropForeignKeyIntent("FK_child", "other_new"));
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [owner, first, drop, create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, owner, Missing());
        Accept(projection, first, Missing());
        if (dropExactOwner)
        {
            Accept(projection, drop, Live(SafeMigrationObservedState.Matching));
        }

        Accept(projection, create, Missing());

        // Act
        var result = projection.Project(ensure, UnknownLivePrerequisite());

        // Assert
        Assert.Equal(dropExactOwner ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Independent catalog failures keep precedence over a captured newly-created-child proof.</summary>
    /// <param name="state">The invariant classification that must not be promoted.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.Different)]
    public async Task IndependentFailure_IsNeverReplacedByPrerequisiteCapture(
        SafeMigrationObservedState state
    )
    {
        // Arrange
        await using var connection = new GraphConnection(true);
        var create = Operation(new EnsureTableIntent(Child(), SafeMigrationTableMode.StrictDefinition));
        var ensure = Operation(new EnsureForeignKeyIntent(ForeignKey()));
        var graph = await ReadGraph(connection, [create, ensure]);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: graph);
        Accept(projection, create, Missing());
        var failure = Live(state);

        // Act
        var result = graph.ValidateProjectedOperation(ensure, failure, projection);

        // Assert
        Assert.Same(failure, result);
    }

    /// <summary>Standalone capture uses the existing 32-operation proof batches, not one command per FK.</summary>
    [Fact]
    public async Task ManyStandaloneForeignKeys_ShareBoundedPrincipalCapture()
    {
        // Arrange
        const int count = 65;
        await using var connection = new GraphConnection(true);
        var operations = new List<SafeMigrationOperation>();
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var name = "child_" + ordinal;
            operations.Add(Operation(new EnsureTableIntent(Child(table: name),
                SafeMigrationTableMode.StrictDefinition)));
            operations.Add(Operation(new EnsureForeignKeyIntent(ForeignKey(name))));
        }

        // Act
        await ReadGraph(connection, operations);

        // Assert
        Assert.Equal(1, connection.Statements.Count(static sql => sql.StartsWith("SELECT i.object_id",
            StringComparison.Ordinal)));
        Assert.Equal(3, connection.Statements.Count(static sql => sql.Contains("CONVERT(bit, CASE WHEN 1 = 1",
            StringComparison.Ordinal)));
        Assert.All(connection.Statements, static sql => Assert.InRange(Encoding.UTF8.GetByteCount(sql), 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }
}
