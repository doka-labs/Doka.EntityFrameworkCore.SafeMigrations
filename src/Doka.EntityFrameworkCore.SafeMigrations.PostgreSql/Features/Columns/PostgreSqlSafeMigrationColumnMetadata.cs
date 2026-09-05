namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Validates the Npgsql column metadata supported by guarded repair.</summary>
internal static class PostgreSqlSafeMigrationColumnMetadata
{
    private const string ValueGenerationStrategyAnnotation = "Npgsql:ValueGenerationStrategy";

    /// <summary>
    /// Determines whether the complete column shape can be replayed for a
    /// guarded PostgreSQL repair.
    /// </summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when repair is provider-proven safe.</returns>
    public static bool CanSafelyConverge(
        ExpectedColumnDefinition definition
    ) => SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(definition)
        && Supports(definition);

    /// <summary>Gets the recognized Npgsql value-generation strategy.</summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns>The strategy, or <see langword="null" /> when none is declared.</returns>
    public static NpgsqlValueGenerationStrategy? GetValueGenerationStrategy(
        ExpectedColumnDefinition definition
    ) => definition.ProviderAnnotations.SingleOrDefault(annotation =>
            StringComparer.Ordinal.Equals(annotation.Name, ValueGenerationStrategyAnnotation))
        ?.Value as NpgsqlValueGenerationStrategy?;

    /// <summary>Determines whether every provider annotation has reviewed semantics.</summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when every annotation is supported.</returns>
    public static bool Supports(
        ExpectedColumnDefinition definition
    ) => definition.ProviderAnnotations.All(static annotation =>
        StringComparer.Ordinal.Equals(annotation.Name, ValueGenerationStrategyAnnotation)
        && annotation.Value is NpgsqlValueGenerationStrategy.None
            or NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
}
