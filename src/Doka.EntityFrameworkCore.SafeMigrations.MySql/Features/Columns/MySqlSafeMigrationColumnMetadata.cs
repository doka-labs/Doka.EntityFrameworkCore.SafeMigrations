namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Projects validated Doka column metadata without depending on provider-private
/// annotation identities.
/// </summary>
internal readonly record struct MySqlSafeMigrationColumnMetadata(
    DokaMySqlGuidFormat? GuidFormat,
    MySqlValueGenerationStrategy? ValueGenerationStrategy
)
{
    /// <summary>
    /// Attempts to project every captured provider annotation through Doka's
    /// public typed migration-operation metadata contract.
    /// </summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <param name="metadata">The validated typed metadata on success.</param>
    /// <returns>
    /// <see langword="true" /> only when every annotation is recognized and its
    /// physical meaning is supported by SafeMigrations.
    /// </returns>
    public static bool TryCreate(
        ExpectedColumnDefinition definition,
        out MySqlSafeMigrationColumnMetadata metadata
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        var operation = new AddColumnOperation
        {
            Name = definition.Name,
            Table = "doka_sm_metadata_projection",
            ClrType = definition.ClrType,
            ColumnType = definition.StoreType,
            IsNullable = definition.IsNullable,
        };

        foreach (var annotation in definition.ProviderAnnotations)
        {
            operation[annotation.Name] = annotation.CreateValueSnapshot();
        }

        MySqlMigrationOperationMetadata providerMetadata;
        try
        {
            providerMetadata = operation.GetMySqlMigrationMetadata();
        }
        catch (InvalidOperationException)
        {
            metadata = default;

            return false;
        }

        var recognizedAnnotationCount = (providerMetadata.GuidFormat is null ? 0 : 1)
            + (providerMetadata.ValueGenerationStrategy is null ? 0 : 1);

        if (recognizedAnnotationCount != definition.ProviderAnnotations.Count
            || !Supports(providerMetadata.ValueGenerationStrategy))
        {
            metadata = default;

            return false;
        }

        metadata = new MySqlSafeMigrationColumnMetadata(
            providerMetadata.GuidFormat,
            providerMetadata.ValueGenerationStrategy);

        return true;
    }

    /// <summary>
    /// Determines whether the complete column shape can be replayed for a
    /// guarded MySQL or MariaDB repair.
    /// </summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when repair is provider-proven safe.</returns>
    public static bool CanSafelyConverge(
        ExpectedColumnDefinition definition
    ) => SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(definition)
        && TryCreate(definition, out _);

    private static bool Supports(
        MySqlValueGenerationStrategy? strategy
    ) => strategy is null
        or MySqlValueGenerationStrategy.None
        or MySqlValueGenerationStrategy.AutoIncrement
        or MySqlValueGenerationStrategy.ClientGuid;
}
