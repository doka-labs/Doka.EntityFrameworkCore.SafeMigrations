namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Rejects stale matching evidence for operations that require an accepted missing table.</summary>
    /// <param name="kind">The table-owned operation family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.EnsureIndex)]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    [InlineData(SafeMigrationOperationKind.EnsureModelManagedData)]
    [InlineData(SafeMigrationOperationKind.UpdateModelManagedData)]
    public void MissingTableRejectsOwnedEnsureAlterAndManagedWrites(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        var projections = MissingOwnerProjections();
        var operation = new SafeMigrationOperation(OwnedIntent(kind), SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Matching);

        // Act
        var analyses = projections.Select(projection => projection.Project(operation, live)).ToArray();
        var decisions = analyses.Select(analysis => SafeMigrationDecisionPlanner.Plan(
            kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability)).ToArray();

        // Assert
        Assert.All(analyses, analysis =>
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
            Assert.Equal("projected_prerequisite_missing", analysis.Code);
            Assert.False(analysis.PostconditionSatisfied);
            Assert.False(analysis.IsOpaqueProjectionUnknown);
        });
        Assert.All(decisions, decision => Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action));
    }

    /// <summary>Missing owners make child removal, rename and managed deletion idempotent no-ops.</summary>
    /// <param name="kind">The table-owned operation family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    [InlineData(SafeMigrationOperationKind.DropIndex)]
    [InlineData(SafeMigrationOperationKind.RenameIndex)]
    [InlineData(SafeMigrationOperationKind.DropPrimaryKey)]
    [InlineData(SafeMigrationOperationKind.DropUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.DropCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.DropForeignKey)]
    [InlineData(SafeMigrationOperationKind.DeleteModelManagedData)]
    [InlineData(SafeMigrationOperationKind.DropTable)]
    [InlineData(SafeMigrationOperationKind.RenameTable)]
    public void MissingTableMakesOwnedRemovalAndRenameNoOp(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        var projections = MissingOwnerProjections();
        var operation = new SafeMigrationOperation(OwnedIntent(kind), SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Matching);

        // Act
        var analyses = projections.Select(projection => projection.Project(operation, live)).ToArray();
        var decisions = analyses.Select(analysis => SafeMigrationDecisionPlanner.Plan(
            kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability)).ToArray();

        // Assert
        Assert.All(analyses, analysis => Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState));
        Assert.All(decisions, decision => Assert.Equal(SafeMigrationAction.NoOp, decision.Action));
    }

    /// <summary>A missing referenced table blocks foreign-key creation despite stale provider matching.</summary>
    [Fact]
    public void MissingPrincipalTableRejectsForeignKeyEnsure()
    {
        // Arrange
        var provider = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(
            provider,
            objectIdentityNormalizer: new ColumnStateOwnershipNormalizer(normalizeAliases: true));

        ObserveAccepted(projection, new DropTableIntent("parents"), SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(
            new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_items_parents",
                "items",
                ["id"],
                "PARENTS",
                ["id"],
                principalSchema: "DBO")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var sequenceChecks = provider.SequenceChecks;

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_prerequisite_missing", analysis.Code);
        Assert.Equal(sequenceChecks, provider.SequenceChecks);
    }

    /// <summary>Accepted recreation clears the missing owner before subsequent child convergence.</summary>
    /// <param name="providerOperation">Whether drop and recreation use ordinary provider operations.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableRecreationRestoresChildOwnership(
        bool providerOperation
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedTableDefinition("items", [Column("id")]);

        if (providerOperation)
        {
            projection.ObserveProviderPostcondition(new DropTableOperation { Name = "items" });
        }
        else
        {
            ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
        }

        var ensureTable = new SafeMigrationOperation(
            new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var ensureColumn = new SafeMigrationOperation(OwnedIntent(SafeMigrationOperationKind.EnsureColumn),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var tableAnalysis = projection.Project(ensureTable, Live(SafeMigrationObservedState.Matching));

        if (providerOperation)
        {
            var create = new CreateTableOperation { Name = "items" };

            create.Columns.Add(ProviderColumn("id", "items", isNullable: false));
            projection.ObserveProviderPostcondition(create);
        }
        else
        {
            var decision = SafeMigrationDecisionPlanner.Plan(
                ensureTable.Intent.Kind,
                tableAnalysis.ObservedState,
                ensureTable.Policy,
                tableAnalysis.RepairCapability);

            projection.Observe(ensureTable, Live(SafeMigrationObservedState.Matching), tableAnalysis, decision);
        }

        var columnAnalysis = projection.Project(ensureColumn, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, tableAnalysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, columnAnalysis.ObservedState);
        Assert.True(columnAnalysis.PostconditionSatisfied);
    }

    /// <summary>Missing owner lookup honors default-schema aliases without leaking into qualified peers.</summary>
    /// <param name="table">The table named by the later operation.</param>
    /// <param name="schema">The schema named by the later operation.</param>
    /// <param name="expectedState">The expected projected state.</param>
    [Theory]
    [InlineData("ITEMS", "DBO", SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData("ITEMS", "archive", SafeMigrationObservedState.Matching)]
    [InlineData("other", "DBO", SafeMigrationObservedState.Matching)]
    public void MissingOwnerRespectsNormalizedAndQualifiedIdentity(
        string table,
        string schema,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var provider = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(
            provider,
            objectIdentityNormalizer: new ColumnStateOwnershipNormalizer(normalizeAliases: true));

        ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent(table, Column("id"), schema),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
    }

    /// <summary>Accepted table rename invalidates old child ownership while preserving the target owner.</summary>
    [Fact]
    public void RenamedSourceTableCannotReuseHistoricalChildMatching()
    {
        // Arrange
        var provider = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(provider);

        Apply(projection, new EnsureTableIntent(
            new ExpectedTableDefinition("items", [Column("id")]),
            SafeMigrationTableMode.StrictDefinition));
        ObserveAccepted(projection, new RenameTableIntent("items", "renamed"), SafeMigrationObservedState.Matching);

        var source = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var target = new SafeMigrationOperation(new EnsureColumnIntent("renamed", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var sourceAnalysis = projection.Project(source, Live(SafeMigrationObservedState.Matching));
        var targetAnalysis = projection.Project(target, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, sourceAnalysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, targetAnalysis.ObservedState);
    }

    /// <summary>Opaque boundaries invalidate prior missing-owner facts and preserve evidence precedence.</summary>
    /// <param name="sqlOperation">Whether the opaque operation is raw SQL.</param>
    /// <param name="sequenceAware">Whether the provider supplies ordered analysis.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpaqueBoundaryInvalidatesMissingOwnerEvidence(
        bool sqlOperation,
        bool sequenceAware
    )
    {
        // Arrange
        var provider = new ProjectionPrecedenceProbe(sequenceAware);
        var projection = new SafeMigrationPreflightProjection(provider);

        ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
        projection.ObserveProviderPostcondition(sqlOperation
            ? new SqlOperation { Sql = "SELECT 1;" }
            : new AlterDatabaseOperation());

        var operation = new SafeMigrationOperation(OwnedIntent(SafeMigrationOperationKind.EnsureColumn),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(sqlOperation || !sequenceAware
            ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(sqlOperation || !sequenceAware, analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>Missing-owner facts cannot override invariant or object-identity rejection.</summary>
    /// <param name="identityMismatch">Whether the rejection concerns object identity.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOwnerPreservesInvariantAndIdentityPriority(
        bool identityMismatch
    )
    {
        // Arrange
        var provider = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(provider, objectIdentityNormalizer: provider);

        ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(OwnedIntent(SafeMigrationOperationKind.EnsureColumn),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            identityMismatch ? "database_qualifier_mismatch" : "unsupported_contract")
        {
            IsInvariantUnsupported = !identityMismatch,
        };
        var sequenceChecks = provider.SequenceChecks;

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(sequenceChecks, provider.SequenceChecks);
    }

    private static SafeMigrationPreflightProjection[] MissingOwnerProjections()
    {
        var projections = new[]
        {
            new SafeMigrationPreflightProjection(),
            new SafeMigrationPreflightProjection(new ProjectionPrecedenceProbe(sequenceAware: true)),
        };

        foreach (var projection in projections)
        {
            ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
        }

        return projections;
    }

    private static SafeMigrationIntent OwnedIntent(
        SafeMigrationOperationKind kind
    ) => kind switch
    {
        SafeMigrationOperationKind.EnsureColumn => new EnsureColumnIntent("items", Column("id")),
        SafeMigrationOperationKind.AlterColumn => new AlterColumnIntent("items", Column("id")),
        SafeMigrationOperationKind.DropColumn => new DropColumnIntent("id", "items"),
        SafeMigrationOperationKind.RenameColumn => new RenameColumnIntent("id", "items", "renamed_id"),
        SafeMigrationOperationKind.EnsureIndex => new EnsureIndexIntent(
            new ExpectedIndexDefinition("ix_items", "items", [new ExpectedIndexKeyDefinition(column: "id")])),
        SafeMigrationOperationKind.DropIndex => new DropIndexIntent("ix_items", "items"),
        SafeMigrationOperationKind.RenameIndex => new RenameIndexIntent("ix_items", "items", "ix_renamed"),
        SafeMigrationOperationKind.EnsurePrimaryKey => new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"])),
        SafeMigrationOperationKind.DropPrimaryKey => new DropPrimaryKeyIntent("pk_items", "items"),
        SafeMigrationOperationKind.EnsureUniqueConstraint => new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items", "items", ["id"])),
        SafeMigrationOperationKind.DropUniqueConstraint => new DropUniqueConstraintIntent("uq_items", "items"),
        SafeMigrationOperationKind.EnsureCheckConstraint => new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("ck_items", "items", "id > 0")),
        SafeMigrationOperationKind.DropCheckConstraint => new DropCheckConstraintIntent("ck_items", "items"),
        SafeMigrationOperationKind.EnsureForeignKey => new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("fk_items_parents", "items", ["id"], "parents", ["id"])),
        SafeMigrationOperationKind.DropForeignKey => new DropForeignKeyIntent("fk_items_parents", "items"),
        SafeMigrationOperationKind.EnsureModelManagedData => new EnsureModelManagedDataIntent(
            "items", ["id"], ["int"], ["id"], ["int"], new object?[,] { { 1 } }, schema: null, uniqueKeys: null),
        SafeMigrationOperationKind.UpdateModelManagedData => new UpdateModelManagedDataIntent(
            "items", ["id"], ["int"], new object?[,] { { 1 } }, ["id"], ["int"],
            new object?[,] { { 1 } }, new object?[,] { { 1 } }, schema: null, uniqueKeys: null),
        SafeMigrationOperationKind.DeleteModelManagedData => new DeleteModelManagedDataIntent(
            "items", ["id"], ["int"], new object?[,] { { 1 } }, ["id"], ["int"],
            new object?[,] { { 1 } }, schema: null, foreignKeys: null),
        SafeMigrationOperationKind.DropTable => new DropTableIntent("items"),
        SafeMigrationOperationKind.RenameTable => new RenameTableIntent("items", "renamed"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
