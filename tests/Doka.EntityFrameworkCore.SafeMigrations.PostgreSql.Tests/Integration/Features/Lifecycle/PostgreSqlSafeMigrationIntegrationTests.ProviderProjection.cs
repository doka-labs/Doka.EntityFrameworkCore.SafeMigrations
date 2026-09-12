namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ExistingTablesProjectAcceptedColumnsAndKeysIntoForeignKeyPreflight()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE projection_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE projection_children (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO projection_parents (id) VALUES (1); "
            + "INSERT INTO projection_children (id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = ExistingTableForeignKeyConvergence(context, defaultValue: null);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-foreign-key"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-foreign-key-postflight"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-foreign-key-replay"),
            CancellationToken.None);

        var foreignKey = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureForeignKey);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, foreignKey.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, foreignKey.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint "
                + "WHERE conname = 'fk_projection_children_parents' AND contype = 'f';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM projection_children WHERE id = 1 AND parent_id IS NULL;"));
    }

    [Fact]
    public async Task ForeignKeyPreflightRejectsUnprovenRowsFromNewDefaultedColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE projection_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE projection_children (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO projection_parents (id) VALUES (1); "
            + "INSERT INTO projection_children (id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = ExistingTableForeignKeyConvergence(context, defaultValue: 999);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("existing-table-foreign-key-default"),
                CancellationToken.None);

        var foreignKey = Assert.Single(
            report.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureForeignKey);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, foreignKey.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, foreignKey.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'projection_children' "
                + "AND column_name = 'parent_id';"));
    }

    [Fact]
    public async Task SemanticUniqueNoOpDoesNotSurvivePhysicalDropInForeignKeyProjection()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE alias_projection_parents ("
            + "id integer NOT NULL, code integer NOT NULL, "
            + "CONSTRAINT pk_alias_projection_parents PRIMARY KEY (id), "
            + "CONSTRAINT uq_alias_projection_parents_code UNIQUE (code));");
        await using var context = CreateContext(connectionString);
        var builder = SemanticUniqueDropProjection(context);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("semantic-unique-drop-projection"),
                CancellationToken.None);

        var alias = Assert.Single(
            report.Assessments,
            static assessment => StringComparer.Ordinal.Equals(
                assessment.ObjectName,
                "uq_alias_projection_parents_code_alias"));

        var drop = Assert.Single(
            report.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.DropUniqueConstraint);

        var foreignKey = Assert.Single(
            report.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureForeignKey);

        Assert.Equal(SafeMigrationAction.NoOp, alias.Action);
        Assert.Equal(SafeMigrationAction.Apply, drop.Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, foreignKey.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, foreignKey.Action);
    }

    [Fact]
    public async Task ProviderAnalysisResolvesPhysicalIdentitiesForSemanticAliases()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE identity_parents ("
            + "id integer NOT NULL, code integer NOT NULL, quantity integer NOT NULL, "
            + "CONSTRAINT pk_identity_parents PRIMARY KEY (id), "
            + "CONSTRAINT uq_identity_code UNIQUE (code), "
            + "CONSTRAINT ck_identity_quantity CHECK (quantity >= 0)); "
            + "CREATE INDEX ix_identity_quantity ON identity_parents (quantity); "
            + "CREATE TABLE identity_children ("
            + "id integer NOT NULL, parent_id integer NULL, "
            + "CONSTRAINT pk_identity_children PRIMARY KEY (id), "
            + "CONSTRAINT fk_identity_parent FOREIGN KEY (parent_id) "
            + "REFERENCES identity_parents (id) ON DELETE CASCADE);");
        await using var context = CreateContext(connectionString);
        var builder = SemanticAliasOperations(context, "identity");
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        var analyses = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, operations, CancellationToken.None);

        string[] physicalNames =
        [
            "uq_identity_code",
            "ck_identity_quantity",
            "fk_identity_parent",
            "ix_identity_quantity",
        ];

        Assert.Equal(physicalNames.Length, analyses.Count);
        for (var index = 0; index < analyses.Count; index++)
        {
            Assert.Equal(SafeMigrationObservedState.Matching, analyses[index].ObservedState);
            Assert.Equal(physicalNames[index], analyses[index].MatchedObjectName);
        }
    }

    [Fact]
    public async Task ProviderAnalysisDoesNotResolveAmbiguousSemanticAliases()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE ambiguous_parents ("
            + "id integer NOT NULL, code integer NOT NULL, quantity integer NOT NULL, "
            + "CONSTRAINT pk_ambiguous_parents PRIMARY KEY (id), "
            + "CONSTRAINT uq_ambiguous_code_a UNIQUE (code), "
            + "CONSTRAINT ck_ambiguous_quantity_a CHECK (quantity >= 0), "
            + "CONSTRAINT ck_ambiguous_quantity_b CHECK (quantity >= 0)); "
            + "ALTER TABLE ambiguous_parents "
            + "ADD CONSTRAINT uq_ambiguous_code_b UNIQUE (code); "
            + "CREATE INDEX ix_ambiguous_quantity_a ON ambiguous_parents (quantity); "
            + "CREATE INDEX ix_ambiguous_quantity_b ON ambiguous_parents (quantity); "
            + "CREATE TABLE ambiguous_children ("
            + "id integer NOT NULL, parent_id integer NULL, "
            + "CONSTRAINT pk_ambiguous_children PRIMARY KEY (id), "
            + "CONSTRAINT fk_ambiguous_parent_a FOREIGN KEY (parent_id) "
            + "REFERENCES ambiguous_parents (id) ON DELETE CASCADE, "
            + "CONSTRAINT fk_ambiguous_parent_b FOREIGN KEY (parent_id) "
            + "REFERENCES ambiguous_parents (id) ON DELETE CASCADE);");
        await using var context = CreateContext(connectionString);

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint "
                + "WHERE conrelid = 'ambiguous_parents'::regclass AND contype = 'u';"));

        var builder = SemanticAliasOperations(context, "ambiguous");
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        var analyses = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, operations, CancellationToken.None);

        Assert.Equal(4, analyses.Count);
        Assert.All(
            analyses,
            static analysis =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
                Assert.Null(analysis.MatchedObjectName);
            });
    }

    [Fact]
    public async Task EmptyExistingTableProjectsPrimaryUniqueAndCheckConstraintPrerequisites()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE projection_constraints (legacy_marker integer NULL);");
        await using var context = CreateContext(connectionString);
        var builder = EmptyExistingTableConstraintConvergence(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-constraints"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-constraints-postflight"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("existing-table-constraints-replay"),
            CancellationToken.None);

        var constraints = preflight.Assessments
            .Where(static assessment => assessment.OperationKind is
                SafeMigrationOperationKind.EnsurePrimaryKey or
                SafeMigrationOperationKind.EnsureUniqueConstraint or
                SafeMigrationOperationKind.EnsureCheckConstraint)
            .ToArray();

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(3, constraints.Length);
        Assert.All(
            constraints,
            static assessment => Assert.Equal(
                SafeMigrationObservedState.Missing,
                assessment.ObservedState));
        Assert.All(constraints, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropThenEnsureUsesProjectedStateAcrossObjectKinds(
        bool useSemanticAliases
    )
    {
        var suffix = useSemanticAliases ? "alias" : "exact";
        var parentTable = $"ordered_{suffix}_parents";
        var childTable = $"ordered_{suffix}_children";
        var primaryKey = $"pk_{parentTable}";
        var uniqueConstraint = $"uq_{parentTable}_code";
        var checkConstraint = $"ck_{parentTable}_quantity";
        var foreignKey = $"fk_{childTable}_parent";
        var index = $"ix_{parentTable}_quantity";
        var expectedPrimaryKey = TargetName(primaryKey, useSemanticAliases);
        var expectedUniqueConstraint = TargetName(uniqueConstraint, useSemanticAliases);
        var expectedCheckConstraint = TargetName(checkConstraint, useSemanticAliases);
        var expectedForeignKey = TargetName(foreignKey, useSemanticAliases);
        var expectedIndex = TargetName(index, useSemanticAliases);
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE {parentTable} ("
            + "id integer NOT NULL, code integer NOT NULL, quantity integer NOT NULL, "
            + $"CONSTRAINT {primaryKey} PRIMARY KEY (id), "
            + $"CONSTRAINT {uniqueConstraint} UNIQUE (code), "
            + $"CONSTRAINT {checkConstraint} CHECK (quantity >= 0)); "
            + $"CREATE INDEX {index} ON {parentTable} (quantity); "
            + $"CREATE TABLE {childTable} ("
            + "id integer NOT NULL PRIMARY KEY, parent_id integer NULL, "
            + $"CONSTRAINT {foreignKey} FOREIGN KEY (parent_id) "
            + $"REFERENCES {parentTable} (id) ON DELETE CASCADE);");
        await using var context = CreateContext(connectionString);
        var builder = DropThenEnsureOperations(
            context,
            parentTable,
            childTable,
            primaryKey,
            uniqueConstraint,
            checkConstraint,
            foreignKey,
            index,
            useSemanticAliases);

        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"ordered-drop-ensure-{suffix}"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        AssertProjectedReplacement(
            preflight,
            SafeMigrationOperationKind.EnsurePrimaryKey,
            expectedPrimaryKey,
            useSemanticAliases);
        AssertProjectedReplacement(
            preflight,
            SafeMigrationOperationKind.EnsureUniqueConstraint,
            expectedUniqueConstraint,
            useSemanticAliases);
        AssertProjectedReplacement(
            preflight,
            SafeMigrationOperationKind.EnsureCheckConstraint,
            expectedCheckConstraint,
            useSemanticAliases);
        AssertProjectedReplacement(
            preflight,
            SafeMigrationOperationKind.EnsureForeignKey,
            expectedForeignKey,
            useSemanticAliases);
        AssertProjectedReplacement(
            preflight,
            SafeMigrationOperationKind.EnsureIndex,
            expectedIndex,
            useSemanticAliases);
        Assert.Equal(
            5,
            preflight.Assessments.Count(
                static assessment => (assessment.OperationKind is
                        SafeMigrationOperationKind.DropPrimaryKey or
                        SafeMigrationOperationKind.DropUniqueConstraint or
                        SafeMigrationOperationKind.DropCheckConstraint or
                        SafeMigrationOperationKind.DropForeignKey or
                        SafeMigrationOperationKind.DropIndex)
                    && assessment.Action == SafeMigrationAction.Apply));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"ordered-drop-ensure-{suffix}-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    [Fact]
    public async Task DataMutationBetweenDropAndUniqueEnsureFailsClosedBeforeDdl()
    {
        const string table = "ordered_data_mutation";
        const string constraint = "uq_ordered_data_mutation_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE {table} ("
            + "id integer NOT NULL PRIMARY KEY, code integer NOT NULL, "
            + $"CONSTRAINT {constraint} UNIQUE (code));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropUniqueConstraintIfExists(constraint, table);
        builder.InsertData(
            table,
            ["id", "code"],
            new object[,] { { 1, 7 }, { 2, 7 }, });
        builder.AddUniqueConstraintIfNotExists(constraint, table, ["code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("ordered-drop-data-ensure"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        var replacement = Assert.Single(
            preflight.Assessments,
            assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureUniqueConstraint);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, replacement.Action);
        Assert.Equal("projected_data_state_unknown", replacement.AnalysisCode);
        Assert.Equal(0, await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM {table};"));
    }

    private static MigrationBuilder ExistingTableForeignKeyConvergence(
        DbContext context,
        int? defaultValue
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = builder.ConvergeTableFromModel(
            "projection_parents",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_projection_parents", value => value.id));

        _ = builder.ConvergeTableFromModel(
            "projection_children",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                parent_id = table.Column<int>(
                    type: "integer",
                    nullable: true,
                    defaultValue: defaultValue),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_projection_children", value => value.id);
                table.ForeignKey(
                    "fk_projection_children_parents",
                    value => value.parent_id,
                    "projection_parents",
                    "id",
                    onDelete: ReferentialAction.SetNull);
            });

        return builder;
    }

    private static void AssertProjectedReplacement(
        SafeMigrationRunReport report,
        SafeMigrationOperationKind kind,
        string objectName,
        bool hasInitialSemanticMatch
    )
    {
        var assessments = report.Assessments
            .Where(assessment => assessment.OperationKind == kind
                && StringComparer.Ordinal.Equals(assessment.ObjectName, objectName))
            .ToArray();

        Assert.Equal(hasInitialSemanticMatch ? 2 : 1, assessments.Length);
        if (hasInitialSemanticMatch)
        {
            Assert.Equal(SafeMigrationAction.NoOp, assessments[0].Action);
        }

        Assert.Equal(SafeMigrationObservedState.Missing, assessments[^1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessments[^1].Action);
    }

    private static MigrationBuilder DropThenEnsureOperations(
        DbContext context,
        string parentTable,
        string childTable,
        string primaryKey,
        string uniqueConstraint,
        string checkConstraint,
        string foreignKey,
        string index,
        bool useSemanticAliases
    )
    {
        var expectedPrimaryKey = TargetName(primaryKey, useSemanticAliases);
        var expectedUniqueConstraint = TargetName(uniqueConstraint, useSemanticAliases);
        var expectedCheckConstraint = TargetName(checkConstraint, useSemanticAliases);
        var expectedForeignKey = TargetName(foreignKey, useSemanticAliases);
        var expectedIndex = TargetName(index, useSemanticAliases);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = builder.ConvergeTableFromModel(
            parentTable,
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                code = table.Column<int>(type: "integer", nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
            });

        _ = builder.ConvergeTableFromModel(
            childTable,
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                parent_id = table.Column<int>(type: "integer", nullable: true),
            });

        if (useSemanticAliases)
        {
            builder.AddPrimaryKeyIfNotExists(expectedPrimaryKey, parentTable, ["id"]);
            builder.AddUniqueConstraintIfNotExists(expectedUniqueConstraint, parentTable, ["code"]);
            builder.EnsureCheckConstraint(
                ExpectedCheckConstraintDefinition.FromExpression(
                    expectedCheckConstraint,
                    parentTable,
                    SqlColumnAndInt(
                        "quantity",
                        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                        0)),
                SafeMigrationPolicy.ThrowIfDifferent);
            builder.CreateIndexIfNotExists(expectedIndex, parentTable, ["quantity"]);
            builder.AddForeignKeyIfNotExists(
                expectedForeignKey,
                childTable,
                ["parent_id"],
                parentTable,
                ["id"],
                onDelete: ReferentialAction.Cascade);
        }

        builder.DropForeignKeyIfExists(foreignKey, childTable);
        builder.DropIndexIfExists(index, parentTable);
        builder.DropCheckConstraintIfExists(checkConstraint, parentTable);
        builder.DropUniqueConstraintIfExists(uniqueConstraint, parentTable);
        builder.DropPrimaryKeyIfExists(primaryKey, parentTable);

        builder.AddPrimaryKeyIfNotExists(expectedPrimaryKey, parentTable, ["id"]);
        builder.AddUniqueConstraintIfNotExists(expectedUniqueConstraint, parentTable, ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                expectedCheckConstraint,
                parentTable,
                SqlColumnAndInt(
                    "quantity",
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                    0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists(expectedIndex, parentTable, ["quantity"]);
        builder.AddForeignKeyIfNotExists(
            expectedForeignKey,
            childTable,
            ["parent_id"],
            parentTable,
            ["id"],
            onDelete: ReferentialAction.Cascade);

        return builder;
    }

    private static string TargetName(
        string physicalName,
        bool useSemanticAlias
    ) => useSemanticAlias ? $"{physicalName}_alias" : physicalName;

    private static MigrationBuilder SemanticUniqueDropProjection(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = builder.ConvergeTableFromModel(
            "alias_projection_parents",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                code = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_alias_projection_parents", value => value.id));
        _ = builder.ConvergeTableFromModel(
            "alias_projection_children",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                parent_code = table.Column<int>(type: "integer", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_alias_projection_children", value => value.id));
        builder.AddUniqueConstraintIfNotExists(
            "uq_alias_projection_parents_code_alias",
            "alias_projection_parents",
            ["code"]);
        builder.DropUniqueConstraintIfExists(
            "uq_alias_projection_parents_code",
            "alias_projection_parents");
        builder.AddForeignKeyIfNotExists(
            "fk_alias_projection_children_parents",
            "alias_projection_children",
            ["parent_code"],
            "alias_projection_parents",
            ["code"]);

        return builder;
    }

    private static MigrationBuilder SemanticAliasOperations(
        DbContext context,
        string prefix
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.AddUniqueConstraintIfNotExists(
            $"uq_{prefix}_code_alias",
            $"{prefix}_parents",
            ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                $"ck_{prefix}_quantity_alias",
                $"{prefix}_parents",
                SqlColumnAndInt(
                    "quantity",
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                    0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            $"fk_{prefix}_parent_alias",
            $"{prefix}_children",
            ["parent_id"],
            $"{prefix}_parents",
            ["id"],
            onDelete: ReferentialAction.Cascade);
        _ = builder.CreateIndexIfNotExistsFromModel(
            $"ix_{prefix}_quantity_alias",
            $"{prefix}_parents",
            "quantity");

        return builder;
    }

    private static MigrationBuilder EmptyExistingTableConstraintConvergence(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = builder.ConvergeTableFromModel(
            "projection_constraints",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                code = table.Column<int>(type: "integer", nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_projection_constraints", value => value.id);
                table.UniqueConstraint("uq_projection_constraints_code", value => value.code);
                table.CheckConstraint("ck_projection_constraints_quantity", "quantity >= 0");
            });

        return builder;
    }
}
