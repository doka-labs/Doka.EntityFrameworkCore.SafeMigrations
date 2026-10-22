namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerStandaloneForeignKeyProjectionTests
{
    /// <summary>Captures the same compact key/storage inputs while keeping this unit fixture catalog-only.</summary>
    private static async Task<SqlServerProjectedDependencyGraph> ReadGraph(
        GraphConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        return await SqlServerProjectedDependencyGraph.ReadAsync(connection, null, operations, 73,
            static (table, foreign) => table.Columns.First(column => column.Name == foreign.Columns[0]).StoreType
                == "int" ? "1 = 1" : "0 = 1",
            catalog.ColumnStorageEquals,
            static (_, _) => true, CancellationToken.None,
            static (table, _, _) => table is null || table.Columns[0].StoreType == "int" ? "1 = 1" : "0 = 1");
    }

    private static ExpectedTableDefinition Child(
        string storeType = "int",
        string table = "child",
        string? schema = null
    )
        => new(table, [new ExpectedColumnDefinition("ParentId", storeType == "int" ? typeof(int) : typeof(long),
            false, storeType)], schema);

    private static ExpectedForeignKeyDefinition ForeignKey(
        string table = "child",
        string? schema = null
    )
        => new("FK_" + table, table, ["ParentId"], "parent", ["Id"], schema);

    /// <summary>Creates one accepted namespace occupant without unrelated row or storage differences.</summary>
    private static ExpectedTableDefinition OccupiedTable(
        string table,
        string kind
    )
        => new(table, Child().Columns,
            primaryKey: kind == "PK" ? new ExpectedPrimaryKeyDefinition("FK_child", table, ["ParentId"]) : null,
            uniqueConstraints: kind == "UQ" ? [new ExpectedUniqueConstraintDefinition("FK_child", table, ["ParentId"])]
                : [],
            checkConstraints: kind == "C" ? [new ExpectedCheckConstraintDefinition("FK_child", table,
                "[ParentId] > 0")] : []);

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    )
        => new(intent, intent is AlterColumnIntent ? SafeMigrationPolicy.RepairIfSafe
            : SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis Missing() => Live(SafeMigrationObservedState.Missing);

    private static SafeMigrationProviderAnalysis UnknownLivePrerequisite()
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false,
            "classified_prerequisite_missing");

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state
    )
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching,
            "classified_" + state.ToString().ToLowerInvariant());

    private static SafeMigrationDecision Decision(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
        => SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState, operation.Policy,
            analysis.RepairCapability);

    private static void Accept(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
        => projection.Observe(operation, analysis, analysis, Decision(operation, analysis));

    /// <summary>Builds exactly the ordered mutation whose stale proof must not be promoted.</summary>
    private static SafeMigrationOperation[] Mutations(
        string mutation
    )
        => mutation switch
        {
            "child-type" => [Operation(new AlterColumnIntent("child",
                new ExpectedColumnDefinition("ParentId", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")))],
            "child-drop-column" => [Operation(new DropColumnIntent("ParentId", "child"))],
            "child-rename-column" => [Operation(new RenameColumnIntent("ParentId", "child", "OtherId"))],
            "child-rename-table" => [Operation(new RenameTableIntent("child", "renamed_child"))],
            "parent-drop-column" => [Operation(new DropColumnIntent("Id", "parent"))],
            "parent-replace-column" => [Operation(new DropColumnIntent("Id", "parent")),
                Operation(new EnsureColumnIntent("parent", new ExpectedColumnDefinition("Id", typeof(int),
                    false, "int")))],
            "parent-alter-column" => [Operation(new AlterColumnIntent("parent",
                new ExpectedColumnDefinition("Id", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("Id", typeof(int), false, "int")))],
            "parent-drop-table" => [Operation(new DropTableIntent("parent"))],
            "parent-rename-table" => [Operation(new RenameTableIntent("parent", "renamed_parent"))],
            "parent-drop-key" => [Operation(new DropPrimaryKeyIntent("PK_parent", "parent"))],
            "parent-replace-key-other-columns" => [Operation(new DropPrimaryKeyIntent("PK_parent", "parent")),
                Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("PK_replacement", "parent",
                    ["OtherId"])))],
            "managed-data" => [Operation(new EnsureModelManagedDataIntent("child", ["ParentId"], ["int"],
                ["ParentId"], ["int"], new object?[,] { { 999 } }, null, null))],
            "raw-sql" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
}
