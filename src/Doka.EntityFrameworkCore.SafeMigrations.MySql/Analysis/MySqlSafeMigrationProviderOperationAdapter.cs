namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Converts Doka-specific standard-operation metadata into closed
/// SafeMigrations contracts and certifies the bounded database-default change.
/// </summary>
internal sealed class MySqlSafeMigrationProviderOperationAdapter : ISafeMigrationProviderOperationAdapter
{
    private const string CharacterSetAnnotation = "Doka:MySql:CharSet";
    private const string CollationAnnotation = "Relational:Collation";
    private const string GuidFormatAnnotation = "Doka:MySql:GuidFormat";
    private const string ValueGenerationStrategyAnnotation = "Doka:MySql:ValueGenerationStrategy";

    /// <summary>Gets the stateless MySQL/MariaDB operation adapter.</summary>
    public static MySqlSafeMigrationProviderOperationAdapter Instance { get; } = new();

    private MySqlSafeMigrationProviderOperationAdapter() { }

    /// <inheritdoc />
    public bool TryNormalize(
        MigrationOperation operation,
        [NotNullWhen(true)] out SafeMigrationOperation? safeOperation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        safeOperation = operation switch
        {
            CreateIndexOperation index => NormalizeIndex(index),
            DropColumnOperation column when HasOnlyRecognizedColumnIdentityAnnotations(column) =>
                new SafeMigrationOperation(
                    new DropColumnIntent(column.Name, column.Table, column.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            RenameColumnOperation column when HasOnlyRecognizedColumnIdentityAnnotations(column) =>
                new SafeMigrationOperation(
                    new RenameColumnIntent(column.Name, column.Table, column.NewName, column.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            _ => null,
        };

        return safeOperation is not null;
    }

    /// <inheritdoc />
    public bool IsCertifiedPassthrough(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation is not AlterDatabaseOperation alterDatabase)
        {
            return false;
        }

        // WHY: Doka emits only ALTER DATABASE CHARACTER SET/COLLATE for this
        // shape. It does not rewrite existing tables or rows, and repeating the
        // same defaults is idempotent. Every other annotation remains rejected.
        return HasValidOptionalCollation(alterDatabase.Collation)
            && HasValidOptionalCollation(alterDatabase.OldDatabase.Collation)
            && HasOnlyCharacterSetAnnotation(alterDatabase)
            && HasOnlyCharacterSetAnnotation(alterDatabase.OldDatabase);
    }

    private static SafeMigrationOperation? NormalizeIndex(
        CreateIndexOperation operation
    )
    {
        var metadata = operation.GetMySqlMigrationMetadata();
        var recognizedAnnotationCount = metadata.IndexPrefixLengths is null ? 0 : 1;
        if (operation.GetAnnotations().Count() != recognizedAnnotationCount)
        {
            return null;
        }

        if (operation.IsDescending is not null
            && operation.IsDescending.Length != operation.Columns.Length)
        {
            throw new InvalidOperationException(
                $"Index '{operation.Name}' has a sort-order count that does not match its key count.");
        }

        var keys = operation.Columns.Select((column, ordinal) => new ExpectedIndexKeyDefinition(
            column,
            sortOrder: operation.IsDescending is null
                ? SafeMigrationIndexSortOrder.ProviderDefault
                : operation.IsDescending[ordinal]
                    ? SafeMigrationIndexSortOrder.Descending
                    : SafeMigrationIndexSortOrder.Ascending,
            prefixLength: metadata.IndexPrefixLengths?[ordinal] is > 0
                ? metadata.IndexPrefixLengths[ordinal]
                : null));

        var definition = new ExpectedIndexDefinition(
            operation.Name,
            operation.Table,
            keys,
            operation.Schema,
            operation.IsUnique,
            operation.Filter);

        return new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);
    }

    private static bool HasOnlyRecognizedColumnIdentityAnnotations(
        MigrationOperation operation
    )
    {
        // WHY: Doka copies these facets from the mapped column for both drop
        // and rename. Supported MySQL/MariaDB profiles use native RENAME COLUMN,
        // which preserves the physical shape; none of these annotations changes
        // the identity transition guarded here.
        foreach (var annotation in operation.GetAnnotations())
        {
            var valid = annotation switch
            {
                { Name: CharacterSetAnnotation, Value: string value } => !string.IsNullOrWhiteSpace(value),
                { Name: CollationAnnotation, Value: string value } => !string.IsNullOrWhiteSpace(value),
                { Name: GuidFormatAnnotation, Value: DokaMySqlGuidFormat format } => Enum.IsDefined(format),
                { Name: ValueGenerationStrategyAnnotation, Value: MySqlValueGenerationStrategy strategy } =>
                    Enum.IsDefined(strategy),
                _ => false,
            };

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOnlyCharacterSetAnnotation(
        IReadOnlyAnnotatable operation
    )
    {
        var annotations = operation.GetAnnotations().ToArray();
        if (annotations.Length == 0)
        {
            return true;
        }

        return annotations is [{ Name: CharacterSetAnnotation, Value: string value }]
            && !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasValidOptionalCollation(
        string? collation
    ) => collation is null || !string.IsNullOrWhiteSpace(collation);
}
