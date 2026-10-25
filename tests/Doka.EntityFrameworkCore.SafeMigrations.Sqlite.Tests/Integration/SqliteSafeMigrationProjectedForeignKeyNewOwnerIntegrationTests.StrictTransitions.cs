namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises projected foreign key new owner with independently owned SQLite state.</summary>
public sealed partial class SqliteSafeMigrationProjectedForeignKeyNewOwnerIntegrationTests
    : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Allowed later PK shapes cannot lend their native identity to an incompatible original PK.</summary>
    /// <param name="nativeKeyColumn">The physically installed initial or allowed terminal key member.</param>
    /// <param name="bindsInitial">Whether the complete physical shape also proves the original creator's key.</param>
    [Theory]
    [InlineData("Id", true)]
    [InlineData("OtherId", false)]
    public async Task StrictConstraintTransition_BindsOnlyShapeMatchingPrimaryKeyIdentity(
        string nativeKeyColumn,
        bool bindsInitial)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE transition_items (Id INTEGER NOT NULL, OtherId INTEGER NOT NULL, "
            + $"CONSTRAINT physical_current_pk PRIMARY KEY({nativeKeyColumn}));");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("transition_items", table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                OtherId = table.Column<int>(type: "INTEGER", nullable: false),
            }, constraints: table => table.PrimaryKey("logical_initial_pk", item => item.Id));
        builder.DropPrimaryKeyIfExists("logical_initial_pk", "transition_items");
        builder.AddPrimaryKeyIfNotExists("logical_terminal_pk", "transition_items", ["OtherId"]);

        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());

        Assert.Equal(SafeMigrationObservedState.Matching, results[0].ObservedState);
        Assert.Equal(bindsInitial, results[0].TableConstraintIdentities?.Any(binding =>
            binding.Kind == SafeMigrationDatabaseObjectKind.PrimaryKey
            && binding.AuthoredName == "logical_initial_pk"
            && binding.PhysicalName == "physical_current_pk") ?? false);
    }

    /// <summary>Standalone transitions never hide base-column drift or an unauthored physical FK shape.</summary>
    /// <param name="scenario">The native violation of the creator or its terminal standalone FK.</param>
    [Theory]
    [InlineData("column_type")]
    [InlineData("extra_fk")]
    [InlineData("wrong_principal")]
    [InlineData("wrong_action")]
    [InlineData("wrong_columns")]
    [InlineData("missing_fk")]
    public async Task StrictConstraintTransition_PostflightRetainsExactNativeContract(string scenario)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents PRIMARY KEY(Id)); "
            + "CREATE TABLE other_parents (Id INTEGER NOT NULL PRIMARY KEY);");
        var type = scenario == "column_type" ? "TEXT" : "INTEGER";
        var principal = scenario == "wrong_principal" ? "other_parents" : "constraint_parents";
        var action = scenario == "wrong_action" ? "RESTRICT" : "CASCADE";
        var columns = scenario == "wrong_columns" ? "Id" : "ParentId";
        var foreignKey = scenario == "missing_fk" ? string.Empty
            : $", CONSTRAINT fk_constraint_children_parent FOREIGN KEY({columns}) "
                + $"REFERENCES {principal}(Id) ON DELETE {action}";

        var extra = scenario == "extra_fk"
            ? ", CONSTRAINT fk_extra FOREIGN KEY(Id) REFERENCES constraint_parents(Id)" : string.Empty;

        await ExecuteSqlAsync(connection,
            $"CREATE TABLE constraint_children (Id {type} NOT NULL, ParentId INTEGER NULL, "
            + $"CONSTRAINT pk_constraint_children PRIMARY KEY(Id){foreignKey}{extra}); "
            + "CREATE INDEX ix_constraint_children_parent_id ON constraint_children(ParentId);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true);

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-constraint-negative"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[scenario == "missing_fk" ? 2 : 0].PostconditionSatisfied);
        if (scenario == "missing_fk")
        {
            Assert.True(report.Assessments[0].PostconditionSatisfied);
        }
    }

    /// <summary>The full child and principal member order remains part of an allowed standalone FK shape.</summary>
    [Fact]
    public async Task StrictConstraintTransition_RejectsReversedNativeForeignKeyOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, OtherId INTEGER NOT NULL, "
            + "PRIMARY KEY(Id, OtherId)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY(Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY(Id, ParentId) "
            + "REFERENCES constraint_parents(Id, OtherId) ON DELETE CASCADE); "
            + "CREATE INDEX ix_constraint_children_parent_id ON constraint_children(ParentId);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true);
        builder.Operations.RemoveAt(2);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "constraint_children", ["ParentId", "Id"],
            "constraint_parents", ["Id", "OtherId"], onDelete: ReferentialAction.Cascade);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-fk-member-order"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
    }

    /// <summary>Main and unqualified ASCII-case table aliases share the authored transition.</summary>
    [Fact]
    public async Task StrictConstraintTransition_UsesSqlitePhysicalTableIdentity()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY(Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY(ParentId) "
            + "REFERENCES constraint_parents(Id) ON DELETE CASCADE); "
            + "CREATE INDEX ix_constraint_children_parent_id ON constraint_children(ParentId);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true);
        builder.Operations.RemoveAt(2);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "CONSTRAINT_CHILDREN", ["ParentId"],
            "constraint_parents", ["Id"], schema: "MAIN", principalSchema: "main",
            onDelete: ReferentialAction.Cascade);

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-constraint-aliases"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, item => Assert.True(item.PostconditionSatisfied));
    }

    /// <summary>Native future constraints cannot make an earlier strict creator match across a boundary.</summary>
    /// <param name="boundary">The opaque or table-lifetime boundary separating the creator from a later FK.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("drop_recreate")]
    [InlineData("rename")]
    [InlineData("repeated")]
    public async Task StrictConstraintTransition_NativeForeignKeyCannotCrossCreatorBoundary(string boundary)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY(Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY(ParentId) "
            + "REFERENCES constraint_parents(Id) ON DELETE CASCADE); "
            + "CREATE INDEX ix_constraint_children_parent_id ON constraint_children(ParentId);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true);
        var originalCreator = builder.Operations[0];
        builder.Operations.Clear();
        builder.Operations.Add(originalCreator);
        switch (boundary)
        {
            case "sql":
                builder.Sql("SELECT 1;");
                break;
            case "drop_recreate":
                builder.DropTableIfExists("constraint_children");
                builder.Operations.Add(CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true).Operations[0]);
                break;
            case "rename":
                builder.RenameTableIfExists("constraint_children", newName: "renamed_children");
                break;
            case "repeated":
                builder.Operations.Add(CreateProjectedForeignKeyNewOwnerBuilder(context, strict: true).Operations[0]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }

        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", boundary == "rename" ? "renamed_children" : "constraint_children",
            ["ParentId"], "constraint_parents", ["Id"], onDelete: ReferentialAction.Cascade);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-native-boundary"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.Equal("classified_different", report.Assessments[0].AnalysisCode);
    }
}
