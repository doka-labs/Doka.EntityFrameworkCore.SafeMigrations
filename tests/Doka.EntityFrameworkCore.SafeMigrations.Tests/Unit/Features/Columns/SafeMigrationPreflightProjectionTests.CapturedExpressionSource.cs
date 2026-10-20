namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Computed and CHECK renames rewrite identifier roles, never numeric or string tokens.</summary>
    /// <param name="renamedColumn">The changed owner member.</param>
    /// <param name="expectedColumnSql">The exact native computed expression after the rename.</param>
    /// <param name="expectedCheckSql">The exact native CHECK predicate after the rename.</param>
    [Theory]
    [InlineData("source_value",
        "CASE WHEN [renamed_value] > 0 THEN 2147483648 / 2147483647 ELSE 0 END",
        "[renamed_value] > 0 AND 'source_value' <> ''")]
    [InlineData("unrelated_value",
        "CASE WHEN [source_value] > 0 THEN 2147483648 / 2147483647 ELSE 0 END",
        "[source_value] > 0 AND 'source_value' <> ''")]
    public void CapturedExpressionRenamePreservesNativeSource(
        string renamedColumn,
        string expectedColumnSql,
        string expectedCheckSql
    )
    {
        // Arrange
        const string computedSql = "CASE WHEN [source_value] > 0 THEN 2147483648 / 2147483647 ELSE 0 END";
        const string checkSql = "[source_value] > 0 AND 'source_value' <> ''";
        var computed = CaptureProjectionComputed(computedSql);
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", checkSql, null);
        var projection = CapturedExpressionProjection(computed, check);

        // Act
        Apply(projection, new RenameColumnIntent(renamedColumn, "items", "renamed_value"));
        var exists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "computed_value", out var copied);

        var expectedCheck = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", expectedCheckSql, null);

        var checkAnalysis = projection.Project(
            new SafeMigrationOperation(new EnsureCheckConstraintIntent(expectedCheck),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.True(exists);
        Assert.NotNull(copied);
        Assert.Equal(expectedColumnSql, copied.CapturedComputedSql);
        Assert.NotNull(copied.ComputedExpression);
        Assert.IsNotType<SafeMigrationSqlOpaqueExpression>(copied.ComputedExpression);
        Assert.Equal(SafeMigrationObservedState.Matching, checkAnalysis.ObservedState);
        if (renamedColumn == "unrelated_value")
        {
            Assert.Same(computedSql, copied.CapturedComputedSql);
            Assert.Same(computed, copied);
        }
    }

    /// <summary>An uncertifiable source rewrite cannot silently become a new typed expression contract.</summary>
    [Fact]
    public void CapturedUnquotedRenameToUnsafeTokenLosesProof()
    {
        // Arrange
        const string sql = "source_value + 2147483648";
        var computed = CaptureProjectionComputed(sql);
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "source_value > 0", null);

        var projection = CapturedExpressionProjection(computed, check);

        // Act
        Apply(projection, new RenameColumnIntent("source_value", "items", "not a bare token"));
        var exists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "computed_value", out var copied);

        // Assert
        Assert.True(exists);
        Assert.NotNull(copied);
        Assert.Null(copied.CapturedComputedSql);
        Assert.Null(copied.ComputedColumnSql);
        var opaque = Assert.IsType<SafeMigrationSqlOpaqueExpression>(copied.ComputedExpression);
        Assert.True(opaque.FollowsIdentifierRename);
        Assert.Same(sql, opaque.Sql);
    }

    /// <summary>Owner-only projection copies preserve native expressions and their source reference.</summary>
    [Fact]
    public void CapturedExpressionsSurviveTableRenameCopies()
    {
        // Arrange
        const string computedSql = "source_value + 2147483648";
        const string checkSql = "source_value > 0";
        var computed = CaptureProjectionComputed(computedSql);
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", checkSql, null);
        var projection = CapturedExpressionProjection(computed, check);

        // Act
        Apply(projection, new RenameTableIntent("items", "renamed_items"));
        var exists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("renamed_items", null, "computed_value", out var copied);

        var expectedCheck = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "renamed_items", checkSql, null);

        var checkAnalysis = projection.Project(
            new SafeMigrationOperation(new EnsureCheckConstraintIntent(expectedCheck),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.True(exists);
        Assert.NotNull(copied);
        Assert.Same(computedSql, copied.CapturedComputedSql);
        Assert.Same(computed.ComputedExpression, copied.ComputedExpression);
        Assert.Equal(SafeMigrationObservedState.Matching, checkAnalysis.ObservedState);
    }

    /// <summary>Renaming the generated member itself exercises the sparse accepted-column copy.</summary>
    [Fact]
    public void CapturedComputedMemberRenameRetainsSourceReference()
    {
        // Arrange
        const string computedSql = "source_value + 2147483648";
        var computed = CaptureProjectionComputed(computedSql);
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "source_value > 0", null);

        var projection = CapturedExpressionProjection(computed, check);

        // Act
        Apply(projection, new RenameColumnIntent("computed_value", "items", "renamed_computed"));
        var exists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "renamed_computed", out var copied);

        // Assert
        Assert.True(exists);
        Assert.NotNull(copied);
        Assert.Equal("renamed_computed", copied.Name);
        Assert.Same(computedSql, copied.CapturedComputedSql);
    }

    /// <summary>A certified rename may add identifier quoting without changing the authored native contract.</summary>
    [Fact]
    public void CapturedRenameMatchesUnquotedAuthoredTargetWithVerifiedNativeQuoting()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new CapturedSqlQuoteIdentityNormalizer());

        var computed = CaptureProjectionComputed("source_value + 2147483648");
        var target = CaptureProjectionComputed("renamed_value + 2147483648");
        var table = new ExpectedTableDefinition("items", [Column("source_value"), computed]);
        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>A matching existing strict table retains only its certified renamed column and CHECK facts.</summary>
    [Fact]
    public void ExistingStrictTableRenameRetainsCapturedComputedAndCheckContracts()
    {
        // Arrange
        var provider = new RenameAbsenceProjection();
        var projection = new SafeMigrationPreflightProjection(provider,
            objectIdentityNormalizer: new CapturedSqlQuoteIdentityNormalizer());

        var source = CaptureProjectionComputed("CASE WHEN \"source_value\" > 0 THEN \"source_value\" ELSE 0 END");
        var target = CaptureProjectionComputed("CASE WHEN \"renamed_value\" > 0 THEN \"renamed_value\" ELSE 0 END");
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"source_value\" >= 0", null);

        var targetCheck = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"renamed_value\" >= 0", null);

        var table = new ExpectedTableDefinition("items", [Column("source_value"), source], checkConstraints: [check]);
        ObserveAccepted(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Matching);

        // Act
        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        var columnAnalysis = projection.Project(new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Different));

        var checkAnalysis = projection.Project(new SafeMigrationOperation(new EnsureCheckConstraintIntent(targetCheck),
            SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.True(provider.WasQueried);
        Assert.Equal(SafeMigrationObservedState.Matching, columnAnalysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, checkAnalysis.ObservedState);
        Assert.True(((ISafeMigrationProjectedTableSource)projection)
            .TryGetProjectedTableState("items", null, out var state));
        Assert.False(state.IsNewlyCreated);
    }

    /// <summary>Retained source contracts cannot invent an empty table or resolve unknown external members.</summary>
    /// <param name="requiredAdd">Whether the operation would need an empty existing table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingStrictRenameDoesNotInventUnrelatedColumnOrEmptyTableProof(bool requiredAdd)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(new RenameAbsenceProjection(),
            objectIdentityNormalizer: new CapturedSqlQuoteIdentityNormalizer());

        var table = new ExpectedTableDefinition("items",
            [Column("source_value"), CaptureProjectionComputed("\"source_value\" + 1")]);

        ObserveAccepted(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Matching);

        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("external_member")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation,
            Live(requiredAdd ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching));

        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.True(decision.Action.RejectsExecution());
        Assert.True(((ISafeMigrationProjectedTableSource)projection)
            .TryGetProjectedTableState("items", null, out var state));
        Assert.False(state.IsNewlyCreated);
        Assert.True(state.HasUnknownStructure);
    }

    /// <summary>A later structural mutation expires renamed CHECK facts instead of reviving stale evidence.</summary>
    [Fact]
    public void ExistingRenamedCheckProofExpiresAfterAnotherMemberDrop()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(new RenameAbsenceProjection(),
            objectIdentityNormalizer: new CapturedSqlQuoteIdentityNormalizer());

        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"source_value\" >= 0", null);

        var table = new ExpectedTableDefinition("items", [Column("source_value"), Column("unrelated_value")],
            checkConstraints: [check]);

        ObserveAccepted(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Matching);

        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        Apply(projection, new DropColumnIntent("unrelated_value", "items"));
        var target = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"renamed_value\" >= 0", null);

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>A rename cannot revive accepted CHECKs invalidated by an earlier structural boundary.</summary>
    [Fact]
    public void ExistingRenameDoesNotRestoreChecksAfterPriorMemberDrop()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(new RenameAbsenceProjection(),
            objectIdentityNormalizer: new CapturedSqlQuoteIdentityNormalizer());

        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"source_value\" >= 0", null);

        var table = new ExpectedTableDefinition("items", [Column("source_value"), Column("unrelated_value")],
            checkConstraints: [check]);

        ObserveAccepted(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Matching);

        Apply(projection, new DropColumnIntent("unrelated_value", "items"));
        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        var target = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", "\"renamed_value\" >= 0", null);

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Native unquoted folding reaches quote-only rename equality without folding quoted members.</summary>
    /// <param name="quotedUppercaseTarget">Whether the target explicitly names another physical member.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedRenameQuoteEquivalenceUsesNativeUnquotedBinding(bool quotedUppercaseTarget)
    {
        // Arrange
        var normalizer = new CapturedSqlQuoteIdentityNormalizer { FoldUnquotedCapturedSqlIdentifiers = true, };
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: normalizer);
        var initial = CaptureProjectionComputed("SOURCE_VALUE + 2147483648");
        var target = CaptureProjectionComputed(quotedUppercaseTarget
            ? "\"RENAMED_VALUE\" + 2147483648"
            : "RENAMED_VALUE + 2147483648");

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("source_value"), initial]), SafeMigrationTableMode.StrictDefinition));

        Apply(projection, new RenameColumnIntent("source_value", "items", "renamed_value"));
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(quotedUppercaseTarget ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
    }

    private sealed class CapturedSqlQuoteIdentityNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public char? CapturedSqlIdentifierDelimiter => '"';

        public bool FoldUnquotedCapturedSqlIdentifiers { get; init; }

        public string NormalizeIdentifier(string identifier) => identifier;

        public string? NormalizeSchema(string? schema) => schema;

        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }

    private static SafeMigrationPreflightProjection CapturedExpressionProjection(
        ExpectedColumnDefinition computed,
        ExpectedCheckConstraintDefinition check
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items",
            [Column("source_value"), Column("unrelated_value"), computed],
            checkConstraints: [check]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));

        return projection;
    }

    private static ExpectedColumnDefinition CaptureProjectionComputed(string sql) =>
        SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "computed_value",
            Table = "items",
            ClrType = typeof(long),
            IsNullable = true,
            ComputedColumnSql = sql,
        });
}
