namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal enum LargeMigrationStressDialect
{
    MySql,
    PostgreSql,
    SqlServer,
}

internal static class LargeMigrationStressContract
{
    public const int OperationCount = 100_000;

    private const string ParentTable = "large_migration_parent";
    private const string SecondaryParentTable = "large_migration_secondary_parent";
    private const string TargetTable = "large_migration_target";
    private const int MissingIndexColumnLag = 10;
    private const int MissingUniqueConstraintColumnLag = 14;

    /// <summary>Builds the same ordered mixed stream for a bounded regression or full qualification.</summary>
    /// <param name="builder">The migration operation sink.</param>
    /// <param name="dialect">The fixture's provider-specific contract.</param>
    /// <param name="operationCount">The bounded prefix length; full qualification retains 100,000 operations.</param>
    public static LargeMigrationStressExpectation Populate(
        MigrationBuilder builder,
        LargeMigrationStressDialect dialect,
        int operationCount = OperationCount
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationCount, OperationCount);

        var scenarios = CreateScenarios(dialect);

        // Missing resources use ordinal-specific definitions. A different name
        // alone cannot make a semantically identical index or constraint a new
        // object, so the stress catalog varies structural identity as well.
        for (var ordinal = 0; ordinal < operationCount; ordinal++)
        {
            scenarios[ordinal % scenarios.Count].AddOperation(builder, ordinal);
        }

        return new LargeMigrationStressExpectation(
            scenarios,
            allowUnexpectedObjects: dialect == LargeMigrationStressDialect.SqlServer,
            operationCount);
    }

    public static IEnumerable<int> ModelManagedUpdateOrdinals(
        LargeMigrationStressDialect dialect
    )
    {
        var scenarios = CreateScenarios(dialect);
        var scenarioIndex = scenarios.FindIndex(static scenario =>
            scenario.OperationKind == SafeMigrationOperationKind.UpdateModelManagedData
            && scenario.ObservedState == SafeMigrationObservedState.TransitionReady);

        if (scenarioIndex < 0)
        {
            throw new InvalidOperationException(
                "The large migration contract has no source-state model-managed update scenario.");
        }

        return Enumerable
            .Range(0, OperationCount)
            .Where(ordinal => ordinal % scenarios.Count == scenarioIndex);
    }

    private static List<LargeMigrationStressScenario> CreateScenarios(
        LargeMigrationStressDialect dialect
    )
    {
        if (dialect == LargeMigrationStressDialect.SqlServer)
        {
            return CreateSqlServerScenarios();
        }

        var integerStoreType = dialect == LargeMigrationStressDialect.MySql ? "int" : "integer";
        var textStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "varchar(40)"
            : "character varying(40)";

        var wideningStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "varchar(80)"
            : "character varying(80)";

        var narrowingStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "varchar(10)"
            : "character varying(10)";

        var blockedNarrowingStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "varchar(5)"
            : "character varying(5)";

        var booleanStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "bit(1)"
            : "boolean";

        var parentDefinition = ParentDefinition(ParentTable, integerStoreType);
        var secondaryParentDefinition = ParentDefinition(SecondaryParentTable, integerStoreType);
        var targetDefinition = TargetDefinition(integerStoreType, textStoreType, booleanStoreType);
        var differentTableDefinition = TargetDefinition(
            integerStoreType,
            textStoreType,
            booleanStoreType,
            comment: "expected stress comment");

        var repairDefinition = new ExpectedColumnDefinition(
            "repair_value",
            typeof(string),
            isNullable: false,
            textStoreType,
            maxLength: 40,
            defaultValue: SafeMigrationDefaultValue.Literal("canonical"));

        var blockedDefinition = new ExpectedColumnDefinition(
            "blocked_value",
            typeof(string),
            isNullable: false,
            textStoreType,
            maxLength: 40);

        var wideningDefinition = new ExpectedColumnDefinition(
            "widening_value",
            typeof(string),
            isNullable: true,
            wideningStoreType,
            maxLength: 80);

        var narrowingDefinition = new ExpectedColumnDefinition(
            "narrowing_value",
            typeof(string),
            isNullable: true,
            narrowingStoreType,
            maxLength: 10);

        var blockedNarrowingDefinition = new ExpectedColumnDefinition(
            "blocked_narrowing_value",
            typeof(string),
            isNullable: true,
            blockedNarrowingStoreType,
            maxLength: 5);

        var booleanDefinition = new ExpectedColumnDefinition(
            "boolean_value",
            typeof(bool),
            isNullable: true,
            storeType: "tinyint(1)");

        var matchingIndex = Index(
            "ix_large_migration_target_indexed",
            ["indexed_value", "matching_value"]);

        var parentIndex = Index(
            "ix_large_migration_target_parent",
            ["parent_id", "parent_tenant_id"]);

        var secondaryParentIndex = Index(
            "ix_large_migration_target_secondary_parent",
            ["secondary_parent_id", "secondary_parent_tenant_id"]);

        var differentIndex = new ExpectedIndexDefinition(
            matchingIndex.Name,
            TargetTable,
            matchingIndex.Keys,
            unique: true);

        var primaryKey = new ExpectedPrimaryKeyDefinition(
            "pk_large_migration_target",
            TargetTable,
            ["id"]);

        var uniqueConstraint = new ExpectedUniqueConstraintDefinition(
            "uq_large_migration_target_value",
            TargetTable,
            ["unique_value", "matching_value"]);

        var checkConstraint = ExpectedCheckConstraintDefinition.FromExpression(
            "ck_large_migration_target_value",
            TargetTable,
            SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("check_value"),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                SafeMigrationSql.Literal(0)));

        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_large_migration_target_parent",
            TargetTable,
            ["parent_id", "parent_tenant_id"],
            ParentTable,
            ["id", "tenant_id"]);

        var scenarios = new List<LargeMigrationStressScenario>
        {
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureTable(
                    targetDefinition,
                    SafeMigrationTableMode.ConvergenceContainer,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => TargetTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureTable(
                    parentDefinition,
                    SafeMigrationTableMode.ConvergenceContainer,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => ParentTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureTable(
                    secondaryParentDefinition,
                    SafeMigrationTableMode.ConvergenceContainer,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => SecondaryParentTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureTable(
                    new ExpectedTableDefinition(
                        MissingTable(ordinal),
                        [new ExpectedColumnDefinition("id", typeof(int), false, integerStoreType)]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureTable(
                    differentTableDefinition,
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ExistenceOnly),
                _ => TargetTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.NoOp),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureTable(
                    differentTableDefinition,
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => TargetTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.RejectDifferent),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    new ExpectedColumnDefinition("id", typeof(int), false, integerStoreType),
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => "id",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    new ExpectedColumnDefinition(MissingColumn(ordinal), typeof(int), true, integerStoreType),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingColumn,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    repairDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => repairDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.Repair,
                requiresLiveDataProof: true,
                convergesOnFirstAcceptedMutation: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    blockedDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => blockedDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationAction.RejectDataBlocked,
                requiresLiveDataProof: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    "large_migration_absent_parent",
                    new ExpectedColumnDefinition("value", typeof(int), false, integerStoreType),
                    SafeMigrationPolicy.RepairIfSafe),
                _ => "value",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationAction.RejectPrerequisiteMissing),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    wideningDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => wideningDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.Repair,
                operationalImpact: SafeMigrationOperationalImpact.TableRewritePossible,
                convergesOnFirstAcceptedMutation: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    narrowingDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => narrowingDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.Repair,
                operationalImpact: SafeMigrationOperationalImpact.TableRewritePossible,
                requiresLiveDataProof: true,
                convergesOnFirstAcceptedMutation: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    blockedNarrowingDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => blockedNarrowingDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationAction.RejectDataBlocked,
                operationalImpact: SafeMigrationOperationalImpact.TableRewritePossible,
                requiresLiveDataProof: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureIndex(
                    matchingIndex,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => matchingIndex.Name,
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureIndex(
                    parentIndex,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => parentIndex.Name,
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureIndex(
                    secondaryParentIndex,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => secondaryParentIndex.Name,
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureIndex(
                    Index(
                        MissingIndex(ordinal),
                        [MissingColumn(ordinal - MissingIndexColumnLag), "id"]),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingIndex,
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureIndex(
                    differentIndex,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => differentIndex.Name,
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.RejectDifferent),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsurePrimaryKey(
                    primaryKey,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => primaryKey.Name,
                SafeMigrationOperationKind.EnsurePrimaryKey,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureUniqueConstraint(
                    uniqueConstraint,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => uniqueConstraint.Name,
                SafeMigrationOperationKind.EnsureUniqueConstraint,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureUniqueConstraint(
                    new ExpectedUniqueConstraintDefinition(
                        MissingUniqueConstraint(ordinal),
                        TargetTable,
                        [MissingColumn(ordinal - MissingUniqueConstraintColumnLag), "id"]),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingUniqueConstraint,
                SafeMigrationOperationKind.EnsureUniqueConstraint,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply,
                requiresLiveDataProof: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureCheckConstraint(
                    checkConstraint,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => checkConstraint.Name,
                SafeMigrationOperationKind.EnsureCheckConstraint,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureCheckConstraint(
                    ExpectedCheckConstraintDefinition.FromExpression(
                        MissingCheckConstraint(ordinal),
                        TargetTable,
                        SafeMigrationSql.Binary(
                            SafeMigrationSql.Identifier("check_value"),
                            SafeMigrationSqlBinaryOperator.LessThanOrEqual,
                            SafeMigrationSql.Literal(checked(OperationCount + ordinal)))),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingCheckConstraint,
                SafeMigrationOperationKind.EnsureCheckConstraint,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply,
                requiresLiveDataProof: true),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureForeignKey(
                    foreignKey,
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => foreignKey.Name,
                SafeMigrationOperationKind.EnsureForeignKey,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureForeignKey(
                    new ExpectedForeignKeyDefinition(
                        MissingForeignKey(ordinal),
                        TargetTable,
                        ["secondary_parent_id", "secondary_parent_tenant_id"],
                        SecondaryParentTable,
                        ["id", "tenant_id"]),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingForeignKey,
                SafeMigrationOperationKind.EnsureForeignKey,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply,
                requiresLiveDataProof: true,
                convergesOnFirstAcceptedMutation: true),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.EnsureModelManagedDataFromModel(
                    TargetTable,
                    ["id"],
                    [integerStoreType],
                    [
                        "id",
                        "matching_value",
                        "repair_value",
                        "blocked_value",
                        "indexed_value",
                        "unique_value",
                        "check_value",
                        "parent_id",
                        "parent_tenant_id",
                        "secondary_parent_id",
                        "secondary_parent_tenant_id",
                    ],
                    [
                        integerStoreType,
                        integerStoreType,
                        textStoreType,
                        textStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                    ],
                    new object?[,]
                    {
                        {
                            ModelManagedEnsureKey(ordinal),
                            ordinal,
                            "canonical",
                            null,
                            ordinal,
                            ModelManagedEnsureKey(ordinal),
                            ordinal,
                            1,
                            1,
                            1,
                            1,
                        },
                    }),
                _ => TargetTable,
                SafeMigrationOperationKind.EnsureModelManagedData,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.UpdateModelManagedDataFromModel(
                    TargetTable,
                    ["id"],
                    [integerStoreType],
                    new object?[,] { { ModelManagedUpdateKey(ordinal) } },
                    ["matching_value"],
                    [integerStoreType],
                    new object?[,] { { ordinal } },
                    new object?[,] { { checked(ordinal + 1) } }),
                _ => TargetTable,
                SafeMigrationOperationKind.UpdateModelManagedData,
                SafeMigrationObservedState.TransitionReady,
                SafeMigrationAction.Apply),
            Scenario(
                (migrationBuilder, ordinal) => migrationBuilder.DeleteModelManagedDataFromModel(
                    TargetTable,
                    ["id"],
                    [integerStoreType],
                    new object?[,] { { ModelManagedDeleteKey(ordinal) } },
                    [
                        "id",
                        "matching_value",
                        "repair_value",
                        "blocked_value",
                        "indexed_value",
                        "unique_value",
                        "check_value",
                        "parent_id",
                        "parent_tenant_id",
                        "secondary_parent_id",
                        "secondary_parent_tenant_id",
                    ],
                    [
                        integerStoreType,
                        integerStoreType,
                        textStoreType,
                        textStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                        integerStoreType,
                    ],
                    new object?[,]
                    {
                        {
                            ModelManagedDeleteKey(ordinal),
                            ordinal,
                            "canonical",
                            null,
                            ordinal,
                            ModelManagedDeleteKey(ordinal),
                            ordinal,
                            1,
                            1,
                            1,
                            1,
                        },
                    }),
                _ => TargetTable,
                SafeMigrationOperationKind.DeleteModelManagedData,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
        };

        if (dialect == LargeMigrationStressDialect.MySql)
        {
            scenarios.Add(Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    booleanDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => booleanDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.Repair,
                operationalImpact: SafeMigrationOperationalImpact.TableRewritePossible,
                convergesOnFirstAcceptedMutation: true));
        }

        scenarios.Add(UnsupportedScenario(dialect));

        return scenarios;
    }

    private static ExpectedTableDefinition ParentDefinition(
        string table,
        string integerStoreType
    ) => new(
        table,
        [
            new ExpectedColumnDefinition("id", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("tenant_id", typeof(int), false, integerStoreType),
        ],
        primaryKey: new ExpectedPrimaryKeyDefinition($"pk_{table}", table, ["id", "tenant_id"]));

    private static ExpectedTableDefinition TargetDefinition(
        string integerStoreType,
        string textStoreType,
        string booleanStoreType,
        string? comment = null
    ) => new(
        TargetTable,
        [
            new ExpectedColumnDefinition("id", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("matching_value", typeof(int), true, integerStoreType),
            new ExpectedColumnDefinition(
                "repair_value",
                typeof(string),
                true,
                textStoreType,
                maxLength: 40,
                defaultValue: SafeMigrationDefaultValue.Literal("legacy")),
            new ExpectedColumnDefinition("blocked_value", typeof(string), true, textStoreType, maxLength: 40),
            new ExpectedColumnDefinition("widening_value", typeof(string), true, textStoreType, maxLength: 40),
            new ExpectedColumnDefinition("narrowing_value", typeof(string), true, textStoreType, maxLength: 40),
            new ExpectedColumnDefinition(
                "blocked_narrowing_value",
                typeof(string),
                true,
                textStoreType,
                maxLength: 40),
            new ExpectedColumnDefinition("boolean_value", typeof(bool), true, booleanStoreType),
            new ExpectedColumnDefinition("indexed_value", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("unique_value", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("check_value", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("parent_id", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("parent_tenant_id", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("secondary_parent_id", typeof(int), false, integerStoreType),
            new ExpectedColumnDefinition("secondary_parent_tenant_id", typeof(int), false, integerStoreType),
        ],
        comment: comment,
        primaryKey: new ExpectedPrimaryKeyDefinition(
            "pk_large_migration_target",
            TargetTable,
            ["id"]),
        uniqueConstraints:
        [
            new ExpectedUniqueConstraintDefinition(
                "uq_large_migration_target_value",
                TargetTable,
                ["unique_value", "matching_value"]),
        ],
        checkConstraints:
        [
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_large_migration_target_value",
                TargetTable,
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("check_value"),
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                    SafeMigrationSql.Literal(0))),
        ],
        foreignKeys:
        [
            new ExpectedForeignKeyDefinition(
                "fk_large_migration_target_parent",
                TargetTable,
                ["parent_id", "parent_tenant_id"],
                ParentTable,
                ["id", "tenant_id"]),
        ]);

    private static ExpectedIndexDefinition Index(
        string name,
        IEnumerable<string> columns
    ) => new(
        name,
        TargetTable,
        columns.Select(static column => new ExpectedIndexKeyDefinition(column)));

    private static LargeMigrationStressScenario UnsupportedScenario(
        LargeMigrationStressDialect dialect
    ) => dialect switch
    {
        LargeMigrationStressDialect.MySql => Scenario(
            (builder, ordinal) => builder.EnsureSchemaExists(UnsupportedSchema(ordinal)),
            UnsupportedSchema,
            SafeMigrationOperationKind.EnsureSchema,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationAction.RejectUnsupported),
        LargeMigrationStressDialect.PostgreSql => Scenario(
            (builder, ordinal) => builder.EnsureIndex(
                new ExpectedIndexDefinition(
                    UnsupportedIndex(ordinal),
                    TargetTable,
                    [new ExpectedIndexKeyDefinition(column: "indexed_value", prefixLength: 4)]),
                SafeMigrationPolicy.ThrowIfDifferent),
            UnsupportedIndex,
            SafeMigrationOperationKind.EnsureIndex,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationAction.RejectUnsupported),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    private static List<LargeMigrationStressScenario> CreateSqlServerScenarios()
    {
        // WHY: SQL Server must prove CHECK/default contracts with its own
        // extended-property stamps. The existing MySQL/PostgreSQL fixture cannot
        // represent those physical identities, so stress only independently
        // provable SQL Server states while preserving all action families.
        return
        [
            Scenario(
                (builder, _) => builder.EnsureColumn(
                    "sqlserver_stress_target",
                    new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => "id",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
            Scenario(
                (builder, ordinal) => builder.EnsureTable(
                    new ExpectedTableDefinition(
                        MissingTable(ordinal),
                        [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingTable,
                SafeMigrationOperationKind.EnsureTable,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
            Scenario(
                (builder, _) => builder.EnsureColumn(
                    "sqlserver_stress_target",
                    new ExpectedColumnDefinition("id", typeof(long), false, "bigint"),
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => "id",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.RejectDifferent),
            Scenario(
                (builder, _) => builder.EnsureIndex(
                    new ExpectedIndexDefinition(
                        "ix_sqlserver_stress_unsupported",
                        "sqlserver_stress_target",
                        [new ExpectedIndexKeyDefinition("id")],
                        method: "hash"),
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => "ix_sqlserver_stress_unsupported",
                SafeMigrationOperationKind.EnsureIndex,
                SafeMigrationObservedState.Unsupported,
                SafeMigrationAction.RejectUnsupported),
            Scenario(
                (builder, _) => builder.EnsureColumn(
                    "sqlserver_stress_target",
                    new ExpectedColumnDefinition("required_value", typeof(int), false, "int"),
                    SafeMigrationPolicy.RepairIfSafe),
                _ => "required_value",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationAction.RejectDataBlocked),
            Scenario(
                (builder, _) => builder.EnsureColumn(
                    "sqlserver_stress_missing_parent",
                    new ExpectedColumnDefinition("value", typeof(int), true, "int"),
                    SafeMigrationPolicy.ThrowIfDifferent),
                _ => "value",
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationAction.RejectPrerequisiteMissing),
            Scenario(
                (builder, _) => builder.AlterColumnIfDifferent(
                    "sqlserver_stress_alter",
                    new ExpectedColumnDefinition("caption", typeof(string), false, "varchar(20)", maxLength: 20),
                    new ExpectedColumnDefinition("caption", typeof(string), false, "varchar(10)", maxLength: 10),
                    SafeMigrationPolicy.RepairIfSafe),
                _ => "caption",
                SafeMigrationOperationKind.AlterColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationAction.Repair,
                convergesOnFirstAcceptedMutation: true),
            Scenario(
                (builder, ordinal) => builder.UpdateModelManagedDataFromModel(
                    "sqlserver_stress_managed",
                    ["id"],
                    ["int"],
                    new object?[,] { { ModelManagedUpdateKey(ordinal) } },
                    ["managed_value"],
                    ["nvarchar(32)"],
                    new object?[,] { { "source" } },
                    new object?[,] { { "target" } }),
                _ => "sqlserver_stress_managed",
                SafeMigrationOperationKind.UpdateModelManagedData,
                SafeMigrationObservedState.TransitionReady,
                SafeMigrationAction.Apply),
            Scenario(
                (builder, _) => builder.DropTableIfExists("sqlserver_stress_absent"),
                _ => "sqlserver_stress_absent",
                SafeMigrationOperationKind.DropTable,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true),
        ];
    }

    private static LargeMigrationStressScenario Scenario(
        Action<MigrationBuilder, int> addOperation,
        Func<int, string> objectName,
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationAction action,
        bool postconditionSatisfied = false,
        SafeMigrationOperationalImpact? operationalImpact = null,
        bool requiresLiveDataProof = false,
        bool convergesOnFirstAcceptedMutation = false
    ) => new(
        addOperation,
        objectName,
        operationKind,
        observedState,
        action,
        postconditionSatisfied,
        operationalImpact,
        requiresLiveDataProof,
        convergesOnFirstAcceptedMutation);

    private static string MissingTable(
        int ordinal
    ) => $"sm_stress_missing_table_{ordinal:D6}";

    private static string MissingColumn(
        int ordinal
    ) => $"missing_column_{ordinal:D6}";

    private static string MissingIndex(
        int ordinal
    ) => $"ix_stress_missing_{ordinal:D6}";

    private static string MissingUniqueConstraint(
        int ordinal
    ) => $"uq_stress_missing_{ordinal:D6}";

    private static string MissingCheckConstraint(
        int ordinal
    ) => $"ck_stress_missing_{ordinal:D6}";

    private static string MissingForeignKey(
        int ordinal
    ) => $"fk_stress_missing_{ordinal:D6}";

    private static string UnsupportedSchema(
        int ordinal
    ) => $"stress_schema_{ordinal:D6}";

    private static string UnsupportedIndex(
        int ordinal
    ) => $"ix_stress_unsupported_{ordinal:D6}";

    private static int ModelManagedEnsureKey(
        int ordinal
    ) => checked(1_000_000 + ordinal);

    public static int ModelManagedUpdateKey(
        int ordinal
    ) => checked(2_000_000 + ordinal);

    private static int ModelManagedDeleteKey(
        int ordinal
    ) => checked(3_000_000 + ordinal);
}

internal sealed class LargeMigrationStressExpectation
{
    private readonly IReadOnlyList<LargeMigrationStressScenario> _scenarios;
    private readonly bool _allowUnexpectedObjects;
    private readonly int _operationCount;

    /// <summary>Captures the source scenarios and exact size of the ordered report to verify.</summary>
    /// <param name="scenarios">The repeating immutable source-state contracts.</param>
    /// <param name="allowUnexpectedObjects">Whether the SQL Server fixture permits stamped catalog artifacts.</param>
    /// <param name="operationCount">The number of operations populated for this run.</param>
    public LargeMigrationStressExpectation(
        IReadOnlyList<LargeMigrationStressScenario> scenarios,
        bool allowUnexpectedObjects = false,
        int operationCount = LargeMigrationStressContract.OperationCount
    )
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationCount, LargeMigrationStressContract.OperationCount);

        _scenarios = scenarios;
        _allowUnexpectedObjects = allowUnexpectedObjects;
        _operationCount = operationCount;
    }

    public void AssertReport(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        Assert.Equal(SafeMigrationReportMode.Preflight, report.Mode);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(_operationCount, report.Assessments.Count);
        if (!_allowUnexpectedObjects)
        {
            Assert.Empty(report.UnexpectedObjects);
        }


        var sourceStateCounts = new int[Enum.GetValues<SafeMigrationObservedState>().Length];
        var actionCounts = new int[Enum.GetValues<SafeMigrationAction>().Length];
        var convergedScenarios = new bool[_scenarios.Count];
        var projectedDataMutationSeen = false;
        var managedMutationOrigin = -1;

        for (var ordinal = 0; ordinal < report.Assessments.Count; ordinal++)
        {
            var scenarioIndex = ordinal % _scenarios.Count;
            var scenario = _scenarios[scenarioIndex];
            var assessment = report.Assessments[ordinal];
            var alreadyConverged = convergedScenarios[scenarioIndex];
            var mutatesModelManagedData = scenario.OperationKind is
                SafeMigrationOperationKind.EnsureModelManagedData
                or SafeMigrationOperationKind.UpdateModelManagedData
                or SafeMigrationOperationKind.DeleteModelManagedData;

            // WHY: These managed writes target existing tables. A prior write
            // can fire triggers and invalidate even a live Missing/NoOp result.
            // Deferred writes also advance the origin without inventing row facts.
            var deferred = mutatesModelManagedData && projectedDataMutationSeen;
            var proofInvalidated = !alreadyConverged
                && scenario.RequiresLiveDataProof
                && projectedDataMutationSeen;

            SafeMigrationObservedState? expectedState = deferred
                ? null
                : alreadyConverged
                    ? SafeMigrationObservedState.Matching
                    : proofInvalidated
                        ? SafeMigrationObservedState.PrerequisiteMissing
                        : scenario.ObservedState;

            var expectedAction = deferred
                ? SafeMigrationAction.ValidateAtRuntime
                : alreadyConverged
                    ? SafeMigrationAction.NoOp
                    : proofInvalidated
                        ? SafeMigrationAction.RejectPrerequisiteMissing
                        : scenario.Action;

            bool? expectedPostcondition = deferred
                ? null
                : alreadyConverged || (!proofInvalidated && scenario.PostconditionSatisfied);

            var expectedOperationalImpact = deferred
                ? SafeMigrationOperationalImpact.Unknown
                : alreadyConverged || proofInvalidated
                    ? SafeMigrationOperationalImpact.NotApplicable
                    : scenario.OperationalImpact;

            Assert.True(assessment.IsSafeOperation);
            Assert.Equal(typeof(SafeMigrationOperation).FullName, assessment.OperationType);

            if (assessment.Ordinal != ordinal)
            {
                Assert.Equal(ordinal, assessment.Ordinal);
            }

            if (assessment.OperationKind != scenario.OperationKind)
            {
                Assert.Equal(scenario.OperationKind, assessment.OperationKind);
            }

            var expectedObjectName = scenario.ObjectName(ordinal);
            if (!StringComparer.Ordinal.Equals(expectedObjectName, assessment.ObjectName))
            {
                Assert.Equal(expectedObjectName, assessment.ObjectName);
            }

            if (assessment.ObservedState != expectedState)
            {
                Assert.Fail(
                    $"Operation {ordinal} ({scenario.OperationKind} {expectedObjectName}) "
                    + $"expected state {expectedState?.ToString() ?? "deferred"}, "
                    + $"actual {assessment.ObservedState?.ToString() ?? "deferred"}.");
            }

            if (assessment.Action != expectedAction)
            {
                Assert.Equal(expectedAction, assessment.Action);
            }

            if (assessment.PostconditionSatisfied != expectedPostcondition)
            {
                Assert.Equal(expectedPostcondition, assessment.PostconditionSatisfied);
            }

            if (expectedOperationalImpact is not null
                && assessment.OperationalImpact != expectedOperationalImpact)
            {
                Assert.Equal(expectedOperationalImpact, assessment.OperationalImpact);
            }

            if (deferred)
            {
                Assert.Equal("runtime_validation_required", assessment.Code);
                Assert.Equal("runtime_validation_required", assessment.DecisionCode);
                Assert.Equal("projected_model_managed_data_state_unknown", assessment.AnalysisCode);
                Assert.Empty(assessment.Differences);
                var origin = assessment.DeferredOrigin;

                Assert.NotNull(origin);
                Assert.Equal(managedMutationOrigin, origin.OperationOrdinal);
                Assert.Equal(typeof(SafeMigrationOperation).FullName, origin.OperationType);
                Assert.Null(origin.MigrationId);
            }
            else
            {
                Assert.Null(assessment.DeferredOrigin);
            }

            // WHY: TransitionReady is a source-state fixture, but all such
            // updates in MySQL/PostgreSQL follow a seed insert and must defer.
            // Exact per-operation checks above retain coverage without counting
            // null as an observed enum or claiming a stale state was certified.
            sourceStateCounts[(int)scenario.ObservedState]++;
            actionCounts[(int)expectedAction]++;

            if (scenario.ConvergesOnFirstAcceptedMutation
                && (expectedAction is SafeMigrationAction.Apply or SafeMigrationAction.Repair))
            {
                convergedScenarios[scenarioIndex] = true;
            }

            if (mutatesModelManagedData
                && (expectedAction is SafeMigrationAction.Apply or SafeMigrationAction.ValidateAtRuntime))
            {
                projectedDataMutationSeen = true;
                managedMutationOrigin = ordinal;
            }
        }

        Assert.DoesNotContain(0, sourceStateCounts);

        foreach (var action in Enum.GetValues<SafeMigrationAction>())
        {
            Assert.True(actionCounts[(int)action] > 0, $"Action {action} was not exercised.");
        }
    }
}

internal sealed record LargeMigrationStressScenario(
    Action<MigrationBuilder, int> AddOperation,
    Func<int, string> ObjectName,
    SafeMigrationOperationKind OperationKind,
    SafeMigrationObservedState ObservedState,
    SafeMigrationAction Action,
    bool PostconditionSatisfied,
    SafeMigrationOperationalImpact? OperationalImpact,
    bool RequiresLiveDataProof,
    bool ConvergesOnFirstAcceptedMutation
);
