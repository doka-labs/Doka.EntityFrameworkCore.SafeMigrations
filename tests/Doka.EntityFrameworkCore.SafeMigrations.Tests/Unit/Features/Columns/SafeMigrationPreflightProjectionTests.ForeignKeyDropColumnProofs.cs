namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Negative add row proofs expire after DML even without a cached target table.</summary>
    /// <param name="mutation">The ordinary or model-managed DML that can change originally blocking rows.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("delete")]
    [InlineData("model")]
    public void RequiredSqlNullAddBlockedRowsDoNotSurviveEarlierDml(string mutation)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var target = new ExpectedColumnDefinition("value", typeof(int), false, "int",
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)));
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
            SafeMigrationRepairCapability.None, false, "classified_data_blocked")
        {
            RequiresLiveDataProof = true,
        };

        var modelOperation = new SafeMigrationOperation(new EnsureModelManagedDataIntent("other",
            ["id"], ["int"], ["id"], ["int"], new object?[,] { { 1 } }, null, null),
            SafeMigrationPolicy.ThrowIfDifferent);
        var modelLive = Live(SafeMigrationObservedState.Missing);
        var modelDecision = SafeMigrationDecisionPlanner.Plan(modelOperation.Intent.Kind, modelLive.ObservedState,
            modelOperation.Policy, modelLive.RepairCapability);
        var mutated = mutation != "none";

        // Act
        if (mutation == "delete")
        {
            projection.ObserveProviderPostcondition(new DeleteDataOperation
            {
                Table = "items",
                KeyColumns = ["id"],
                KeyColumnTypes = ["int"],
                KeyValues = new object?[,] { { 1 } },
            });
        }
        else if (mutation == "model")
        {
            projection.Observe(modelOperation, modelLive, modelLive, modelDecision);
        }

        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(mutated ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.DataBlocked, result.ObservedState);
        Assert.Equal(mutated ? "projected_data_state_unknown" : "classified_data_blocked", result.Code);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        if (!mutated)
        {
            Assert.Same(live, result);
        }
    }

    /// <summary>Accepted targets use physical member identity without weakening any non-name facet.</summary>
    /// <param name="ignoreColumnCase">Whether the provider resolves case variants to one column.</param>
    /// <param name="changeFacet">Whether the second target changes an independent physical contract.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AcceptedEnsureTargetUsesOnlyProviderColumnNameIdentity(bool ignoreColumnCase, bool changeFacet)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase));
        var first = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("Id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing);
        var firstResult = projection.Project(first, live);
        var firstDecision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);
        var target = new ExpectedColumnDefinition("id", typeof(int), false, "int",
            comment: changeFacet ? "reviewed" : null);
        var second = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Observe(first, live, firstResult, firstDecision);
        var result = projection.Project(second, live);
        var decision = SafeMigrationDecisionPlanner.Plan(second.Intent.Kind, result.ObservedState,
            second.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, firstDecision.Action);
        Assert.Equal(!ignoreColumnCase ? SafeMigrationObservedState.Missing
            : changeFacet ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            result.ObservedState);
        Assert.Equal(!ignoreColumnCase ? SafeMigrationAction.Apply
            : changeFacet ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Full-table accepted targets apply the same bounded name identity as column-state facts.</summary>
    [Fact]
    public void CompleteProjectedEnsureUsesColumnMemberIdentityWithoutChangingOwner()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase: true));
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("Id")]),
            SafeMigrationTableMode.StrictDefinition));
        var matching = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("id")),
            SafeMigrationPolicy.RepairIfSafe);
        var otherOwner = new SafeMigrationOperation(new EnsureColumnIntent("ITEMS", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var missing = Live(SafeMigrationObservedState.Missing);

        // Act
        var result = projection.Project(matching, missing);
        var ownerResult = projection.Project(otherOwner, missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, ownerResult.ObservedState);
    }

    /// <summary>Case-resolved accepted sources retain exact old metadata and supported alter facets.</summary>
    /// <param name="ignoreColumnCase">Whether case variants resolve one physical member.</param>
    /// <param name="drift">Whether independent source or target storage facets differ.</param>
    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(true, "source")]
    [InlineData(true, "target")]
    public void AcceptedAlterSourceUsesOnlyProviderColumnNameIdentity(bool ignoreColumnCase, string drift)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase));
        Apply(projection, new EnsureColumnIntent("items", Column("Id")));
        var old = new ExpectedColumnDefinition("id", typeof(int), false, "int",
            comment: drift == "source" ? "unproven" : null);
        var target = new ExpectedColumnDefinition("id", typeof(int), true, drift == "target" ? "bigint" : "int");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, old),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(ignoreColumnCase ? SafeMigrationObservedState.Different
            : SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal(ignoreColumnCase && drift == "none" ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.Equal(!ignoreColumnCase ? SafeMigrationAction.RejectPrerequisiteMissing
            : drift == "none" ? SafeMigrationAction.Repair : SafeMigrationAction.RejectDifferent, decision.Action);
        if (!ignoreColumnCase)
        {
            Assert.Equal("projected_structure_state_unknown", result.Code);
        }
    }

    /// <summary>Foreign-key removal alone cannot make an unsupported provider's column proof current.</summary>
    /// <param name="matching">Whether the immutable live column already matches the target.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyDropColumnReuseRemainsDisabledWithoutProviderCertificate(bool matching)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_reference", "items"),
            SafeMigrationObservedState.Matching);
        var operation = ForeignKeyDropNullabilityRelaxation();
        var live = new SafeMigrationProviderAnalysis(
            matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            matching, "captured_column_proof");

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>A rejected removal does not change the live source proof or table lifetime.</summary>
    [Fact]
    public void RejectedForeignKeyDropDoesNotInvalidateColumnProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var drop = new SafeMigrationOperation(new DropForeignKeyIntent("fk_reference", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var rejected = Live(SafeMigrationObservedState.Unsupported);
        var rejection = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, rejected.ObservedState,
            drop.Policy, rejected.RepairCapability);
        var operation = ForeignKeyDropNullabilityRelaxation();
        var live = AlterProof();

        // Act
        projection.Observe(drop, rejected, rejected, rejection);
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, rejection.Action);
        Assert.Same(live, result);
    }

    /// <summary>Provider invariant rejection remains visible even after accepted removal history.</summary>
    [Fact]
    public void ForeignKeyDropCannotEraseInvariantUnsupportedColumn()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_reference", "items"),
            SafeMigrationObservedState.Matching);
        var operation = ForeignKeyDropNullabilityRelaxation();
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, "unsupported_column")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Same(live, result);
    }

    /// <summary>Column case identity rewrites every dependent member without folding another table owner.</summary>
    /// <param name="ignoreColumnCase">Whether case variants name the same physical column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnCaseRenameKeepsDependentMembersAndOwnerIdentityCoherent(bool ignoreColumnCase)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase));
        var source = ColumnRenameTable("items", "owner_id", "fk_self");
        var otherOwner = ColumnRenameTable("ITEMS", "owner_id", "fk_other_self");
        Apply(projection, new EnsureTableIntent(source, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureTableIntent(otherOwner, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("ix_owner", "items",
            [new ExpectedIndexKeyDefinition("owner_id")], includedColumns: ["id"])));
        Apply(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("ix_include", "items",
            [new ExpectedIndexKeyDefinition("id")], includedColumns: ["owner_id"])));
        var child = new ExpectedTableDefinition("child", [Column("owner_ref")], foreignKeys:
        [
            new ExpectedForeignKeyDefinition("fk_parent", "child", ["owner_ref"], "items", ["owner_id"]),
        ]);

        Apply(projection, new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        var rename = new SafeMigrationOperation(new RenameColumnIntent("OWNER_ID", table: "items", newName: "actor_id"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing);
        var result = projection.Project(rename, live);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Intent.Kind, result.ObservedState,
            rename.Policy, result.RepairCapability);
        var expectedColumn = ignoreColumnCase ? "actor_id" : "owner_id";

        // Act
        projection.Observe(rename, live, result, decision);
        var tableResult = projection.Project(new SafeMigrationOperation(new EnsureTableIntent(
            ColumnRenameTable("items", expectedColumn, "fk_self"), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent), live);
        var indexResult = projection.Project(new SafeMigrationOperation(new EnsureIndexIntent(
            new ExpectedIndexDefinition("ix_owner", "items", [new ExpectedIndexKeyDefinition(expectedColumn)],
                includedColumns: ["id"])), SafeMigrationPolicy.ThrowIfDifferent), live);
        var includedResult = projection.Project(new SafeMigrationOperation(new EnsureIndexIntent(
            new ExpectedIndexDefinition("ix_include", "items", [new ExpectedIndexKeyDefinition("id")],
                includedColumns: [expectedColumn])), SafeMigrationPolicy.ThrowIfDifferent), live);
        var childResult = projection.Project(new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("fk_parent", "child", ["owner_ref"], "items", [expectedColumn])),
            SafeMigrationPolicy.ThrowIfDifferent), live);
        var ownerResult = projection.Project(new SafeMigrationOperation(new EnsureTableIntent(
            otherOwner, SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Equal(ignoreColumnCase ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, tableResult.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, indexResult.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, includedResult.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, childResult.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, ownerResult.ObservedState);
    }

    /// <summary>Structured column references use member identity while preserving every owner qualifier.</summary>
    /// <param name="qualified">Whether the schema qualifier also matches the renamed column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnCaseRenameUpdatesStructuredExpressionWithoutChangingQualifiers(bool qualified)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase: true));
        var schema = qualified ? "OWNER_ID" : null;
        var sourceExpression = qualified
            ? SafeMigrationSql.Identifier("OWNER_ID", "items", "owner_id")
            : SafeMigrationSql.Identifier("owner_id");
        var targetExpression = qualified
            ? SafeMigrationSql.Identifier("OWNER_ID", "items", "actor_id")
            : SafeMigrationSql.Identifier("actor_id");
        var computed = new ExpectedColumnDefinition("calculated", typeof(int), true, "int",
            isStored: false, computedExpression: sourceExpression);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("owner_id"), computed], schema: schema), SafeMigrationTableMode.StrictDefinition));
        var rename = new SafeMigrationOperation(new RenameColumnIntent("OWNER_ID", table: "items", newName: "actor_id",
            schema: schema),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing);
        var result = projection.Project(rename, live);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Intent.Kind, result.ObservedState,
            rename.Policy, result.RepairCapability);
        var targetColumn = new ExpectedColumnDefinition("calculated", typeof(int), true, "int", isStored: false,
            computedExpression: targetExpression);
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", targetColumn, schema),
            SafeMigrationPolicy.RepairIfSafe);
        var stale = new SafeMigrationOperation(new EnsureColumnIntent("items", computed, schema),
            SafeMigrationPolicy.RepairIfSafe);
        var expectedTable = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("actor_id"), targetColumn], schema: schema), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.Observe(rename, live, result, decision);
        var computedResult = projection.Project(ensure, Live(SafeMigrationObservedState.Matching));
        var staleResult = projection.Project(stale, Live(SafeMigrationObservedState.Matching));
        var tableResult = projection.Project(expectedTable, live);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, computedResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, computedResult.RepairCapability);
        Assert.Equal(SafeMigrationObservedState.Different, staleResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, staleResult.RepairCapability);
        Assert.Equal(SafeMigrationObservedState.Matching, tableResult.ObservedState);
    }

    /// <summary>An opaque dependent expression cannot inherit a structured rename certificate.</summary>
    /// <param name="providerFragment">Whether the unknown expression is a provider-specific fragment.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnCaseRenameKeepsOpaqueExpressionFailClosed(bool providerFragment)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase: true));
        var expression = providerFragment
            ? SafeMigrationSql.ProviderFragment("anonymous", "owner_id + 1")
            : SafeMigrationSql.Opaque("owner_id + 1");
        var computed = new ExpectedColumnDefinition("calculated", typeof(int), true, "int",
            isStored: false, computedExpression: expression);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("owner_id"), computed]), SafeMigrationTableMode.StrictDefinition));
        var rename = new SafeMigrationOperation(new RenameColumnIntent("OWNER_ID", table: "items", newName: "actor_id"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing);
        var result = projection.Project(rename, live);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Intent.Kind, result.ObservedState,
            rename.Policy, result.RepairCapability);
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", computed),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Observe(rename, live, result, decision);
        var computedResult = projection.Project(ensure, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, computedResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, computedResult.RepairCapability);
    }

    /// <summary>Builds an anonymous valid self-reference with distinct table and member identities.</summary>
    private static ExpectedTableDefinition ColumnRenameTable(string table, string column, string foreignKeyName) => new(
        table, [Column(column), Column("id")],
        primaryKey: new ExpectedPrimaryKeyDefinition("pk_owner", table, [column]),
        uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_owner", table, [column])],
        foreignKeys: [new ExpectedForeignKeyDefinition(foreignKeyName, table, [column], table, [column])]);

    /// <summary>Creates an anonymous exact-layout nullability relaxation.</summary>
    private static SafeMigrationOperation ForeignKeyDropNullabilityRelaxation() => new(
        new AlterColumnIntent("items",
            new ExpectedColumnDefinition("reference_id", typeof(Guid), true, "char(36)"),
            new ExpectedColumnDefinition("reference_id", typeof(Guid), false, "char(36)")),
        SafeMigrationPolicy.RepairIfSafe);
}
