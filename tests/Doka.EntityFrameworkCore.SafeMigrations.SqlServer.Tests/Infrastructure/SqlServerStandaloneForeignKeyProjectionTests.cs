namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies compact live-parent prerequisites for FKs added to accepted newly created children.</summary>
public sealed class SqlServerStandaloneForeignKeyProjectionTests
{
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

    /// <summary>Captures the same compact key/storage inputs while keeping this unit fixture catalog-only.</summary>
    private static async Task<SqlServerProjectedDependencyGraph> ReadGraph(
        GraphConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        return await SqlServerProjectedDependencyGraph.ReadAsync(connection, null, operations, 73,
            static (table, foreign) => table.Columns.First(column => column.Name == foreign.Columns[0]).StoreType
                == "int" ? "1 = 1" : "0 = 1",
            catalog.ColumnStorageEquals,
            static (_, _) => true, CancellationToken.None);
    }

    private static ExpectedTableDefinition Child(
        string storeType = "int",
        string table = "child",
        string? schema = null
    )
        => new(table, [new ExpectedColumnDefinition("ParentId", storeType == "int" ? typeof(int) : typeof(long),
            false, storeType)], schema);

    private static ExpectedForeignKeyDefinition ForeignKey(
        string table = "child",
        string? schema = null
    )
        => new("FK_" + table, table, ["ParentId"], "parent", ["Id"], schema);

    /// <summary>Creates one accepted namespace occupant without unrelated row or storage differences.</summary>
    private static ExpectedTableDefinition OccupiedTable(
        string table,
        string kind
    )
        => new(table, Child().Columns,
            primaryKey: kind == "PK" ? new ExpectedPrimaryKeyDefinition("FK_child", table, ["ParentId"]) : null,
            uniqueConstraints: kind == "UQ" ? [new ExpectedUniqueConstraintDefinition("FK_child", table, ["ParentId"])]
                : [],
            checkConstraints: kind == "C" ? [new ExpectedCheckConstraintDefinition("FK_child", table,
                "[ParentId] > 0")] : []);

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    )
        => new(intent, intent is AlterColumnIntent ? SafeMigrationPolicy.RepairIfSafe
            : SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis Missing() => Live(SafeMigrationObservedState.Missing);

    private static SafeMigrationProviderAnalysis UnknownLivePrerequisite()
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false,
            "classified_prerequisite_missing");

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state
    )
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching,
            "classified_" + state.ToString().ToLowerInvariant());

    private static SafeMigrationDecision Decision(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
        => SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState, operation.Policy,
            analysis.RepairCapability);

    private static void Accept(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
        => projection.Observe(operation, analysis, analysis, Decision(operation, analysis));

    /// <summary>Builds exactly the ordered mutation whose stale proof must not be promoted.</summary>
    private static SafeMigrationOperation[] Mutations(
        string mutation
    )
        => mutation switch
        {
            "child-type" => [Operation(new AlterColumnIntent("child",
                new ExpectedColumnDefinition("ParentId", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")))],
            "child-drop-column" => [Operation(new DropColumnIntent("ParentId", "child"))],
            "child-rename-column" => [Operation(new RenameColumnIntent("ParentId", "child", "OtherId"))],
            "child-rename-table" => [Operation(new RenameTableIntent("child", "renamed_child"))],
            "parent-drop-column" => [Operation(new DropColumnIntent("Id", "parent"))],
            "parent-replace-column" => [Operation(new DropColumnIntent("Id", "parent")),
                Operation(new EnsureColumnIntent("parent", new ExpectedColumnDefinition("Id", typeof(int),
                    false, "int")))],
            "parent-alter-column" => [Operation(new AlterColumnIntent("parent",
                new ExpectedColumnDefinition("Id", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("Id", typeof(int), false, "int")))],
            "parent-drop-table" => [Operation(new DropTableIntent("parent"))],
            "parent-rename-table" => [Operation(new RenameTableIntent("parent", "renamed_parent"))],
            "parent-drop-key" => [Operation(new DropPrimaryKeyIntent("PK_parent", "parent"))],
            "parent-replace-key-other-columns" => [Operation(new DropPrimaryKeyIntent("PK_parent", "parent")),
                Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("PK_replacement", "parent",
                    ["OtherId"])))],
            "managed-data" => [Operation(new EnsureModelManagedDataIntent("child", ["ParentId"], ["int"],
                ["ParentId"], ["int"], new object?[,] { { 999 } }, null, null))],
            "raw-sql" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    /// <summary>Supplies real graph result shapes without pretending to prove runtime orphan absence.</summary>
    private sealed class GraphConnection(
        bool hasKey,
        string? occupiedKind = null
    ) : System.Data.Common.DbConnection
    {
        /// <summary>Gets the complete ordered capture statements.</summary>
        public List<string> Statements { get; } = [];

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        /// <inheritdoc />
        public override string Database => "graph_fixture";

        /// <inheritdoc />
        public override string DataSource => "graph_fixture";

        /// <inheritdoc />
        public override string ServerVersion => "16.0";

        /// <inheritdoc />
        public override ConnectionState State => ConnectionState.Open;

        /// <inheritdoc />
        public override void Open() { }

        /// <inheritdoc />
        public override void Close() { }

        /// <inheritdoc />
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        /// <inheritdoc />
        protected override System.Data.Common.DbCommand CreateDbCommand() => new GraphCommand(this);

        /// <summary>Returns only the requested compact metadata and Boolean storage certificates.</summary>
        public DataTableReader Read(
            string sql
        )
        {
            Statements.Add(sql);
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (sql.StartsWith("SELECT object_id, parent_object_id", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(int), typeof(int), typeof(string), typeof(byte),
                    typeof(byte), typeof(int));
            }
            else if (sql.StartsWith("SELECT requested.schema_name", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(string), typeof(string), typeof(string), typeof(string), typeof(int),
                    typeof(int), typeof(byte), typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string));

                var matches = System.Text.RegularExpressions.Regex.Matches(sql,
                    @"\(N'(?<schema>[^']*)', N'(?<table>[^']*)', "
                    + @"(?<column>N'[^']*'|CONVERT\(nvarchar\(128\), NULL\)), "
                    + @"(?<name>N'[^']*'|CONVERT\(nvarchar\(128\), NULL\))\)");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var parent = match.Groups["table"].Value == "parent";
                    var other = match.Groups["table"].Value == "other";
                    var column = Unquote(match.Groups["column"].Value);
                    var name = Unquote(match.Groups["name"].Value);
                    var occupied = name == "FK_child" && occupiedKind is not null;

                    table.Rows.Add(match.Groups["schema"].Value, match.Groups["table"].Value,
                        column, name, 1, parent ? 100 : other ? 200 : DBNull.Value,
                        parent && column is not null ? (byte)56 : DBNull.Value,
                        parent && column is not null ? false : DBNull.Value,
                        parent && column is not null ? 0 : DBNull.Value,
                        other && name == "FK_child" && occupiedKind == "F" ? 700 : DBNull.Value, false, false,
                        parent && hasKey && name == "PK_parent" ? 1 : DBNull.Value,
                        parent && hasKey && name == "PK_parent" ? 1 : DBNull.Value,
                        parent && column is not null ? 7 : DBNull.Value,
                        parent ? 100 : other ? 200 : DBNull.Value,
                        occupied ? 700 : DBNull.Value,
                        occupied ? occupiedKind is "F" or "PK" or "UQ" or "C" ? 200 : 0 : DBNull.Value,
                        occupied ? occupiedKind : DBNull.Value);
                }
            }
            else if (sql.StartsWith("SELECT i.object_id", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(int), typeof(string), typeof(string), typeof(string),
                    typeof(int));
                if (hasKey)
                {
                    table.Rows.Add(100, 1, "PK_parent", "PK_parent", "Id", 7);
                }
            }
            else if (sql.Contains("CONVERT(bit, CASE WHEN", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(bool));
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    sql, @"SELECT (?<ordinal>\d+), CONVERT\(bit, CASE WHEN (?<ready>[01]) = 1"))
                {
                    table.Rows.Add(int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture),
                        match.Groups["ready"].Value == "1");
                }
            }
            else
            {
                throw new InvalidOperationException("Unowned graph capture statement.");
            }

            return table.CreateDataReader();
        }

        private static string? Unquote(string token) => token.StartsWith("N'", StringComparison.Ordinal)
            ? token[2..^1] : null;

        private static void AddColumns(DataTable table, params Type[] types)
        {
            for (var ordinal = 0; ordinal < types.Length; ordinal++)
            {
                table.Columns.Add("column_" + ordinal, types[ordinal]);
            }
        }
    }

    /// <summary>Keeps command/reader ownership identical to the direct compact graph capture.</summary>
    private sealed class GraphCommand(GraphConnection connection) : System.Data.Common.DbCommand
    {
        private readonly SqlCommand _parameterOwner = new();

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override int CommandTimeout { get; set; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection
            => _parameterOwner.Parameters;

        /// <inheritdoc />
        public override void Cancel() { }

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() { }

        /// <inheritdoc />
        protected override System.Data.Common.DbParameter CreateDbParameter() => new SqlParameter();

        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => connection.Read(CommandText);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameterOwner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
