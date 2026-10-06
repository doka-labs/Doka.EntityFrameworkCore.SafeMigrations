namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>A seed insert into a new plain table preserves unrelated cyclic-FK row proofs.</summary>
    /// <param name="mode">The table creation mode exercised by the migration stream.</param>
    [Theory]
    [InlineData(SafeMigrationTableMode.StrictDefinition)]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer)]
    public void ConfinedSeedInsertPreservesUnrelatedCyclicForeignKeys(SafeMigrationTableMode mode)
    {
        // Arrange
        var projection = MutationIsolationProjection(mode);
        var firstForeignKey = MutationIsolationForeignKey("first", "second");
        var secondForeignKey = MutationIsolationForeignKey("second", "first");

        Accept(projection, Operation(new EnsureTableIntent(RoleTable(), mode)), SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var first = projection.Project(firstForeignKey, Live(SafeMigrationObservedState.PrerequisiteMissing));
        var second = projection.Project(secondForeignKey, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, first.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, second.ObservedState);
        Assert.Equal("projected_missing", first.Code);
        Assert.Equal("projected_missing", second.Code);
    }

    /// <summary>A confined seed insert preserves unrelated empty-row proofs for keys and required columns.</summary>
    /// <param name="kind">The data-dependent operation consuming the unrelated table's empty-row proof.</param>
    [Theory]
    [InlineData("primary-key")]
    [InlineData("unique-constraint")]
    [InlineData("required-column")]
    public void ConfinedSeedInsertPreservesUnrelatedKeyAndColumnRowProofs(string kind)
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var unrelated = new ExpectedTableDefinition("unrelated", RoleTable().Columns);

        Accept(projection,
            Operation(new EnsureTableIntent(unrelated, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        SafeMigrationIntent intent = kind switch
        {
            "primary-key" => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_unrelated", "unrelated", ["id"])),
            "unique-constraint" => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_unrelated_name", "unrelated", ["name"])),
            "required-column" => new EnsureColumnIntent("unrelated",
                new ExpectedColumnDefinition("rank", typeof(int), isNullable: false, storeType: "int")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var operation = Operation(intent);

        // Act
        var projected = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind, projected.ObservedState, operation.Policy, projected.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
    }

    /// <summary>A confined insert still invalidates the written table's generic empty-row proof.</summary>
    /// <param name="mode">The table creation mode exercised by the migration stream.</param>
    [Theory]
    [InlineData(SafeMigrationTableMode.StrictDefinition)]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer)]
    public void ConfinedSeedInsertInvalidatesItsOwnForeignKeyRowProof(SafeMigrationTableMode mode)
    {
        // Arrange
        var projection = MutationIsolationProjection(mode);
        var ensure = Operation(new EnsureModelManagedDataIntent(
            "first",
            ["id"],
            ["int"],
            ["id", "reference_id"],
            ["int", "int"],
            new object?[,] { { 1, 42 } },
            schema: null,
            uniqueKeys: null));

        ObserveAppliedMutation(projection, ensure, SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Existing tables cannot certify that a managed insert has no cross-table effects.</summary>
    [Fact]
    public void ExistingTableSeedInsertInvalidatesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.ConvergenceContainer)),
            SafeMigrationObservedState.Matching);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.Missing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Updates and deletions retain global invalidation, including cascade effects.</summary>
    /// <param name="delete">Whether the managed mutation deletes rather than updates the seed row.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedUpdateOrDeleteInvalidatesUnrelatedForeignKeyRowProof(bool delete)
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        var mutation = delete
            ? Operation(new DeleteModelManagedDataIntent(
                "roles", ["id"], ["int"], new object?[,] { { 1 } },
                ["id", "name"], ["int", "varchar(64)"], new object?[,] { { 1, "administrator" } },
                schema: null, foreignKeys: null))
            : RoleUpdate();

        ObserveAppliedMutation(projection, mutation, SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Raw provider DML is not implicitly certified by the managed-seed confinement rule.</summary>
    [Fact]
    public void ProviderInsertIntoNewTableStillInvalidatesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(new InsertDataOperation
        {
            Table = "roles",
            Columns = ["id", "name"],
            ColumnTypes = ["int", "varchar(64)"],
            Values = new object?[,] { { 1, "administrator" } },
        });

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Expression-bearing table definitions cannot prove managed inserts are confined.</summary>
    /// <param name="hazard">The SQL-bearing definition that can invoke provider behavior.</param>
    [Theory]
    [InlineData("sql-default")]
    [InlineData("structured-default")]
    [InlineData("sql-computed")]
    [InlineData("structured-computed")]
    [InlineData("sql-check")]
    [InlineData("structured-check")]
    public void ExpressionBearingSeedTableInvalidatesUnrelatedForeignKeyRowProof(string hazard)
    {
        // Arrange
        var projection = MutationIsolationProjection();
        var table = MutationIsolationHazardTable(hazard);

        Accept(projection,
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Later accepted expression-bearing artifacts revoke the plain-table insert certificate.</summary>
    /// <param name="hazard">The artifact added after the plain table was created.</param>
    [Theory]
    [InlineData("column-default")]
    [InlineData("check")]
    [InlineData("index-expression")]
    [InlineData("index-filter")]
    [InlineData("index-structured-expression")]
    [InlineData("index-structured-filter")]
    [InlineData("index-method")]
    [InlineData("index-operator-class")]
    public void AddedExpressionArtifactInvalidatesUnrelatedForeignKeyRowProof(string hazard)
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);

        // WHY: The provider has accepted the artifact; this test isolates the
        // subsequent seed classification from provider expression support.
        var artifact = Operation(MutationIsolationHazardArtifact(hazard));
        var accepted = Live(SafeMigrationObservedState.Missing);
        var decision = SafeMigrationDecisionPlanner.Plan(
            artifact.Intent.Kind, accepted.ObservedState, artifact.Policy, accepted.RepairCapability);

        projection.Observe(artifact, accepted, accepted, decision);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>A literal default requires no SQL evaluation and preserves unrelated table proofs.</summary>
    [Fact]
    public void LiteralDefaultSeedTablePreservesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();
        var table = new ExpectedTableDefinition("roles",
        [
            .. RoleTable().Columns,
            new ExpectedColumnDefinition("rank", typeof(int), isNullable: false, storeType: "int",
                defaultValue: SafeMigrationDefaultValue.Literal(0)),
        ]);

        Accept(projection,
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A renamed fresh seed table retains the proof belonging to its physical lifetime.</summary>
    [Fact]
    public void RenamedNewSeedTablePreservesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        Accept(projection,
            Operation(new RenameTableIntent("roles", newName: "application_roles")),
            SafeMigrationObservedState.Matching);
        ObserveAppliedMutation(projection,
            RoleEnsure("application_roles"), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A provider rename moves the populated table rather than restoring an empty-table proof.</summary>
    [Fact]
    public void ProviderRenameRetainsPopulatedSeedTableForeignKeyUncertainty()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        ObserveAppliedMutation(projection,
            MutationIsolationReferenceSeed(), SafeMigrationObservedState.PrerequisiteMissing);
        projection.ObserveProviderPostcondition(new RenameTableOperation
        {
            Name = "first",
            NewName = "renamed_first",
        });

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("renamed_first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Drop and recreation start a new empty lifetime regardless of either operation's origin.</summary>
    /// <param name="providerDrop">Whether the drop is an ordinary provider operation.</param>
    /// <param name="providerCreate">Whether recreation is an ordinary provider operation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecreatedPopulatedSeedTableRestoresForeignKeyEmptyRowProof(bool providerDrop, bool providerCreate)
    {
        // Arrange
        var projection = MutationIsolationProjection();

        ObserveAppliedMutation(projection,
            MutationIsolationReferenceSeed(), SafeMigrationObservedState.PrerequisiteMissing);

        if (providerDrop)
        {
            projection.ObserveProviderPostcondition(new DropTableOperation { Name = "first" });
        }
        else
        {
            Accept(projection, Operation(new DropTableIntent("first")), SafeMigrationObservedState.Matching);
        }

        var recreated = new CreateTableOperation { Name = "first" };

        recreated.Columns.Add(new AddColumnOperation
        {
            Table = "first", Name = "id", ClrType = typeof(int), ColumnType = "int", IsNullable = false,
        });
        recreated.Columns.Add(new AddColumnOperation
        {
            Table = "first", Name = "reference_id", ClrType = typeof(int), ColumnType = "int", IsNullable = false,
        });

        if (providerCreate)
        {
            projection.ObserveProviderPostcondition(recreated);
        }
        else
        {
            Accept(projection,
                Operation(new EnsureTableIntent(SafeMigrationExpectedDefinitionFactory.From(recreated),
                    SafeMigrationTableMode.StrictDefinition)),
                SafeMigrationObservedState.Missing);
        }

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A seed insert cannot recover schema or empty-row proofs invalidated by opaque SQL.</summary>
    [Fact]
    public void OpaqueSqlAfterTableCreationPreventsSeedAndForeignKeyProjection()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT side_effect();" });

        // Act
        var seed = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.PrerequisiteMissing));
        var foreignKey = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, seed.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, foreignKey.ObservedState);
        Assert.Equal("projected_structure_state_unknown", seed.Code);
        Assert.Equal("projected_structure_state_unknown", foreignKey.Code);
    }

    /// <summary>An opaque provider table change revokes insert confinement for its original fresh table.</summary>
    [Fact]
    public void OpaqueSeedTableChangeInvalidatesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "roles" });
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>A matching name in another schema cannot lend its new-table insert certificate.</summary>
    [Fact]
    public void DifferentSchemaSeedTargetDoesNotReuseNewTableInsertCertificate()
    {
        // Arrange
        var projection = MutationIsolationProjection();
        var definition = new ExpectedTableDefinition("roles", RoleTable().Columns, schema: "new_schema");
        var ensure = Operation(new EnsureModelManagedDataIntent(
            "roles", ["id"], ["int"], ["id", "name"], ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } }, schema: "existing_schema", uniqueKeys: null));

        Accept(projection,
            Operation(new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, ensure, SafeMigrationObservedState.Missing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>A recreated plain table cannot inherit expression hazards from its removed predecessor.</summary>
    [Fact]
    public void RecreatedPlainSeedTablePreservesUnrelatedForeignKeyRowProof()
    {
        // Arrange
        var projection = MutationIsolationProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(MutationIsolationHazardTable("sql-default"),
                SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        Accept(projection, Operation(new DropTableIntent("roles")), SafeMigrationObservedState.Missing);
        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(
            MutationIsolationForeignKey("first", "second"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A confined managed insert does not discard exact rows belonging to an unrelated table.</summary>
    [Fact]
    public void ConfinedSeedInsertPreservesOtherTablesExactManagedRows()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var otherTable = new ExpectedTableDefinition("other_roles", RoleTable().Columns);

        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);
        Accept(projection,
            Operation(new EnsureTableIntent(otherTable, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);
        ObserveAppliedMutation(projection, RoleEnsure("other_roles"), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, projected.ObservedState);
        Assert.Equal("projected_matching", projected.Code);
    }

    /// <summary>Creates two empty, keyed tables awaiting both halves of a cyclic foreign key.</summary>
    private static SafeMigrationPreflightProjection MutationIsolationProjection(
        SafeMigrationTableMode mode = SafeMigrationTableMode.StrictDefinition
    )
    {
        var projection = new SafeMigrationPreflightProjection();

        foreach (var table in new[] { "first", "second" })
        {
            var definition = new ExpectedTableDefinition(table,
            [
                new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                new ExpectedColumnDefinition("reference_id", typeof(int), isNullable: false, storeType: "int"),
            ], primaryKey: new ExpectedPrimaryKeyDefinition($"pk_{table}", table, ["id"]));

            Accept(projection, Operation(new EnsureTableIntent(definition, mode)), SafeMigrationObservedState.Missing);
        }

        return projection;
    }

    /// <summary>Creates one missing foreign key whose data safety requires the empty-table proof.</summary>
    private static SafeMigrationOperation MutationIsolationForeignKey(string dependent, string principal) => Operation(
        new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            $"fk_{dependent}_{principal}", dependent, ["reference_id"], principal, ["id"])));

    /// <summary>Populates the first cyclic-FK table with a reference that has no known matching principal.</summary>
    private static SafeMigrationOperation MutationIsolationReferenceSeed() => Operation(
        new EnsureModelManagedDataIntent(
            "first", ["id"], ["int"], ["id", "reference_id"], ["int", "int"],
            new object?[,] { { 1, 42 } }, schema: null, uniqueKeys: null));

    /// <summary>Observes a managed mutation and proves that the arranged write was actually accepted.</summary>
    private static void ObserveAppliedMutation(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationObservedState liveState
    )
    {
        var live = Live(liveState);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, analysis.ObservedState, operation.Policy, analysis.RepairCapability);

        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        projection.Observe(operation, live, analysis, decision);
    }

    /// <summary>Builds a seed table with a SQL expression that Core cannot certify as side-effect free.</summary>
    private static ExpectedTableDefinition MutationIsolationHazardTable(string hazard)
    {
        var expression = SafeMigrationSql.Function("side_effect");
        var extra = hazard switch
        {
            "sql-default" => new ExpectedColumnDefinition("extra", typeof(int), true, "int",
                defaultValue: SafeMigrationDefaultValue.Sql("side_effect()")),
            "structured-default" => new ExpectedColumnDefinition("extra", typeof(int), true, "int",
                defaultValue: SafeMigrationDefaultValue.Sql(expression)),
            "sql-computed" => new ExpectedColumnDefinition("extra", typeof(int), true, "int",
                computedColumnSql: "side_effect()"),
            "structured-computed" => new ExpectedColumnDefinition("extra", typeof(int), true, "int",
                computedExpression: expression),
            "sql-check" or "structured-check" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(hazard)),
        };

        ExpectedCheckConstraintDefinition[] checks = hazard switch
        {
            "sql-check" => [new ExpectedCheckConstraintDefinition("ck_roles", "roles", "side_effect() > 0")],
            "structured-check" => [ExpectedCheckConstraintDefinition.FromExpression("ck_roles", "roles", expression)],
            _ => [],
        };

        return new ExpectedTableDefinition("roles",
            extra is null ? RoleTable().Columns : [.. RoleTable().Columns, extra], checkConstraints: checks);
    }

    /// <summary>Builds a later artifact whose SQL evaluation invalidates insert confinement.</summary>
    private static SafeMigrationIntent MutationIsolationHazardArtifact(string hazard) => hazard switch
    {
        "column-default" => new EnsureColumnIntent("roles",
            new ExpectedColumnDefinition("extra", typeof(int), true, "int",
                defaultValue: SafeMigrationDefaultValue.Sql("side_effect()"))),
        "check" => new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("ck_roles", "roles", "side_effect() > 0")),
        "index-expression" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(expression: "side_effect(name)")])),
        "index-filter" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(column: "name")], filter: "side_effect(name) > 0")),
        "index-structured-expression" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Function("side_effect"))])),
        "index-structured-filter" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(column: "name")],
            structuredFilter: SafeMigrationSql.Function("side_effect"))),
        "index-method" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(column: "name")], method: "custom_method")),
        "index-operator-class" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix_roles", "roles",
            [new ExpectedIndexKeyDefinition(column: "name", operatorClass: "custom_ops")])),
        _ => throw new ArgumentOutOfRangeException(nameof(hazard)),
    };
}
