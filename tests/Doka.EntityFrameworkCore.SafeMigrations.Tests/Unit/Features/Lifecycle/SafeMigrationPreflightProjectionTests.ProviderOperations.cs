namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>
    /// Rejects a foreign database identity before consulting sequence or opacity proofs, even for invariant
    /// failures.
    /// </summary>
    /// <param name="invariantUnsupported">
    /// Whether the native identity rejection is also marked intrinsically unsupported.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityMismatchPrecedesInvariantRejectionAndOpaqueSql(
        bool invariantUnsupported
    )
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "database_qualifier_mismatch")
        {
            IsInvariantUnsupported = invariantUnsupported,
        };

        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(0, probe.SequenceChecks);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>
    /// Retains an intrinsic provider rejection ahead of newly projected table facts and either opacity boundary.
    /// </summary>
    /// <param name="opaqueSql">Whether unstructured SQL invalidates the ordered structure proof.</param>
    /// <param name="opaqueProvider">Whether an unclassified provider operation invalidates the structure proof.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvariantUnsupportedPrecedesProjectedFactsAndOpaqueState(
        bool opaqueSql,
        bool opaqueProvider
    )
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var table = new CreateTableOperation { Name = "items" };

        table.Columns.Add(ProviderColumn("id", "items", isNullable: false, defaultValue: 0));
        projection.ObserveProviderPostcondition(table);

        if (opaqueProvider)
        {
            projection.ObserveProviderPostcondition(new AlterDatabaseOperation());
        }

        if (opaqueSql)
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        }

        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(
                SafeMigrationExpectedDefinitionFactory.From(table),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "unsupported_contract")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, decision.Action);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(0, probe.SequenceChecks);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>
    /// Reclassifies catalog-dependent unsupported evidence only through ordered, nonopaque projected facts.
    /// </summary>
    /// <param name="opaqueSql">Whether unstructured SQL invalidates the ordered structure proof.</param>
    /// <param name="expectedState">The independently declared exact projected catalog state.</param>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Matching)]
    [InlineData(true, SafeMigrationObservedState.PrerequisiteMissing)]
    public void CatalogDependentUnsupportedDoesNotBypassProjection(
        bool opaqueSql,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: false);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var table = new CreateTableOperation { Name = "items" };

        table.Columns.Add(ProviderColumn("id", "items", isNullable: false, defaultValue: 0));
        projection.ObserveProviderPostcondition(table);

        if (opaqueSql)
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        }

        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(
                SafeMigrationExpectedDefinitionFactory.From(table),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Unsupported);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.NotSame(live, analysis);
        Assert.Equal(expectedState, analysis.ObservedState);
        Assert.Equal(opaqueSql, analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(
            opaqueSql
                ? 0
                : 1,
            probe.SequenceChecks);
    }

    /// <summary>
    /// Rejects opaque SQL lineage before allowing the sequence-aware provider to reuse any catalog evidence.
    /// </summary>
    [Fact]
    public void OpaqueSqlPrecedesSequenceAwareProviderAnalysis()
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var operation = new SafeMigrationOperation(
            new DropTableIntent("items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
        Assert.True(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(0, probe.SequenceChecks);
    }

    /// <summary>
    /// Allows a provider-certified ordered operation to preserve native evidence despite generic opacity.
    /// </summary>
    [Fact]
    public void SequenceAwareProviderAnalysisPrecedesGenericOpacity()
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: true);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var operation = new SafeMigrationOperation(
            new DropTableIntent("items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(new AlterDatabaseOperation());

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(1, probe.SequenceChecks);
    }

    /// <summary>
    /// Requires an explicit ordered-provider proof before accepting catalog evidence across an opaque operation.
    /// </summary>
    [Fact]
    public void GenericOpacityInvalidatesAnalysisWithoutOrderedProviderProof()
    {
        // Arrange
        var probe = new ProjectionPrecedenceProbe(sequenceAware: false);
        var projection = new SafeMigrationPreflightProjection(probe, objectIdentityNormalizer: probe);
        var operation = new SafeMigrationOperation(
            new DropTableIntent("items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(new AlterDatabaseOperation());

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.True(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(1, probe.IdentityChecks);
        Assert.Equal(1, probe.SequenceChecks);
    }

    /// <summary>
    /// Carries an ordinary ADD column into the following nonunique-index prerequisite without inventing row proofs.
    /// </summary>
    [Fact]
    public void ProviderAddColumnProjectsFollowingNonUniqueIndexPrerequisite()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var addColumn = ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0);

        projection.ObserveProviderPostcondition(addColumn);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Recognizes the exact captured ADD shape as matching instead of reusing the historical missing result.
    /// </summary>
    [Fact]
    public void ProviderAddColumnProjectsItsExactDefinitionForFollowingEnsureColumn()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var providerColumn = ProviderColumn(
            "customer_id",
            "shipments",
            isNullable: false,
            defaultValue: 0);

        projection.ObserveProviderPostcondition(providerColumn);

        // Act
        var analysis = ProjectColumn(
            projection,
            "shipments",
            SafeMigrationExpectedDefinitionFactory.From(providerColumn),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    /// <summary>
    /// Rejects stale missing-column evidence when the accepted ordinary ADD has a different required shape.
    /// </summary>
    [Fact]
    public void ProviderAddColumnDoesNotReuseHistoricalMissingEvidenceForDifferentDefinition()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        // Act
        var analysis = ProjectColumn(
            projection,
            "shipments",
            new ExpectedColumnDefinition(
                "customer_id",
                typeof(int),
                isNullable: true,
                storeType: "int"),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.Equal("projected_different", analysis.Code);
    }

    /// <summary>
    /// Keeps the accepted ALTER target matching while denying historical repair approval for a different target.
    /// </summary>
    [Fact]
    public void ProviderAlterColumnDoesNotReuseHistoricalRepairEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var providerColumn = new AlterColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = typeof(string),
            ColumnType = "varchar(120)",
            IsNullable = true,
            MaxLength = 120,
        };

        projection.ObserveProviderPostcondition(providerColumn);

        // Act
        var matching = ProjectColumn(
            projection,
            "items",
            SafeMigrationExpectedDefinitionFactory.From(providerColumn),
            RepairableVarcharAnalysis());

        var different = ProjectColumn(
            projection,
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(200)",
                maxLength: 200),
            RepairableVarcharAnalysis());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, matching.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, different.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, different.RepairCapability);
    }

    /// <summary>
    /// Makes an ordinary exact-member DROP authoritative over an earlier native matching-column classification.
    /// </summary>
    [Fact]
    public void ProviderDropColumnProjectsFollowingEnsureColumnAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "customer_id",
                Table = "shipments",
            });

        // Act
        var analysis = ProjectColumn(
            projection,
            "shipments",
            new ExpectedColumnDefinition(
                "customer_id",
                typeof(int),
                isNullable: true,
                storeType: "int"),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Invalidates both old and target member snapshots after an uncertified rename on an existing table.
    /// </summary>
    [Fact]
    public void ProviderRenameColumnOnExistingTableInvalidatesHistoricalStructure()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var providerColumn = ProviderColumn("customer_id", "shipments", isNullable: true);

        projection.ObserveProviderPostcondition(providerColumn);
        projection.ObserveProviderPostcondition(
            new RenameColumnOperation
            {
                Name = "customer_id",
                NewName = "account_id",
                Table = "shipments",
            });

        var target = SafeMigrationExpectedDefinitionFactory.From(providerColumn);
        var targetDefinition = new ExpectedColumnDefinition(
            "account_id",
            target.ClrType,
            target.IsNullable,
            target.StoreType);

        // Act
        var targetAnalysis = ProjectColumn(
            projection,
            "shipments",
            targetDefinition,
            Live(SafeMigrationObservedState.Missing));

        var sourceAnalysis = ProjectColumn(
            projection,
            "shipments",
            SafeMigrationExpectedDefinitionFactory.From(providerColumn),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, targetAnalysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", targetAnalysis.Code);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, sourceAnalysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", sourceAnalysis.Code);
    }

    /// <summary>
    /// Records unsupported annotation opacity without throwing from observation, then rejects the following safe
    /// query.
    /// </summary>
    [Fact]
    public void UnsupportedProviderColumnAnnotationBlocksOnlyTheFollowingSafeOperation()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var providerColumn = ProviderColumn("customer_id", "shipments", isNullable: true);
        providerColumn.AddAnnotation("provider:opaque", new object());

        // Act
        var exception = Record.Exception(() => projection.ObserveProviderPostcondition(providerColumn));
        var analysis = ProjectColumn(
            projection,
            "shipments",
            new ExpectedColumnDefinition(
                "customer_id",
                typeof(int),
                isNullable: true,
                storeType: "int"),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Null(exception);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Retains ordinary table removal as a missing owner instead of trusting stale native table presence.
    /// </summary>
    [Fact]
    public void ProviderDropTableProjectsFollowingEnsureTableAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedTableDefinition("items", [Column("id")]);

        projection.ObserveProviderPostcondition(new DropTableOperation { Name = "items" });

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Uses the complete accepted ordinary CREATE shape to satisfy the following strict table contract.
    /// </summary>
    [Fact]
    public void ProviderCreateTableProjectsItsExactDefinitionForFollowingStrictEnsureTable()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var createTable = new CreateTableOperation { Name = "items" };
        createTable.Columns.Add(ProviderColumn("id", "items", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(createTable);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(
                    SafeMigrationExpectedDefinitionFactory.From(createTable),
                    SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    /// <summary>
    /// Separates provable table existence from strict-shape evidence invalidated by an ordinary table alteration.
    /// </summary>
    [Fact]
    public void ProviderAlterTableRetainsExistenceButInvalidatesHistoricalStrictShape()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedTableDefinition("items", [Column("id")]);

        projection.ObserveProviderPostcondition(
            new AlterTableOperation
            {
                Name = "items",
                Comment = "updated",
            });

        // Act
        var strict = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        var convergence = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(definition, SafeMigrationTableMode.ConvergenceContainer),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, strict.ObservedState);
        Assert.Equal("projected_structure_state_unknown", strict.Code);
        Assert.Equal(SafeMigrationObservedState.Matching, convergence.ObservedState);
        Assert.Equal("projected_matching", convergence.Code);
    }

    /// <summary>
    /// Keeps a separately owned complete table proof when ordinary DDL removes a member from another table.
    /// </summary>
    [Fact]
    public void ProviderDropColumnDoesNotInvalidateAnIndependentProjectedTable()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var independent = new ExpectedTableDefinition("audit_entries", [Column("id")]);

        Apply(
            projection,
            new EnsureTableIntent(independent, SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "customer_id",
                Table = "shipments",
            });

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(independent, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    /// <summary>
    /// Allows only row-independent nullable additions through a column-removal boundary with unknown table
    /// contents.
    /// </summary>
    /// <param name="isNullable">
    /// Whether the newly added member is nullable and therefore independent from row contents.
    /// </param>
    /// <param name="expectedState">The independently declared exact projected catalog state.</param>
    /// <param name="expectedAction">The independently declared exact planner action for the projected state.</param>
    [Theory]
    [InlineData(true, SafeMigrationObservedState.Missing, SafeMigrationAction.Apply)]
    [InlineData(false, SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing)]
    public void ProviderDropColumnRetainsOnlySafeAdditiveColumnEvidence(
        bool isNullable,
        SafeMigrationObservedState expectedState,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedColumnDefinition(
            "new_value",
            typeof(int),
            isNullable,
            "int");

        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "old_value",
                Table = "items",
            });

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
        Assert.Equal(expectedAction, decision.Action);
    }

    /// <summary>
    /// Carries only nullable unrelated-column absence through the provider-certified rename lifetime.
    /// </summary>
    /// <param name="isNullable">
    /// Whether the newly added member is nullable and therefore independent from row contents.
    /// </param>
    /// <param name="expectedState">The independently declared exact projected catalog state.</param>
    /// <param name="expectedAction">The independently declared exact planner action for the projected state.</param>
    [Theory]
    [InlineData(true, SafeMigrationObservedState.Missing, SafeMigrationAction.Apply)]
    [InlineData(false, SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing)]
    public void ProviderCertifiedRenameRetainsOnlySafeUnrelatedColumnAbsence(
        bool isNullable,
        SafeMigrationObservedState expectedState,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var providerProjection = new RenameAbsenceProjection();
        var projection = new SafeMigrationPreflightProjection(providerProjection);
        var renameOperation = new SafeMigrationOperation(
            new RenameColumnIntent("old_value", "items", "renamed_value"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var renameLive = Live(SafeMigrationObservedState.Matching);
        var unrelated = new ExpectedColumnDefinition(
            "new_value",
            typeof(int),
            isNullable,
            storeType: "int");

        var columnOperation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", unrelated),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var renameAnalysis = projection.Project(renameOperation, renameLive);
        var renameDecision = SafeMigrationDecisionPlanner.Plan(
            renameOperation.Intent.Kind,
            renameAnalysis.ObservedState,
            renameOperation.Policy,
            renameAnalysis.RepairCapability);

        projection.Observe(renameOperation, renameLive, renameAnalysis, renameDecision);

        var columnAnalysis = projection.Project(columnOperation, Live(SafeMigrationObservedState.Missing));
        var columnDecision = SafeMigrationDecisionPlanner.Plan(
            columnOperation.Intent.Kind,
            columnAnalysis.ObservedState,
            columnOperation.Policy,
            columnAnalysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, renameDecision.Action);
        Assert.True(providerProjection.WasQueried);
        Assert.Equal(expectedState, columnAnalysis.ObservedState);
        Assert.Equal(expectedAction, columnDecision.Action);
    }

    /// <summary>
    /// Does not mutate an earlier immutable classification when a later ordinary ADD supplies its prerequisite.
    /// </summary>
    [Fact]
    public void ProviderAddColumnDoesNotProjectBackwardsInOperationOrder()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Invalidates an incomplete full-table snapshot while retaining the newly added member for nonunique indexing.
    /// </summary>
    [Fact]
    public void ProviderAddColumnInvalidatesCompleteShapeButRetainsExactPrerequisite()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var originalTable = new ExpectedTableDefinition("shipments", [Column("id")]);

        Apply(
            projection,
            new EnsureTableIntent(originalTable, SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        var liveTable = Live(SafeMigrationObservedState.Different);

        // Act
        var tableAnalysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(originalTable, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            liveTable);

        var indexAnalysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, tableAnalysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", tableAnalysis.Code);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAnalysis.ObservedState);
        Assert.Equal("projected_missing", indexAnalysis.Code);
    }

    /// <summary>
    /// Retains table-scoped facts only when the active provider certifies the database alteration boundary.
    /// </summary>
    [Fact]
    public void ProviderAlterDatabasePreservesExistingTableScopedEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(new AlterDatabaseProjection());
        var definition = new ExpectedTableDefinition("items", [Column("id")]);

        Apply(
            projection,
            new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(new AlterDatabaseOperation());

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    /// <summary>
    /// Allows fresh table and nonunique-index projection after a provider-certified database alteration.
    /// </summary>
    [Fact]
    public void ProviderAlterDatabaseAllowsFollowingSafeTableAndIndexProjection()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(new AlterDatabaseProjection());
        var definition = new ExpectedTableDefinition("artifacts", [Column("id")]);

        projection.ObserveProviderPostcondition(new AlterDatabaseOperation());

        Apply(
            projection,
            new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition));

        // Act
        var analysis = ProjectIndex(projection, "artifacts", "id", unique: false);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Rejects speculative prerequisite facts after an unclassified database-wide operation.
    /// </summary>
    [Fact]
    public void AlterDatabaseWithoutProviderProofInvalidatesAllPrerequisiteFacts()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(new AlterDatabaseOperation());

        // Act
        var analysis = ProjectIndex(
            projection,
            "artifacts",
            "id",
            unique: false,
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Prevents earlier accepted column facts from crossing a later unstructured SQL boundary.
    /// </summary>
    [Fact]
    public void OpaqueProviderOperationInvalidatesAllPrerequisiteFacts()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "ALTER TABLE shipments DROP COLUMN customer_id;",
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Does not infer unique-row safety from a native snapshot preceding opaque provider SQL.
    /// </summary>
    [Fact]
    public void OpaqueProviderOperationInvalidatesLiveDataSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "INSERT INTO shipments (customer_id) VALUES (7), (7);",
            });

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>Retains structural nonunique-index proof independently across INSERT, UPDATE and DELETE.</summary>
    /// <param name="operationOrdinal">The independent ordinary DML operation ordinal.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProviderDataOperationsPreserveStructuralFactsForNonUniqueIndex(
        int operationOrdinal
    )
    {
        // Arrange
        MigrationOperation[] dataOperations =
        [
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            },
            new UpdateDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1 } },
                Columns = ["customer_id"],
                Values = new object[,] { { 7 } },
            },
            new DeleteDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1 } },
            },
        ];

        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(dataOperations[operationOrdinal]);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Consumes the projected empty-row proof when ordinary DML can introduce duplicate indexed values.
    /// </summary>
    [Fact]
    public void ProviderDataOperationInvalidatesProjectedUniqueIndexSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7 } },
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>Invalidates each row-dependent additive proof after an intervening ordinary data write.</summary>
    /// <param name="intentOrdinal">The independently evaluated primary, unique, CHECK, FK or column contract.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ProviderDataOperationInvalidatesEveryAdditiveDataSafetyProof(
        int intentOrdinal
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("parents", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7 } },
            });

        SafeMigrationIntent[] dataDependentOperations =
        [
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_shipments", "shipments", ["id"])),
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(
                    "uq_shipments_customer_id",
                    "shipments",
                    ["customer_id"])),
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_shipments_customer_id",
                    "shipments",
                    SafeMigrationSql.Binary(
                        SafeMigrationSql.Identifier("customer_id"),
                        SafeMigrationSqlBinaryOperator.GreaterThan,
                        SafeMigrationSql.Literal(0)))),
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_shipments_parents_customer_id",
                    "shipments",
                    ["customer_id"],
                    "parents",
                    ["id"])),
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "required_after_seed",
                    typeof(int),
                    isNullable: false,
                    storeType: "int")),
        ];

        var intent = dataDependentOperations[intentOrdinal];
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(
            intent is EnsureForeignKeyIntent
                ? "projected_structure_state_unknown"
                : "projected_data_state_unknown",
            analysis.Code);
    }

    /// <summary>
    /// Invalidates historical native unique-index approval after ordinary provider-owned row insertion.
    /// </summary>
    [Fact]
    public void ProviderDataOperationInvalidatesLiveUniqueIndexSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["customer_id"],
                Values = new object[,] { { 7 } },
            });

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Keeps the earlier DML invalidation active when a later nullable ADD supplies unrelated structural evidence.
    /// </summary>
    [Fact]
    public void LaterProviderDdlDoesNotEraseEarlierDataUncertainty()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7 } },
            });

        projection.ObserveProviderPostcondition(
            ProviderColumn("tracking_id", "shipments", isNullable: true));

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Preserves the original native nonunique-index classification because ordinary DML changes no structural
    /// proof.
    /// </summary>
    [Fact]
    public void ProviderDataOperationPreservesLiveNonUniqueIndexAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["customer_id"],
                Values = new object[,] { { 7 } },
            });

        var live = Live(SafeMigrationObservedState.Missing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Rejects required-column addition when ordinary DML invalidates the earlier native empty-table proof.
    /// </summary>
    [Fact]
    public void ProviderDataOperationInvalidatesLiveRequiredColumnSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            });

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "required_after_seed",
                    typeof(int),
                    isNullable: false,
                    storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Rejects stale nullability-tightening approval after ordinary UPDATE may have introduced NULL values.
    /// </summary>
    [Fact]
    public void ProviderDataOperationInvalidatesLiveNullabilityTighteningSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new UpdateDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1 } },
                Columns = ["customer_id"],
                Values = new object?[,] { { null } },
            });

        var oldColumn = new ExpectedColumnDefinition(
            "customer_id",
            typeof(int),
            isNullable: true,
            storeType: "int");

        var requiredColumn = new ExpectedColumnDefinition(
            "customer_id",
            typeof(int),
            isNullable: false,
            storeType: "int");

        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("shipments", requiredColumn, oldColumn),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Preserves a row-independent nullable-column addition even after ordinary writes consume row-safety proofs.
    /// </summary>
    [Fact]
    public void ProviderDataOperationRetainsSafeNullableColumnAddition()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("shipments", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            });

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "optional_after_seed",
                    typeof(int),
                    isNullable: true,
                    storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Keeps a later table-creation lifetime independent from writes against an earlier existing owner.
    /// </summary>
    [Fact]
    public void ProviderDataOperationDoesNotTaintLaterCreatedTable()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "existing_items",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            });

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Does not bind accepted member facts across a different table, schema or column identifier.
    /// </summary>
    /// <param name="providerTable">The physical owner named by the ordinary ADD operation.</param>
    /// <param name="providerSchema">The optional ordinary-operation schema identity.</param>
    /// <param name="providerColumn">The ordinary ADD member identity compared with the following index member.</param>
    [Theory]
    [InlineData("other_shipments", null, "customer_id")]
    [InlineData("shipments", "tenant", "customer_id")]
    [InlineData("shipments", null, "other_customer_id")]
    public void ProviderAddColumnDoesNotSatisfyDifferentObjectIdentity(
        string providerTable,
        string? providerSchema,
        string providerColumn
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn(
                providerColumn,
                providerTable,
                isNullable: false,
                defaultValue: 0,
                providerSchema));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Discards the earlier accepted member prerequisite when a following ordinary DROP removes that member.
    /// </summary>
    [Fact]
    public void ProviderDropColumnInvalidatesEarlierProjectedPrerequisite()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "customer_id",
                Table = "shipments",
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Keeps both historical and renamed index prerequisites unproven after an uncertified owner/member rename
    /// chain.
    /// </summary>
    [Fact]
    public void ProviderRenameSequenceDoesNotReuseHistoricalPrerequisites()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(
            new RenameColumnOperation
            {
                Name = "customer_id",
                NewName = "account_id",
                Table = "shipments",
            });

        projection.ObserveProviderPostcondition(
            new RenameTableOperation
            {
                Name = "shipments",
                NewName = "deliveries",
            });

        // Act
        var projected = ProjectIndex(projection, "deliveries", "account_id", unique: false);
        var stale = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_structure_state_unknown", projected.Code);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, stale.ObservedState);
        Assert.Equal("projected_structure_state_unknown", stale.Code);
    }

    /// <summary>
    /// Consumes the all-NULL unique-index proof when ordinary ALTER can replace nullable values with a required
    /// default.
    /// </summary>
    [Fact]
    public void ProviderAlterColumnDoesNotInventUniqueSafetyForExistingRows()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("external_id", "items", isNullable: true));

        // Act
        var initiallySafe = ProjectIndex(projection, "items", "external_id", unique: true);

        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "external_id",
                Table = "items",
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = false,
                DefaultValue = 0,
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var afterAlter = ProjectIndex(projection, "items", "external_id", unique: true, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, initiallySafe.ObservedState);
        Assert.Same(live, afterAlter);
    }

    /// <summary>
    /// Provider table creation grants child prerequisites and its later drop removes that owner proof.
    /// </summary>
    [Fact]
    public void ProviderCreateAndDropTableProjectAndInvalidateNewTableSafety()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var createTable = new CreateTableOperation { Name = "items" };

        createTable.Columns.Add(ProviderColumn("external_id", "items", isNullable: false, defaultValue: 0));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        projection.ObserveProviderPostcondition(createTable);

        var projected = ProjectIndex(projection, "items", "external_id", unique: true);

        projection.ObserveProviderPostcondition(new DropTableOperation { Name = "items" });

        var afterDrop = ProjectIndex(projection, "items", "external_id", unique: true, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, afterDrop.ObservedState);
        Assert.Equal("projected_prerequisite_missing", afterDrop.Code);
    }

    /// <summary>
    /// Projects an exact ordinary index removal as missing for its following nonunique replacement.
    /// </summary>
    [Fact]
    public void ProviderDropIndexProjectsExactOrdinaryReplacementAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: false,
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Makes ordinary index removal override an earlier accepted matching lifetime of the same physical name.
    /// </summary>
    [Fact]
    public void ProviderDropIndexInvalidatesAnEarlierAcceptedIndex()
    {
        // Arrange
        const string indexName = "ix_shipments_customer_id";
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false));

        ObserveAccepted(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    indexName,
                    "shipments",
                    [new ExpectedIndexKeyDefinition(column: "customer_id")])),
            SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = indexName,
                Table = "shipments",
            });

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: false,
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Rejects stale index evidence after ordinary DDL removes one of its accepted key members.
    /// </summary>
    [Fact]
    public void ProviderColumnDropInvalidatesAnEarlierAcceptedIndex()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false));

        ObserveAccepted(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_shipments_customer_id",
                    "shipments",
                    [new ExpectedIndexKeyDefinition(column: "customer_id")])),
            SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "customer_id",
                Table = "shipments",
            });

        // Act
        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: false,
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Retains a proven physical index removal across metadata-only table and column alterations.
    /// </summary>
    [Fact]
    public void SafeDropIndexSurvivesOrderedTableAndColumnMetadataAlterations()
    {
        // Arrange
        const string indexName = "ix_records_tenant_id_code";
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new DropIndexIntent(indexName, "records"),
            SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new AlterTableOperation
            {
                Name = "records",
                Comment = "target table comment",
                OldTable = new AlterTableOperation
                {
                    Name = "records",
                    Comment = "source table comment",
                },
            });

        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "code",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(180)",
                IsNullable = false,
                Collation = "utf8mb4_bin",
                Comment = "target code comment",
            });

        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "description",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(240)",
                IsNullable = false,
                DefaultValue = "target default",
            });

        var replacement = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    indexName,
                    "records",
                    [
                        new ExpectedIndexKeyDefinition(column: "tenant_id"),
                        new ExpectedIndexKeyDefinition(column: "code", prefixLength: 48),
                    ])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(
            replacement,
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Invalidates the earlier exact index removal when later opaque SQL can change the physical namespace.
    /// </summary>
    [Fact]
    public void SafeDropIndexDoesNotSurviveAnOpaqueProviderOperation()
    {
        // Arrange
        const string indexName = "ix_shipments_customer_id";
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new DropIndexIntent(indexName, "shipments"),
            SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "ALTER TABLE shipments ADD INDEX ix_unknown (customer_id);",
            });

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Retains unsupported, data-blocked and missing-prerequisite native rejections despite an earlier index
    /// removal.
    /// </summary>
    /// <param name="state">The unsafe native result that an index-removal proof must never override.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void ProviderDropIndexDoesNotOverrideUnsafeReplacementAnalysis(
        SafeMigrationObservedState state
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        var live = Live(state);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Uses exact physical removal to replace only its own authored index identity after native semantic matching.
    /// </summary>
    [Fact]
    public void ProviderDropIndexProjectsAnExactNameReplacementAfterSemanticAliasAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Retains the native duplicate-row rejection instead of turning physical index removal into unique-row proof.
    /// </summary>
    [Fact]
    public void ProviderDropIndexPreservesDuplicateRowEvidenceForUniqueReplacement()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "index_replacement_data_blocked");

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, analysis.ObservedState);
        Assert.Equal("index_replacement_data_blocked", analysis.Code);
    }

    /// <summary>
    /// Leaves the native result unchanged when an ordinary DROP removes another physical index name.
    /// </summary>
    [Fact]
    public void ProviderDropIndexDoesNotProjectASeparateIndexIdentity()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_other",
                Table = "shipments",
            });

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Invalidates exact-owner index removal proof after a tableless provider DROP has unknown namespace effects.
    /// </summary>
    [Fact]
    public void TablelessProviderDropIndexFailsClosedWithoutAnOwnershipProjection()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "provider_global_index",
                Table = null,
            });

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    private static AddColumnOperation ProviderColumn(
        string name,
        string table,
        bool isNullable,
        object? defaultValue = null,
        string? schema = null
    ) => new()
    {
        Name = name,
        Table = table,
        Schema = schema,
        ClrType = typeof(int),
        ColumnType = "int",
        IsNullable = isNullable,
        DefaultValue = defaultValue,
    };

    private static SafeMigrationProviderAnalysis ProjectIndex(
        SafeMigrationPreflightProjection projection,
        string table,
        string column,
        bool unique,
        SafeMigrationProviderAnalysis? live = null
    )
    {
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    $"ix_{table}_{column}",
                    table,
                    [new ExpectedIndexKeyDefinition(column: column)],
                    unique: unique)),
            SafeMigrationPolicy.ThrowIfDifferent);

        return projection.Project(operation, live ?? Live(SafeMigrationObservedState.PrerequisiteMissing));
    }

    private static SafeMigrationProviderAnalysis ProjectColumn(
        SafeMigrationPreflightProjection projection,
        string table,
        ExpectedColumnDefinition definition,
        SafeMigrationProviderAnalysis live
    )
    {
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(table, definition),
            SafeMigrationPolicy.RepairIfSafe);

        return projection.Project(operation, live);
    }

    private sealed class ProjectionPrecedenceProbe : ISafeMigrationProviderOperationProjection,
        ISafeMigrationProviderObjectIdentityNormalizer
    {
        private readonly bool _sequenceAware;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="sequenceAware">The supplied sequence aware for this fixture.</param>
        public ProjectionPrecedenceProbe(
            bool sequenceAware
        )
        {
            _sequenceAware = sequenceAware;
        }

        /// <summary>Gets how often physical identity was checked before projection.</summary>
        public int IdentityChecks { get; private set; }

        /// <summary>Gets how often lower-priority ordered provider evidence was consulted.</summary>
        public int SequenceChecks { get; private set; }

        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public string NormalizeIdentifier(
            string identifier
        ) => identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(
            string? schema
        ) => schema;

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        )
        {
            IdentityChecks++;

            return StringComparer.Ordinal.Equals(analysis.Code, "database_qualifier_mismatch");
        }

        /// <inheritdoc />
        public bool PreservesExistingTableState(
            MigrationOperation operation
        ) => false;

        /// <inheritdoc />
        public bool IsSequenceAwareAnalysis(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis analysis
        )
        {
            SequenceChecks++;

            return _sequenceAware;
        }
    }

    private sealed class AlterDatabaseProjection : ISafeMigrationProviderOperationProjection
    {
        public bool PreservesExistingTableState(
            MigrationOperation operation
        ) => operation is AlterDatabaseOperation;
    }

    private sealed class RenameAbsenceProjection : ISafeMigrationProviderOperationProjection
    {
        public bool WasQueried { get; private set; }

        public bool PreservesExistingTableState(
            MigrationOperation operation
        ) => false;

        public bool PreservesUnrelatedColumnAbsence(
            RenameColumnIntent operation
        )
        {
            WasQueried = true;

            return true;
        }
    }
}
