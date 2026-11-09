namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Certifies the single explicit storage-engine contract supported by guarded MySQL tables.</summary>
internal static class MySqlSafeMigrationTableMetadata
{
    internal const string StorageEngineAnnotation = "Doka:MySql:StorageEngine";

    /// <summary>Certifies only the historical facets emitted for native identity-only transitions.</summary>
    internal static bool CanNormalizeIdentity(
        MigrationOperation operation
    )
    {
        // WHY: Doka copies table facets onto DROP/RENAME and removed keys.
        // These native identity operations neither create nor change those
        // facets, just like column DROP/RENAME. Do not invent an old-engine
        // precondition absent from the public identity API, or accept any
        // unknown annotation that could carry an operative provider option.
        foreach (var annotation in operation.GetAnnotations())
        {
            var recognized = annotation switch
            {
                { Name: StorageEngineAnnotation, Value: string engine } =>
                    StringComparer.OrdinalIgnoreCase.Equals(engine, "InnoDB"),
                { Name: "Doka:MySql:CharSet" or "Relational:Collation", Value: string value } =>
                    !string.IsNullOrWhiteSpace(value),
                _ => false,
            };

            if (!recognized)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Recognizes a captured InnoDB engine without inferring other provider metadata.</summary>
    internal static bool IsExplicitInnoDb(
        ExpectedTableDefinition definition
    ) => IsExplicitInnoDb(definition.ProviderAnnotations);

    /// <summary>Checks borrowed creation metadata without materializing a projected table.</summary>
    internal static bool IsExplicitInnoDb(
        IReadOnlyList<SafeMigrationProviderAnnotation> annotations
    ) => annotations.Count == 1
        && StringComparer.Ordinal.Equals(annotations[0].Name, StorageEngineAnnotation)
        && annotations[0].Value is string engine
        && StringComparer.OrdinalIgnoreCase.Equals(engine, "InnoDB");

    /// <summary>Rejects unknown or unsupported table metadata before native rendering or catalog work.</summary>
    internal static string? GetUnsupportedFeature(
        ExpectedTableDefinition definition
    ) => definition.ProviderAnnotations.Count == 0 || IsExplicitInnoDb(definition)
        ? null
        : "table_storage_engine_metadata";

    /// <summary>Certifies table normalization while keeping constraint annotations fail-closed.</summary>
    internal static bool CanNormalize(
        CreateTableOperation operation
    ) => operation
            .GetAnnotations()
            .Count()
        == 1
        && operation[StorageEngineAnnotation] is string engine
        && StringComparer.OrdinalIgnoreCase.Equals(engine, "InnoDB")
        && (operation.PrimaryKey is null
            || !operation
                .PrimaryKey
                .GetAnnotations()
                .Any())
        && operation.UniqueConstraints.All(static value => !value
            .GetAnnotations()
            .Any())
        && operation.CheckConstraints.All(static value => !value
            .GetAnnotations()
            .Any())
        && operation.ForeignKeys.All(static value => !value
            .GetAnnotations()
            .Any());
}
