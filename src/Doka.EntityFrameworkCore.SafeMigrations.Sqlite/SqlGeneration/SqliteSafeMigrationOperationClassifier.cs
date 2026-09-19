namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Classifies operations for SQLite structural batching and table rebuilds.</summary>
internal static class SqliteSafeMigrationOperationClassifier
{
    /// <summary>Determines whether an operation can share an ordered structural batch.</summary>
    public static bool CanParticipateInStructuralBatch(
        MigrationOperation operation
    ) => operation switch
    {
        SafeMigrationOperation safe => safe.Intent switch
        {
            ModelManagedDataIntent => false,
            EnsureSchemaIntent => false,
            DropSchemaIntent => false,
            RenameIndexIntent => false,
            EnsureColumnIntent column when column.Definition.ComputedColumnSql is not null
                || column.Definition.ComputedExpression is not null => false,
            EnsureIndexIntent index when RequiresCustomIndexSql(index.Definition) => false,
            _ => true,
        },
        CreateTableOperation
            or DropTableOperation
            or RenameTableOperation
            or AddColumnOperation
            or AlterColumnOperation
            or DropColumnOperation
            or RenameColumnOperation
            or CreateIndexOperation
            or DropIndexOperation
            or RenameIndexOperation
            or AddPrimaryKeyOperation
            or DropPrimaryKeyOperation
            or AddUniqueConstraintOperation
            or DropUniqueConstraintOperation
            or AddCheckConstraintOperation
            or DropCheckConstraintOperation
            or AddForeignKeyOperation
            or DropForeignKeyOperation
            or AlterTableOperation => true,
        _ => false,
    };

    /// <summary>Determines whether a safe intent requires SQLite's table-rebuild path.</summary>
    public static bool RequiresTableRebuild(
        SafeMigrationIntent intent
    ) => intent is AlterColumnIntent
        or DropColumnIntent
        or EnsurePrimaryKeyIntent
        or DropPrimaryKeyIntent
        or EnsureUniqueConstraintIntent
        or DropUniqueConstraintIntent
        or EnsureCheckConstraintIntent
        or DropCheckConstraintIntent
        or EnsureForeignKeyIntent
        or DropForeignKeyIntent;

    /// <summary>Gets the table targeted by a safe migration intent.</summary>
    public static string? TableName(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureTableIntent value => value.Definition.Table,
        DropTableIntent value => value.Table,
        RenameTableIntent value => value.Name,
        EnsureColumnIntent value => value.Table,
        AlterColumnIntent value => value.Table,
        DropColumnIntent value => value.Table,
        RenameColumnIntent value => value.Table,
        EnsureIndexIntent value => value.Definition.Table,
        DropIndexIntent value => value.Table,
        RenameIndexIntent value => value.Table,
        EnsurePrimaryKeyIntent value => value.Definition.Table,
        DropPrimaryKeyIntent value => value.Table,
        EnsureUniqueConstraintIntent value => value.Definition.Table,
        DropUniqueConstraintIntent value => value.Table,
        EnsureCheckConstraintIntent value => value.Definition.Table,
        DropCheckConstraintIntent value => value.Table,
        EnsureForeignKeyIntent value => value.Definition.Table,
        DropForeignKeyIntent value => value.Table,
        ModelManagedDataIntent value => value.Table,
        _ => null,
    };

    /// <summary>Gets the table targeted by a migration operation.</summary>
    public static string? TableName(
        MigrationOperation operation
    ) => operation switch
    {
        SafeMigrationOperation safe => TableName(safe.Intent),
        ColumnOperation column => column.Table,
        CreateTableOperation table => table.Name,
        DropTableOperation table => table.Name,
        RenameTableOperation table => table.Name,
        AlterTableOperation table => table.Name,
        CreateIndexOperation index => index.Table,
        DropIndexOperation index => index.Table,
        RenameIndexOperation index => index.Table,
        AddPrimaryKeyOperation value => value.Table,
        DropPrimaryKeyOperation value => value.Table,
        AddUniqueConstraintOperation value => value.Table,
        DropUniqueConstraintOperation value => value.Table,
        AddCheckConstraintOperation value => value.Table,
        DropCheckConstraintOperation value => value.Table,
        AddForeignKeyOperation value => value.Table,
        DropForeignKeyOperation value => value.Table,
        _ => null,
    };

    /// <summary>Determines whether an operation requires SQLite's table-rebuild path.</summary>
    public static bool RequiresTableRebuild(
        MigrationOperation operation
    ) => operation switch
    {
        SafeMigrationOperation safe => RequiresTableRebuild(safe.Intent),
        AlterColumnOperation
            or DropColumnOperation
            or AddPrimaryKeyOperation
            or DropPrimaryKeyOperation
            or AddUniqueConstraintOperation
            or DropUniqueConstraintOperation
            or AddCheckConstraintOperation
            or DropCheckConstraintOperation
            or AddForeignKeyOperation
            or DropForeignKeyOperation => true,
        _ => false,
    };

    private static bool RequiresCustomIndexSql(
        ExpectedIndexDefinition definition
    ) => definition.Keys.Any(static key =>
        key.Expression is not null || key.StructuredExpression is not null || key.Collation is not null);
}
