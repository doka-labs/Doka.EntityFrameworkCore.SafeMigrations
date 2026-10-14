namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Accepted new owners cannot suppress requested native effective-default qualification.</summary>
    /// <param name="forceNative">Whether the source independently requires native endpoint qualification.</param>
    /// <param name="compatible">Whether the native effective endpoint defaults are compatible.</param>
    /// <param name="newKey">Whether the parent key is freshly applied instead of matching.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void NewForeignKeyOwnerRetainsRequestedNativeDefaultQualification(
        bool forceNative,
        bool compatible,
        bool newKey
    )
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            ForceNativeQualification = forceNative,
            EndpointsCompatible = compatible,
        };

        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);
        ObserveAccepted(projection, new EnsureColumnIntent("live_parent",
                new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parent", "live_parent", ["id"])),
            newKey ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureTableIntent(new ExpectedTableDefinition("live_child",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int")],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_child", "live_child", ["id"])),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(forceNative && !compatible ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(forceNative ? 1 : 0, source.ForeignKeyQueries);
    }

    /// <summary>Unchanged live members complete sparse accepted additions without model seeding.</summary>
    /// <param name="composite">Whether each endpoint includes an unchanged live member.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseForeignKeyUsesCertifiedLivePrincipal(bool composite)
    {
        // Arrange
        var source = new LivePrerequisiteProbe();
        var projection = SparseLiveProjection(source);
        var intent = SparseLiveForeignKey(composite);

        // Act
        var analysis = Project(projection, intent, SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(1, source.ForeignKeyQueries);
        Assert.True(source.SawAcceptedAddition);
    }

    /// <summary>Catalog compatibility, owner presence and candidate-key evidence remain independent gates.</summary>
    /// <param name="missingProof">The independently absent certificate.</param>
    [Theory]
    [InlineData("provider")]
    [InlineData("column")]
    [InlineData("compatibility")]
    [InlineData("key")]
    public void SparseForeignKeyRejectsMissingLiveProof(string missingProof)
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            HasPrincipalColumn = missingProof != "column",
            EndpointsCompatible = missingProof != "compatibility",
            CandidateKeys = missingProof == "key" ? [] : [new(SafeMigrationDatabaseObjectKind.PrimaryKey, "pk_parent")],
        };

        var projection = SparseLiveProjection(missingProof == "provider" ? null : source);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>Nullable declarations cannot substitute for actual accepted NULL-preserving additions.</summary>
    /// <param name="rowProof">The operation or row evidence that must remain insufficient.</param>
    [Theory]
    [InlineData("no_add")]
    [InlineData("matching")]
    [InlineData("rejected")]
    [InlineData("default")]
    [InlineData("computed")]
    [InlineData("dml")]
    [InlineData("raw_sql")]
    public void SparseForeignKeyRejectsUnprovenExistingRows(string rowProof)
    {
        // Arrange
        var source = new LivePrerequisiteProbe();
        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);
        var column = new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int",
            defaultValue: rowProof == "default" ? SafeMigrationDefaultValue.Literal(999) : null,
            computedColumnSql: rowProof == "computed" ? "999" : null);

        if (rowProof == "rejected")
        {
            var operation = new SafeMigrationOperation(new EnsureColumnIntent("live_child", column),
                SafeMigrationPolicy.ThrowIfDifferent);
            var rejected = Live(SafeMigrationObservedState.DataBlocked);
            var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, rejected.ObservedState,
                operation.Policy, rejected.RepairCapability);

            projection.Observe(operation, rejected, rejected, decision);
        }
        else if (rowProof != "no_add")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("live_child", column),
                rowProof == "matching" ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing);
        }

        if (rowProof == "dml")
        {
            projection.ObserveProviderPostcondition(new UpdateDataOperation
            {
                Table = "live_child", KeyColumns = ["id"], KeyValues = new object[,] { { 1 } },
                Columns = ["parent_id"], Values = new object[,] { { 999 } },
            });
        }
        else if (rowProof == "raw_sql")
        {
            projection.ObserveProviderPostcondition(new SqlOperation
            {
                Sql = "UPDATE live_child SET parent_id = 999;",
            });
        }

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>Removing a candidate or its owned backing index invalidates only that physical certificate.</summary>
    /// <param name="kind">The independently captured key kind.</param>
    /// <param name="dropBacking">Whether the index is removed instead of its constraint.</param>
    /// <param name="survivingAlias">Whether another untouched native candidate remains.</param>
    [Theory]
    [InlineData(SafeMigrationDatabaseObjectKind.PrimaryKey, false, false)]
    [InlineData(SafeMigrationDatabaseObjectKind.UniqueConstraint, false, false)]
    [InlineData(SafeMigrationDatabaseObjectKind.UniqueConstraint, true, false)]
    [InlineData(SafeMigrationDatabaseObjectKind.Index, false, false)]
    [InlineData(SafeMigrationDatabaseObjectKind.UniqueConstraint, true, true)]
    public void SparseForeignKeyRespectsLiveCandidateRemoval(
        SafeMigrationDatabaseObjectKind kind,
        bool dropBacking,
        bool survivingAlias
    )
    {
        // Arrange
        var candidate = new SafeMigrationLiveCandidateKey(kind, "live_key", "live_backing");
        var source = new LivePrerequisiteProbe
        {
            CandidateKeys = survivingAlias ? [candidate, new(SafeMigrationDatabaseObjectKind.Index, "other_key")]
                : [candidate],
        };

        var projection = SparseLiveProjection(source);
        SafeMigrationIntent removal = dropBacking ? new DropIndexIntent("live_backing", "live_parent")
            : kind switch
            {
                SafeMigrationDatabaseObjectKind.PrimaryKey => new DropPrimaryKeyIntent("live_key", "live_parent"),
                SafeMigrationDatabaseObjectKind.UniqueConstraint =>
                    new DropUniqueConstraintIntent("live_key", "live_parent"),
                _ => new DropIndexIntent("live_key", "live_parent"),
            };

        ObserveAccepted(projection, removal, SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(survivingAlias ? SafeMigrationObservedState.Missing
                : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
    }

    /// <summary>A reused physical name cannot resurrect the old candidate's different column shape.</summary>
    /// <param name="kind">The physical replacement family.</param>
    [Theory]
    [InlineData(SafeMigrationDatabaseObjectKind.PrimaryKey)]
    [InlineData(SafeMigrationDatabaseObjectKind.UniqueConstraint)]
    [InlineData(SafeMigrationDatabaseObjectKind.Index)]
    public void SparseForeignKeyRejectsDifferentSameNameReplacement(SafeMigrationDatabaseObjectKind kind)
    {
        // Arrange
        var source = new LivePrerequisiteProbe { CandidateKeys = [new(kind, "live_key")] };
        var projection = SparseLiveProjection(source);
        SafeMigrationIntent removal = kind switch
        {
            SafeMigrationDatabaseObjectKind.PrimaryKey => new DropPrimaryKeyIntent("live_key", "live_parent"),
            SafeMigrationDatabaseObjectKind.UniqueConstraint =>
                new DropUniqueConstraintIntent("live_key", "live_parent"),
            _ => new DropIndexIntent("live_key", "live_parent"),
        };

        SafeMigrationIntent replacement = kind switch
        {
            SafeMigrationDatabaseObjectKind.PrimaryKey => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("live_key", "live_parent", ["other"])),
            SafeMigrationDatabaseObjectKind.UniqueConstraint => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("live_key", "live_parent", ["other"])),
            _ => new EnsureIndexIntent(new ExpectedIndexDefinition("live_key", "live_parent",
                [new ExpectedIndexKeyDefinition(column: "other")], unique: true)),
        };

        ObserveAccepted(projection, removal, SafeMigrationObservedState.Matching);
        if (kind == SafeMigrationDatabaseObjectKind.PrimaryKey)
        {
            // WHY: A primary-key replacement needs an independent current empty
            // certificate; the nullable child addition cannot supply parent rows.
            ObserveAccepted(projection, new EnsureColumnIntent("live_parent", Column("other")),
                SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsureColumnIntent("live_parent", Column("empty_marker")),
                SafeMigrationObservedState.Missing);
        }

        ObserveAccepted(projection, replacement, SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Table and member lifecycle invalidation dominates an immutable source certificate.</summary>
    /// <param name="mutation">The earlier accepted destructive mutation.</param>
    [Theory]
    [InlineData("drop_table")]
    [InlineData("recreate_table")]
    [InlineData("drop_column")]
    [InlineData("rename_column")]
    public void SparseForeignKeyRejectsInvalidatedLiveOwner(string mutation)
    {
        // Arrange
        var source = new LivePrerequisiteProbe();
        var projection = SparseLiveProjection(source);

        if (mutation is "drop_table" or "recreate_table")
        {
            ObserveAccepted(projection, new DropTableIntent("live_parent"), SafeMigrationObservedState.Matching);
            if (mutation == "recreate_table")
            {
                Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("live_parent", [Column("id")]),
                    SafeMigrationTableMode.StrictDefinition));
            }
        }
        else if (mutation == "drop_column")
        {
            ObserveAccepted(projection, new DropColumnIntent("id", "live_parent"), SafeMigrationObservedState.Matching);
        }
        else
        {
            ObserveAccepted(projection, new RenameColumnIntent("id", "live_parent", "renamed"),
                SafeMigrationObservedState.Matching);
        }

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>Sparse index projection combines old and new members without inventing their row semantics.</summary>
    /// <param name="unique">Whether the key is unique.</param>
    /// <param name="nullsDistinct">Whether repeated NULLs are permitted by this contract.</param>
    /// <param name="expected">The safe projected classification.</param>
    [Theory]
    [InlineData(false, true, SafeMigrationObservedState.Missing)]
    [InlineData(true, true, SafeMigrationObservedState.Missing)]
    [InlineData(true, false, SafeMigrationObservedState.PrerequisiteMissing)]
    public void SparseCompositeIndexRetainsNullSemantics(
        bool unique,
        bool nullsDistinct,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var projection = SparseLiveProjection(new LivePrerequisiteProbe());
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("ix_child", "live_child",
            [new ExpectedIndexKeyDefinition(column: "id"), new ExpectedIndexKeyDefinition(column: "parent_id")],
            unique: unique, nullsDistinct: unique ? nullsDistinct : null));

        // Act
        var analysis = Project(projection, intent, SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>Unique indexes cannot certify foreign-key equality through unproven key facets.</summary>
    /// <param name="facet">The explicit key facet, or ordinary inherited column semantics.</param>
    /// <param name="projectPrincipalColumn">Whether the principal column also has an accepted definition.</param>
    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("ordinary", true)]
    [InlineData("collation", false)]
    [InlineData("collation", true)]
    [InlineData("operator_class", false)]
    [InlineData("operator_class", true)]
    [InlineData("prefix", false)]
    [InlineData("prefix", true)]
    [InlineData("partial", false)]
    [InlineData("partial", true)]
    public void SparseForeignKeyChecksAcceptedIndexEquality(string facet, bool projectPrincipalColumn)
    {
        // Arrange
        var source = new LivePrerequisiteProbe { CandidateKeys = [] };
        var projection = SparseLiveProjection(source);
        var key = new ExpectedIndexKeyDefinition(column: "id",
            prefixLength: facet == "prefix" ? 1 : null,
            collation: facet == "collation" ? new SafeMigrationCollationIdentifier("NOCASE") : null,
            operatorClass: facet == "operator_class" ? "custom_ops" : null);

        if (projectPrincipalColumn)
        {
            ObserveAccepted(projection, new EnsureColumnIntent("live_parent", Column("id")),
                SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("new_key", "live_parent",
                [key], unique: true, filter: facet == "partial" ? "id > 0" : null)),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(facet == "ordinary" ? SafeMigrationObservedState.Missing
                : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
        Assert.True(facet == "ordinary" && projectPrincipalColumn || source.ForeignKeyQueries == 1);
    }

    /// <summary>The same sparse member proof supports constraints without changing their row-safety rules.</summary>
    /// <param name="kind">The constraint whose initial prerequisite query could not bind the new member.</param>
    /// <param name="empty">Whether the required new member independently certifies an empty owner.</param>
    [Theory]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public void SparseMixedConstraintsPreserveIndependentRowGates(string kind, bool empty)
    {
        // Arrange
        var source = new LivePrerequisiteProbe();
        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);

        ObserveAccepted(projection,
            new EnsureColumnIntent("live_child", empty ? Column("parent_id") : NullableColumn("parent_id")),
            SafeMigrationObservedState.Missing);

        SafeMigrationIntent intent = kind switch
        {
            "unique" => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_child", "live_child", ["id", "parent_id"])),
            "primary" => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_child", "live_child", ["id", "parent_id"])),
            _ => new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression(
                "ck_child", "live_child", SafeMigrationSql.Binary(Positive("id"),
                    SafeMigrationSqlBinaryOperator.And, Positive("parent_id")))),
        };

        // Act
        var analysis = Project(projection, intent, SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(empty || kind == "unique" ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Rejected and absent removals do not destroy valid untouched live key evidence.</summary>
    /// <param name="dropState">The independently observed failed or absent removal state.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void SparseLiveKeySurvivesNonExecutingDrop(SafeMigrationObservedState dropState)
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            CandidateKeys = [new(SafeMigrationDatabaseObjectKind.Index, "live_key")],
        };
        var projection = SparseLiveProjection(source);
        var operation = new SafeMigrationOperation(new DropIndexIntent("live_key", "live_parent"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(dropState);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, dropState,
            operation.Policy, live.RepairCapability);

        projection.Observe(operation, live, live, decision);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(dropState == SafeMigrationObservedState.Missing ? SafeMigrationAction.NoOp
            : SafeMigrationAction.RejectUnsupported, decision.Action);
    }

    /// <summary>A proven equivalent new key can replace its removed snapshot lifetime.</summary>
    [Fact]
    public void SparseForeignKeyUsesEquivalentAcceptedKeyReplacement()
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            CandidateKeys = [new(SafeMigrationDatabaseObjectKind.Index, "live_key")],
        };
        var projection = SparseLiveProjection(source);

        ObserveAccepted(projection, new DropIndexIntent("live_key", "live_parent"),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("live_key", "live_parent",
                [new ExpectedIndexKeyDefinition(column: "id")], unique: true)),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(1, source.ForeignKeyQueries);
    }

    /// <summary>Reused matching key shapes cannot replace required native reference-key qualification.</summary>
    /// <param name="created">Whether accepted Apply establishes a new ordinary key.</param>
    /// <param name="nativeCandidate">Whether an independently eligible native key exists.</param>
    /// <param name="compatible">Whether native endpoint/owner comparison semantics match.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void SparseMatchingDefinitionsCannotBypassNativeKeyQualification(
        bool created,
        bool nativeCandidate,
        bool compatible
    )
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            ForceNativeQualification = true,
            EndpointsCompatible = compatible,
            CandidateKeys = nativeCandidate ? [new(SafeMigrationDatabaseObjectKind.PrimaryKey, "pk_parent")] : [],
        };

        var projection = SparseLiveProjection(source);

        ObserveAccepted(projection, new EnsureColumnIntent("live_parent", Column("id")),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("candidate", "live_parent", ["id"])),
            created ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(compatible && (created || nativeCandidate) ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(1, source.ForeignKeyQueries);
    }

    /// <summary>Only a surviving accepted primary-key allocation can replace an empty native key certificate.</summary>
    /// <param name="history">The accepted creation, matching alias, or subsequent physical removal.</param>
    /// <param name="survives">Whether an actual new primary-key lifetime remains available.</param>
    [Theory]
    [InlineData("created_then_matching", true)]
    [InlineData("created_then_alias", true)]
    [InlineData("matching_only", false)]
    [InlineData("created_then_dropped", false)]
    public void SparseForeignKeyRetainsOnlySurvivingCreatedPrimaryKey(string history, bool survives)
    {
        // Arrange
        var source = new LivePrerequisiteProbe { ForceNativeQualification = true, CandidateKeys = [] };
        var projection = SparseLiveProjection(source);
        var key = new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_parent", "live_parent", ["id"]));

        ObserveAccepted(projection, new EnsureColumnIntent("live_parent", Column("id")),
            SafeMigrationObservedState.Matching);
        if (history != "matching_only")
        {
            ObserveAccepted(projection, key, SafeMigrationObservedState.Missing);
        }

        if (history == "created_then_dropped")
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_parent", "live_parent"),
                SafeMigrationObservedState.Matching);
        }
        else
        {
            var matching = history == "created_then_alias"
                ? new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("logical_pk", "live_parent", ["id"]))
                : key;

            ObserveAccepted(projection, matching, SafeMigrationObservedState.Matching, "pk_parent");
        }

        // Act
        var analysis = Project(projection, SparseLiveForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(survives ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
        Assert.Equal(1, source.ForeignKeyQueries);
    }

    /// <summary>Discharged native capability blockers still need independent structure and current row proof.</summary>
    /// <param name="scenario">The provider certificate, row proof, or earlier immutable boundary.</param>
    /// <param name="expected">The state permitted by the combined independent proofs.</param>
    [Theory]
    [InlineData("qualified", SafeMigrationObservedState.Missing)]
    [InlineData("no_certificate", SafeMigrationObservedState.Unsupported)]
    [InlineData("no_row_proof", SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData("bad_endpoints", SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData("invariant", SafeMigrationObservedState.Unsupported)]
    [InlineData("opaque_sql", SafeMigrationObservedState.PrerequisiteMissing)]
    public void SparseDynamicForeignKeyRequalificationPreservesIndependentGates(
        string scenario,
        SafeMigrationObservedState expected)
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            RequalifiesDynamicForeignKey = scenario != "no_certificate",
            EndpointsCompatible = scenario != "bad_endpoints",
        };

        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);
        var column = new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int",
            defaultValue: scenario == "no_row_proof" ? SafeMigrationDefaultValue.Literal(999) : null);

        ObserveAccepted(projection, new EnsureColumnIntent("live_child", column), SafeMigrationObservedState.Missing);
        if (scenario == "opaque_sql")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        }

        var operation = new SafeMigrationOperation(SparseLiveForeignKey(), SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_dynamic_native_fk")
        {
            IsInvariantUnsupported = scenario == "invariant",
        };

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.Equal(scenario is "invariant" or "opaque_sql" ? 0 : 1, source.RequalificationQueries);
    }

    /// <summary>
    /// Certified native index removal cannot bypass immutable capability or physical-lifetime boundaries.
    /// </summary>
    /// <param name="scenario">The certificate or earlier immutable boundary.</param>
    /// <param name="expected">The state permitted by independent ordered proofs.</param>
    [Theory]
    [InlineData("qualified", SafeMigrationObservedState.Matching)]
    [InlineData("no_certificate", SafeMigrationObservedState.Unsupported)]
    [InlineData("invariant", SafeMigrationObservedState.Unsupported)]
    [InlineData("opaque_sql", SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData("removed_owner", SafeMigrationObservedState.Missing)]
    public void SparseDynamicDropIndexRequalificationPreservesIndependentGates(
        string scenario,
        SafeMigrationObservedState expected)
    {
        // Arrange
        var source = new LivePrerequisiteProbe
        {
            RequalifiesDynamicDropIndex = scenario != "no_certificate",
        };

        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);
        if (scenario == "opaque_sql")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        }

        if (scenario == "removed_owner")
        {
            ObserveAccepted(projection, new DropTableIntent("live_child"), SafeMigrationObservedState.Matching);
        }

        var operation = new SafeMigrationOperation(new DropIndexIntent("ix_child", "live_child"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_dynamic_native_index")
        {
            IsInvariantUnsupported = scenario == "invariant",
        };

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
        Assert.Equal(scenario is "invariant" or "opaque_sql" ? 0 : 1, source.RequalificationQueries);
    }

    /// <summary>Creates a sparse child certificate without converging either owner or its old members.</summary>
    private static SafeMigrationPreflightProjection SparseLiveProjection(LivePrerequisiteProbe? source)
    {
        var projection = new SafeMigrationPreflightProjection(livePrerequisiteSource: source);

        ObserveAccepted(projection, new EnsureColumnIntent("live_child", NullableColumn("parent_id")),
            SafeMigrationObservedState.Missing);

        return projection;
    }

    /// <summary>Builds the exact FK endpoint order used by the sparse-provider certificate.</summary>
    private static EnsureForeignKeyIntent SparseLiveForeignKey(bool composite = false) => new(
        new ExpectedForeignKeyDefinition("fk_child", "live_child", composite ? ["id", "parent_id"] : ["parent_id"],
            "live_parent", composite ? ["tenant", "id"] : ["id"]));

    /// <summary>Supplies observed old members; new members must come from accepted ordered projection.</summary>
    private sealed class LivePrerequisiteProbe : ISafeMigrationLivePrerequisiteSource
    {
        /// <summary>Gets whether the real parent endpoint was observed in the native catalog.</summary>
        public bool HasPrincipalColumn { get; init; } = true;

        /// <summary>Gets whether the provider certifies native storage and owner compatibility.</summary>
        public bool EndpointsCompatible { get; init; } = true;

        /// <summary>Gets whether the source requires metadata despite equal authored CLR/storage facets.</summary>
        public bool ForceNativeQualification { get; init; }

        /// <summary>Gets whether accepted native prerequisite mutations discharge a dynamic FK blocker.</summary>
        public bool RequalifiesDynamicForeignKey { get; init; }

        /// <summary>
        /// Gets whether native index presence and exact dependency removal are independently certified.
        /// </summary>
        public bool RequalifiesDynamicDropIndex { get; init; }

        /// <summary>Gets how often an eligible dynamic FK input reached the provider requalification gate.</summary>
        public int RequalificationQueries { get; private set; }

        /// <summary>Gets independently observed physical keys, without invented target-model identities.</summary>
        public IReadOnlyList<SafeMigrationLiveCandidateKey> CandidateKeys { get; init; } =
            [new(SafeMigrationDatabaseObjectKind.PrimaryKey, "pk_parent")];

        /// <summary>Gets how many sparse FK certificates Core actually requested.</summary>
        public int ForeignKeyQueries { get; private set; }

        /// <summary>Gets whether the source was consulted with an already accepted new member.</summary>
        public bool SawAcceptedAddition { get; private set; }

        /// <inheritdoc />
        public bool HasLiveColumn(string table, string? schema, string column) => schema is null
            && (table == "live_parent" ? HasPrincipalColumn && column is "id" or "tenant" or "other"
                : table == "live_child" && column == "id");

        /// <inheritdoc />
        public bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent) => ForceNativeQualification;

        /// <inheritdoc />
        public bool CanRequalifyLiveForeignKeyPrerequisites(
            EnsureForeignKeyIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis,
            ISafeMigrationProjectedColumnSource columns)
        {
            RequalificationQueries++;

            return RequalifiesDynamicForeignKey;
        }

        /// <inheritdoc />
        public bool CanRequalifyLiveDropIndexPrerequisites(
            DropIndexIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis,
            ISafeMigrationProjectedColumnSource columns)
        {
            RequalificationQueries++;

            return RequalifiesDynamicDropIndex;
        }

        /// <inheritdoc />
        public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
            EnsureForeignKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns
        )
        {
            ForeignKeyQueries++;
            SawAcceptedAddition = columns.TryGetProjectedColumn("live_child", null, "parent_id", out _);

            return EndpointsCompatible ? CandidateKeys : null;
        }
    }
}
