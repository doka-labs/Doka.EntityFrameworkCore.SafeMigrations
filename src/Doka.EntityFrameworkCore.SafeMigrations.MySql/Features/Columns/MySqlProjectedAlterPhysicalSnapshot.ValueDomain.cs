namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlProjectedAlterPhysicalSnapshot
{
    /// <summary>Proves target representability from an unchanged original live value domain.</summary>
    /// <param name="intent">The exact reviewed current transition.</param>
    /// <param name="source">The accepted projected source after certified lossless changes.</param>
    /// <returns>Whether every original value fits without inspecting rows.</returns>
    /// <remarks>The caller must independently certify that no intervening mutation changed the value domain.</remarks>
    internal bool PreservesOriginalValueDomain(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source
    )
    {
        var target = intent.Definition;
        if (!columns.TryGetValue(target.Name, out var original)
            || source.ClrType != typeof(string)
            || target.ClrType != typeof(string)
            || !MySqlSafeMigrationColumnMetadata.CanSafelyConverge(source)
            || !MySqlSafeMigrationColumnMetadata.CanSafelyConverge(target)
            || !Equals(source.Collation, target.Collation)
            || original.HasForeignKey
            || original.IsNullable
                && !target.IsNullable
            || source.StoreType is not { } sourceType
            || target.StoreType is not { } targetType)
        {
            return false;
        }

        var sourceIsString = MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(sourceType, out _)
            || MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(sourceType, out _);

        var characterSet = original.CharacterSet;
        if (source.Collation is { } collation)
        {
            characterSet = collation.Schema is null
                && environment.CollationCharacterSets is { } characterSets
                && characterSets.TryGetValue(collation.Name, out var resolved) ? resolved : null;
        }

        if (!sourceIsString
            || MySqlProjectedColumnTransition.RequiresStrictConversion(sourceType, targetType)
                && !environment.StrictSqlMode
            || !MySqlSafeMigrationCatalogSqlBuilder.CanRepresentAlterColumnBackfill(
                source,
                target,
                sourceType,
                targetType,
                characterSet,
                environment.StrictSqlMode,
                environment.SupportsQuotedExpressionDefaults,
                environment.SupportsTextExpressionControlCharacters))
        {
            return false;
        }

        // WHY: An accepted widening changes the schema but does not populate its
        // larger domain. Retaining the original VARCHAR character ceiling (or
        // TEXT byte ceiling) proves a later target only when Core has certified
        // that every intervening operation preserved these original values.
        var originalVarchar = original.StoreTypeFamily == "varchar"
            && original.IndexShape.MaximumPrefixUnits is > 0;

        var originalText = MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(
            original.StoreTypeFamily, out var textCapacity);

        if (!originalVarchar
            && !originalText)
        {
            return false;
        }

        if (MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(targetType, out var targetCharacters))
        {
            return originalVarchar
                ? original.IndexShape.MaximumPrefixUnits!.Value <= targetCharacters
                : textCapacity <= (ulong)targetCharacters;
        }

        if (!MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(targetType, out var targetBytes))
        {
            return false;
        }

        var originalBytes = originalVarchar
            ? (ulong)original.IndexShape.MaximumPrefixUnits!.Value * (ulong)original.CharacterBytes
            : textCapacity;

        return originalBytes <= targetBytes;
    }
}
