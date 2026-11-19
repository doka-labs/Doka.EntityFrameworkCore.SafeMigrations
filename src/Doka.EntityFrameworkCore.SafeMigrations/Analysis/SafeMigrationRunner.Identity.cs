namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    /// <summary>Captures only authored identities from ordinary EF operations without row or SQL payloads.</summary>
    internal static SafeMigrationOperationIdentity? CaptureProviderIdentity(
        MigrationOperation operation
    ) => operation switch
    {
        AddColumnOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureColumn,
            value.Schema,
            value.Table,
            value.Name,
            SafeMigrationOperationIdentity.CaptureSingleColumn(value.Name)),
        AlterColumnOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.AlterColumn,
            value.Schema,
            value.Table,
            value.Name,
            SafeMigrationOperationIdentity.CaptureSingleColumn(value.Name)),
        DropColumnOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropColumn,
            value.Schema,
            value.Table,
            value.Name,
            SafeMigrationOperationIdentity.CaptureSingleColumn(value.Name)),
        RenameColumnOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.RenameColumn,
            value.Schema,
            value.Table,
            value.Name,
            SafeMigrationOperationIdentity.CaptureSingleColumn(value.Name),
            newObjectName: value.NewName),
        CreateIndexOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureIndex,
            value.Schema,
            value.Table,
            value.Name,
            SnapshotColumns(value.Columns)),
        DropIndexOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropIndex,
            value.Schema,
            value.Table,
            value.Name),
        RenameIndexOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.RenameIndex,
            value.Schema,
            value.Table,
            value.Name,
            newObjectName: value.NewName),
        AddPrimaryKeyOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsurePrimaryKey,
            value.Schema,
            value.Table,
            value.Name,
            SnapshotColumns(value.Columns)),
        DropPrimaryKeyOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropPrimaryKey,
            value.Schema,
            value.Table,
            value.Name),
        AddUniqueConstraintOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureUniqueConstraint,
            value.Schema,
            value.Table,
            value.Name,
            SnapshotColumns(value.Columns)),
        DropUniqueConstraintOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropUniqueConstraint,
            value.Schema,
            value.Table,
            value.Name),
        AddCheckConstraintOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureCheckConstraint,
            value.Schema,
            value.Table,
            value.Name),
        DropCheckConstraintOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropCheckConstraint,
            value.Schema,
            value.Table,
            value.Name),
        AddForeignKeyOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureForeignKey,
            value.Schema,
            value.Table,
            value.Name,
            SnapshotColumns(value.Columns),
            value.PrincipalSchema,
            value.PrincipalTable,
            SnapshotColumns(value.PrincipalColumns)),
        DropForeignKeyOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropForeignKey,
            value.Schema,
            value.Table,
            value.Name),
        CreateTableOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureTable,
            value.Schema,
            value.Name,
            value.Name),
        DropTableOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropTable,
            value.Schema,
            value.Name,
            value.Name),
        RenameTableOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.RenameTable,
            value.Schema,
            value.Name,
            value.Name,
            newSchema: value.NewSchema,
            newTable: value.NewName ?? value.Name),
        EnsureSchemaOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureSchema,
            value.Name,
            table: null,
            value.Name),
        DropSchemaOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DropSchema,
            value.Name,
            table: null,
            value.Name),
        AlterTableOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            value.Name,
            value.Name),
        InsertDataOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.EnsureModelManagedData,
            value.Schema,
            value.Table,
            value.Table,
            SnapshotColumns(value.Columns)),
        UpdateDataOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.UpdateModelManagedData,
            value.Schema,
            value.Table,
            value.Table,
            SnapshotColumns(value.Columns)),
        DeleteDataOperation value => CaptureKnownProviderIdentity(
            SafeMigrationOperationKind.DeleteModelManagedData,
            value.Schema,
            value.Table,
            value.Table,
            SnapshotColumns(value.KeyColumns)),
        CreateSequenceOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            table: null,
            value.Name),
        AlterSequenceOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            table: null,
            value.Name),
        DropSequenceOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            table: null,
            value.Name),
        RestartSequenceOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            table: null,
            value.Name),
        RenameSequenceOperation value => CaptureKnownProviderIdentity(
            operationKind: null,
            value.Schema,
            table: null,
            value.Name,
            newSchema: value.NewSchema,
            newObjectName: value.NewName),
        _ => null,
    };

    /// <summary>Captures only usable structural metadata from mutable, potentially incomplete EF operations.</summary>
    private static SafeMigrationOperationIdentity? CaptureKnownProviderIdentity(
        SafeMigrationOperationKind? operationKind,
        string? schema,
        string? table,
        string? objectName,
        IReadOnlyList<string>? columns = null,
        string? principalSchema = null,
        string? principalTable = null,
        IReadOnlyList<string>? principalColumns = null,
        string? newSchema = null,
        string? newTable = null,
        string? newObjectName = null
    )
    {
        var requiresTable = operationKind is not (null
            or SafeMigrationOperationKind.EnsureSchema
            or SafeMigrationOperationKind.DropSchema);

        if (string.IsNullOrWhiteSpace(objectName)
            || (requiresTable && string.IsNullOrWhiteSpace(table))
            || (schema is not null && string.IsNullOrWhiteSpace(schema))
            || (principalSchema is not null && string.IsNullOrWhiteSpace(principalSchema))
            || (newSchema is not null && string.IsNullOrWhiteSpace(newSchema))
            || (newTable is not null && string.IsNullOrWhiteSpace(newTable))
            || (newObjectName is not null && string.IsNullOrWhiteSpace(newObjectName))
            || HasUnavailableIdentifier(columns)
            || HasUnavailableIdentifier(principalColumns))
        {
            // WHY: Ordinary EF operations are mutable and may be malformed.
            // Diagnostics must preserve their original unclassified report,
            // not manufacture names or throw while rendering unavailable data.
            return null;
        }

        if (operationKind == SafeMigrationOperationKind.EnsureForeignKey
            && string.IsNullOrWhiteSpace(principalTable))
        {
            return null;
        }

        return SafeMigrationOperationIdentity.CreateKnown(
            operationKind,
            schema,
            table,
            objectName,
            columns,
            principalSchema,
            principalTable,
            principalColumns,
            newSchema,
            newTable,
            newObjectName);
    }

    /// <summary>Checks a captured member list without allocating a filtered endpoint.</summary>
    private static bool HasUnavailableIdentifier(
        IReadOnlyList<string>? identifiers
    )
    {
        if (identifiers is null)
        {
            return false;
        }

        // WHY: These retained snapshots expose indexed immutable reads. Enumerating
        // IReadOnlyList allocates an iterator, including the one-column metadata view.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < identifiers.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(identifiers[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Snapshots mutable EF column arrays before the report outlives the analyzed stream.</summary>
    private static ReadOnlyCollection<string>? SnapshotColumns(
        string[]? columns
    )
    {
        // WHY: EF operations are mutable and may be reused after analysis.
        // A report must not change when callers mutate a composite endpoint.
        return columns is null
            ? null
            : Array.AsReadOnly((string[])columns.Clone());
    }
}
