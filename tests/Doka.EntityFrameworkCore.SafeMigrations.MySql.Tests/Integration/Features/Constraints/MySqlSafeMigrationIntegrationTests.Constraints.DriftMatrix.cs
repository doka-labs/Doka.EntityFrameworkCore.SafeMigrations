namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ObservableConstraintFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `constraint_matrix_parents` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "PRIMARY KEY (`id`, `alternate_id`)); "
            + "CREATE TABLE `constraint_matrix_other_parents` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "PRIMARY KEY (`id`, `alternate_id`)); "
            + "CREATE TABLE `constraint_matrix_children` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "`code` varchar(30) NULL, `alternate_code` varchar(30) NULL, "
            + "`quantity` int NOT NULL, `parent_id` int NULL, `alternate_parent_id` int NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new MigrationBuilder(context.Database.ProviderName!);
        canonical.AddPrimaryKeyIfNotExists(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["id", "alternate_id"]);
        canonical.AddUniqueConstraintIfNotExists(
            "uq_constraint_matrix_code",
            "constraint_matrix_children",
            ["code", "alternate_code"]);
        canonical.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        canonical.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);

        await ExecuteOperationsAsync(context, canonical.Operations);
        await ExecuteOperationsAsync(context, canonical.Operations);

        var canonicalReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, canonical.Operations, new SafeMigrationRunOptions("constraint-matrix-canonical"));

        Assert.Equal(SafeMigrationReportStatus.Ready, canonicalReport.Status);
        Assert.All(
            canonicalReport.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        var strictDefinition = CreateStrictConstraintMatrixTable(ReferentialAction.SetNull);
        var strict = new MigrationBuilder(context.Database.ProviderName!);
        strict.EnsureTable(
            strictDefinition,
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var strictReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, strict.Operations, new SafeMigrationRunOptions("constraint-matrix-strict"));

        Assert.Equal(SafeMigrationReportStatus.Ready, strictReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(strictReport.Assessments)
                .ObservedState);

        var strictDrift = new MigrationBuilder(context.Database.ProviderName!);
        strictDrift.EnsureTable(
            CreateStrictConstraintMatrixTable(ReferentialAction.Cascade),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var strictDriftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                strictDrift.Operations,
                new SafeMigrationRunOptions("constraint-matrix-strict-drift"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, strictDriftReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(strictDriftReport.Assessments)
                .ObservedState);

        var variants = new List<IReadOnlyList<MigrationOperation>>();

        var primaryKeyOrder = new MigrationBuilder(context.Database.ProviderName!);
        primaryKeyOrder.AddPrimaryKeyIfNotExists(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["alternate_id", "id"]);
        variants.Add(primaryKeyOrder.Operations);

        var uniqueColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        uniqueColumnOrder.AddUniqueConstraintIfNotExists(
            "uq_constraint_matrix_code",
            "constraint_matrix_children",
            ["alternate_code", "code"]);
        variants.Add(uniqueColumnOrder.Operations);

        var checkExpression = new MigrationBuilder(context.Database.ProviderName!);
        checkExpression.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThan, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        variants.Add(checkExpression.Operations);

        var dependentColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        dependentColumnOrder.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["alternate_parent_id", "parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(dependentColumnOrder.Operations);

        var principalTable = new MigrationBuilder(context.Database.ProviderName!);
        principalTable.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_other_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(principalTable.Operations);

        var principalColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        principalColumnOrder.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["alternate_id", "id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(principalColumnOrder.Operations);

        var updateAction = new MigrationBuilder(context.Database.ProviderName!);
        updateAction.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.NoAction,
            onDelete: ReferentialAction.SetNull);
        variants.Add(updateAction.Operations);

        var deleteAction = new MigrationBuilder(context.Database.ProviderName!);
        deleteAction.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.Cascade);
        variants.Add(deleteAction.Operations);

        foreach (var operations in variants)
        {
            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, operations, new SafeMigrationRunOptions("constraint-matrix-drift"));

            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }

    private static ExpectedTableDefinition CreateStrictConstraintMatrixTable(
        ReferentialAction onDelete
    ) => new(
        "constraint_matrix_children",
        [
            new ExpectedColumnDefinition("id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("alternate_id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("code", typeof(string), true, "varchar(30)", maxLength: 30),
            new ExpectedColumnDefinition("alternate_code", typeof(string), true, "varchar(30)", maxLength: 30),
            new ExpectedColumnDefinition("quantity", typeof(int), false, "int"),
            new ExpectedColumnDefinition("parent_id", typeof(int), true, "int"),
            new ExpectedColumnDefinition("alternate_parent_id", typeof(int), true, "int"),
        ],
        primaryKey:
        new ExpectedPrimaryKeyDefinition(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["id", "alternate_id"]),
        uniqueConstraints
        :
        [
            new ExpectedUniqueConstraintDefinition(
                "uq_constraint_matrix_code",
                "constraint_matrix_children",
                ["code", "alternate_code"]),
        ],
        checkConstraints:
        [
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
        ],
        foreignKeys:
        [
            new ExpectedForeignKeyDefinition(
                "fk_constraint_matrix_parent",
                "constraint_matrix_children",
                ["parent_id", "alternate_parent_id"],
                "constraint_matrix_parents",
                ["id", "alternate_id"],
                onUpdate: ReferentialAction.Cascade,
                onDelete: onDelete),
        ]);
}
