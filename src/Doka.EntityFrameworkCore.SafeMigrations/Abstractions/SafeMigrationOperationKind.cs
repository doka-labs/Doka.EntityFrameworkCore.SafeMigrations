namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Identifies every operation family supported by the SafeMigrations contract.
/// </summary>
public enum SafeMigrationOperationKind
{
    /// <summary>Ensure a schema exists.</summary>
    EnsureSchema = 0,

    /// <summary>Drop a schema when it exists.</summary>
    DropSchema = 1,

    /// <summary>Ensure a table exists.</summary>
    EnsureTable = 2,

    /// <summary>Drop a table when it exists.</summary>
    DropTable = 3,

    /// <summary>Rename a table when the source exists.</summary>
    RenameTable = 4,

    /// <summary>Ensure a column exists.</summary>
    EnsureColumn = 5,

    /// <summary>Drop a column when it exists.</summary>
    DropColumn = 6,

    /// <summary>Rename a column when the source exists.</summary>
    RenameColumn = 7,

    /// <summary>Alter a column when its definition differs.</summary>
    AlterColumn = 8,

    /// <summary>Ensure an index exists.</summary>
    EnsureIndex = 9,

    /// <summary>Drop an index when it exists.</summary>
    DropIndex = 10,

    /// <summary>Rename an index when the source exists.</summary>
    RenameIndex = 11,

    /// <summary>Ensure a primary key exists.</summary>
    EnsurePrimaryKey = 12,

    /// <summary>Drop a primary key when it exists.</summary>
    DropPrimaryKey = 13,

    /// <summary>Ensure a unique constraint exists.</summary>
    EnsureUniqueConstraint = 14,

    /// <summary>Drop a unique constraint when it exists.</summary>
    DropUniqueConstraint = 15,

    /// <summary>Ensure a check constraint exists.</summary>
    EnsureCheckConstraint = 16,

    /// <summary>Drop a check constraint when it exists.</summary>
    DropCheckConstraint = 17,

    /// <summary>Ensure a foreign key exists.</summary>
    EnsureForeignKey = 18,

    /// <summary>Drop a foreign key when it exists.</summary>
    DropForeignKey = 19,

    /// <summary>Ensure model-managed rows exist with their source-controlled values.</summary>
    EnsureModelManagedData = 20,

    /// <summary>Update model-managed rows from captured source values to target values.</summary>
    UpdateModelManagedData = 21,

    /// <summary>Delete model-managed rows only when their captured source values still match.</summary>
    DeleteModelManagedData = 22,
}
