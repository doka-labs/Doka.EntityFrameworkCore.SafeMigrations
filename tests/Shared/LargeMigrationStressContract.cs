namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal enum LargeMigrationStressDialect
{
    MySql,
    PostgreSql,
}

internal static class LargeMigrationStressContract
{
    public const int OperationCount = 100_000;

    private const string ParentTable = "large_migration_parent";
    private const string SecondaryParentTable = "large_migration_secondary_parent";
    private const string TargetTable = "large_migration_target";

    public static LargeMigrationStressExpectation Populate(
        MigrationBuilder builder,
        LargeMigrationStressDialect dialect
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        var scenarios = CreateScenarios(dialect);

        // Missing resources use ordinal-specific names. Reusing one name would
        // let preflight projection turn later operations into synthetic no-ops
        // and would no longer exercise a large expected catalog.
        for (var ordinal = 0; ordinal < OperationCount; ordinal++)
        {
            scenarios[ordinal % scenarios.Count].AddOperation(builder, ordinal);
        }

        return new LargeMigrationStressExpectation(scenarios);
    }

    private static List<LargeMigrationStressScenario> CreateScenarios(
        LargeMigrationStressDialect dialect
    )
    {
        var integerStoreType = dialect == LargeMigrationStressDialect.MySql ? "int" : "integer";
        var textStoreType = dialect == LargeMigrationStressDialect.MySql
            ? "varchar(40)"
            : "character varying(40)";

        var parentDefinition = ParentDefinition(ParentTable, integerStoreType);
        var secondaryParentDefinition = ParentDefinition(SecondaryParentTable, integerStoreType);
        var targetDefinition = TargetDefinition(integerStoreType, textStoreType);
        var differentTableDefinition = TargetDefinition(
            integerStoreType,
            textStoreType,
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
                SafeMigrationAction.Repair),
            Scenario(
                (migrationBuilder, _) => migrationBuilder.EnsureColumn(
                    TargetTable,
                    blockedDefinition,
                    SafeMigrationPolicy.RepairIfSafe),
                _ => blockedDefinition.Name,
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationAction.RejectDataBlocked),
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
                    Index(MissingIndex(ordinal), ["indexed_value", "id"]),
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
                        ["id", "matching_value"]),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingUniqueConstraint,
                SafeMigrationOperationKind.EnsureUniqueConstraint,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
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
                            SafeMigrationSql.Literal(OperationCount))),
                    SafeMigrationPolicy.ThrowIfDifferent),
                MissingCheckConstraint,
                SafeMigrationOperationKind.EnsureCheckConstraint,
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply),
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
                SafeMigrationAction.Apply),
        };

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

    private static LargeMigrationStressScenario Scenario(
        Action<MigrationBuilder, int> addOperation,
        Func<int, string> objectName,
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationAction action,
        bool postconditionSatisfied = false
    ) => new(
        addOperation,
        objectName,
        operationKind,
        observedState,
        action,
        postconditionSatisfied);

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
}

internal sealed class LargeMigrationStressExpectation
{
    private readonly IReadOnlyList<LargeMigrationStressScenario> _scenarios;

    public LargeMigrationStressExpectation(
        IReadOnlyList<LargeMigrationStressScenario> scenarios
    )
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        _scenarios = scenarios;
    }

    public void AssertReport(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        Assert.Equal(SafeMigrationReportMode.Preflight, report.Mode);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(LargeMigrationStressContract.OperationCount, report.Assessments.Count);
        Assert.Empty(report.UnexpectedObjects);

        var stateCounts = new int[Enum.GetValues<SafeMigrationObservedState>().Length];
        var actionCounts = new int[Enum.GetValues<SafeMigrationAction>().Length];
        for (var ordinal = 0; ordinal < report.Assessments.Count; ordinal++)
        {
            var scenario = _scenarios[ordinal % _scenarios.Count];
            var assessment = report.Assessments[ordinal];

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

            if (assessment.ObservedState != scenario.ObservedState)
            {
                Assert.Equal(scenario.ObservedState, assessment.ObservedState);
            }

            if (assessment.Action != scenario.Action)
            {
                Assert.Equal(scenario.Action, assessment.Action);
            }

            if (assessment.PostconditionSatisfied != scenario.PostconditionSatisfied)
            {
                Assert.Equal(scenario.PostconditionSatisfied, assessment.PostconditionSatisfied);
            }

            stateCounts[(int)scenario.ObservedState]++;
            actionCounts[(int)scenario.Action]++;
        }

        Assert.DoesNotContain(0, stateCounts);
        Assert.DoesNotContain(0, actionCounts);
    }
}

internal sealed record LargeMigrationStressScenario(
    Action<MigrationBuilder, int> AddOperation,
    Func<int, string> ObjectName,
    SafeMigrationOperationKind OperationKind,
    SafeMigrationObservedState ObservedState,
    SafeMigrationAction Action,
    bool PostconditionSatisfied
);
