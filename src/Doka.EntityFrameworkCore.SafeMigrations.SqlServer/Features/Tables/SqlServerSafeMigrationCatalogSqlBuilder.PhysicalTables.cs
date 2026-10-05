namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private const string UnsupportedPhysicalTablePredicate = "physical.is_memory_optimized = 1 "
        + "OR physical.is_filetable = 1 OR physical.temporal_type <> 0 "
        + "OR physical.is_node = 1 OR physical.is_edge = 1 "
        + "OR EXISTS (SELECT 1 FROM sys.columns ledger_column WHERE ledger_column.object_id = physical.object_id "
        + "AND ledger_column.generated_always_type IN (5, 6, 7, 8))";

    /// <summary>Proves an existing table uses the ordinary authored engine contract, allowing absence.</summary>
    /// <param name="table">The requested physical table name.</param>
    /// <param name="schema">The requested schema, or null for dbo.</param>
    /// <returns>A catalog-only scalar bit, independent of table existence prerequisites.</returns>
    internal string BuildPhysicalTableSupportExpression(
        string table,
        string? schema
    )
        => Bit("NOT EXISTS (SELECT 1 FROM sys.tables physical "
            + $"WHERE physical.object_id = {TableId(table, schema)} "
            + $"AND ({UnsupportedPhysicalTablePredicate}))");

    private string? BuildPhysicalTableSupportExpression(SafeMigrationIntent intent)
    {
        // WHY: Most operations touch one table. Reusing its scalar proof
        // avoids a temporary identity array, set, list, and nested CASE copies
        // on every catalog plan without omitting any physical-engine guard.
        var single = intent switch
        {
            EnsureTableIntent { Definition.ForeignKeys.Count: 0 } value
                => (value.Definition.Table, value.Definition.Schema),
            DropTableIntent value => (value.Table, value.Schema),
            EnsureColumnIntent value => (value.Table, value.Schema),
            DropColumnIntent value => (value.Table, value.Schema),
            RenameColumnIntent value => (value.Table, value.Schema),
            AlterColumnIntent value => (value.Table, value.Schema),
            EnsureIndexIntent value => (value.Definition.Table, value.Definition.Schema),
            DropIndexIntent value => (value.Table, value.Schema),
            RenameIndexIntent value => (value.Table, value.Schema),
            EnsurePrimaryKeyIntent value => (value.Definition.Table, value.Definition.Schema),
            DropPrimaryKeyIntent value => (value.Table, value.Schema),
            EnsureUniqueConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
            DropUniqueConstraintIntent value => (value.Table, value.Schema),
            EnsureCheckConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
            DropCheckConstraintIntent value => (value.Table, value.Schema),
            DeleteModelManagedDataIntent { ForeignKeys.Count: > 0 } => ((string, string?)?)null,
            ModelManagedDataIntent value => (value.Table, value.Schema),
            _ => ((string, string?)?)null,
        };

        if (single is { } identity)
        {
            return BuildPhysicalTableSupportExpression(identity.Item1, identity.Item2);
        }

        // WHY: Filtering TableExists would misclassify unsupported engines as
        // absent and authorize CREATE over a real object. This independent
        // boundary must run before prerequisites, data probes, and baseline DDL.
        // Ledger generation markers use the existing sys.columns field, so
        // older servers do not bind a SQL Server 2022-only ledger_type column.
        // Graph edge constraints are not ordinary sys.foreign_keys and can
        // reject node deletion or cascade into uncaptured edge rows.
        (string Table, string? Schema)[] identities = intent switch
        {
            EnsureTableIntent value =>
            [
                (value.Definition.Table, value.Definition.Schema),
                .. value.Definition.ForeignKeys.Select(static foreignKey =>
                    (foreignKey.PrincipalTable, foreignKey.PrincipalSchema)),
            ],
            DropTableIntent value => [(value.Table, value.Schema)],
            RenameTableIntent value =>
            [
                (value.Name, value.Schema),
                (value.NewName ?? value.Name, value.NewSchema ?? value.Schema),
            ],
            EnsureColumnIntent value => [(value.Table, value.Schema)],
            DropColumnIntent value => [(value.Table, value.Schema)],
            RenameColumnIntent value => [(value.Table, value.Schema)],
            AlterColumnIntent value => [(value.Table, value.Schema)],
            EnsureIndexIntent value => [(value.Definition.Table, value.Definition.Schema)],
            DropIndexIntent value => [(value.Table, value.Schema)],
            RenameIndexIntent value => [(value.Table, value.Schema)],
            EnsurePrimaryKeyIntent value => [(value.Definition.Table, value.Definition.Schema)],
            DropPrimaryKeyIntent value => [(value.Table, value.Schema)],
            EnsureUniqueConstraintIntent value => [(value.Definition.Table, value.Definition.Schema)],
            DropUniqueConstraintIntent value => [(value.Table, value.Schema)],
            EnsureCheckConstraintIntent value => [(value.Definition.Table, value.Definition.Schema)],
            DropCheckConstraintIntent value => [(value.Table, value.Schema)],
            EnsureForeignKeyIntent value =>
            [
                (value.Definition.Table, value.Definition.Schema),
                (value.Definition.PrincipalTable, value.Definition.PrincipalSchema),
            ],
            DropForeignKeyIntent value => [(value.Table, value.Schema)],
            DeleteModelManagedDataIntent value =>
            [
                (value.Table, value.Schema),
                .. value.ForeignKeys.Select(static foreignKey => (foreignKey.Table, foreignKey.Schema)),
            ],
            ModelManagedDataIntent value => [(value.Table, value.Schema)],
            _ => [],
        };

        var predicates = identities.Distinct().Select(identity =>
            $"({BuildPhysicalTableSupportExpression(identity.Table, identity.Schema)}) = 1").ToList();

        if (intent is DropForeignKeyIntent dropForeignKey)
        {
            // WHY: Drop captures only the dependent identity. The current
            // catalog still supplies its principal's engine contract.
            predicates.Add("NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk "
                + "JOIN sys.tables physical ON physical.object_id = fk.referenced_object_id "
                + $"WHERE fk.parent_object_id = {TableId(dropForeignKey.Table, dropForeignKey.Schema)} "
                + $"AND fk.name = {Literal(dropForeignKey.Name)} AND ({UnsupportedPhysicalTablePredicate}))");
        }

        return predicates.Count == 0 ? null : Bit(string.Join(" AND ", predicates));
    }
}
