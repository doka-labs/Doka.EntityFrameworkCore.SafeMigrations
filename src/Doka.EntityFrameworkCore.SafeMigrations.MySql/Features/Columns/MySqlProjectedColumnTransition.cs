namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Validates physical conversion limits for complete, empty projected tables.</summary>
internal static class MySqlProjectedColumnTransition
{
    /// <summary>Checks source-family, row and dependent-key feasibility without live row assumptions.</summary>
    /// <param name="intent">The exact reviewed transition.</param>
    /// <param name="source">The accepted source column.</param>
    /// <param name="table">The complete accepted table shape.</param>
    /// <param name="environment">The captured provider storage limits.</param>
    /// <param name="mappings">The provider type mappings.</param>
    /// <param name="canReuseCreationCharacterSet">
    /// Whether the table inherited database defaults before an unproven database-default change.
    /// </param>
    /// <returns>Whether every required physical invariant is proven.</returns>
    internal static bool IsSafe(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        ISafeMigrationProjectedAlterTable table,
        SafeMigrationIndexPhysicalEnvironment? environment,
        IRelationalTypeMappingSource mappings,
        bool canReuseCreationCharacterSet
    )
    {
        // A recreated table no longer inherits the old engine row format.
        environment = environment?.NewTableEnvironment ?? environment;
        var target = intent.Definition;
        if (environment is null
            || environment.MaximumKeyBytes == 0
            || !MySqlSafeMigrationColumnMetadata.CanSafelyConverge(source)
            || !MySqlSafeMigrationColumnMetadata.CanSafelyConverge(target)
            || !Equals(source.Collation, target.Collation)
            || source.ClrType != target.ClrType
            || !SupportedFamily(source, target, mappings)
            || !BackfillIsRepresentable(source, target, environment, mappings, canReuseCreationCharacterSet)
            || RequiresStrictConversion(StoreType(source, mappings), StoreType(target, mappings))
                && !environment.StrictSqlMode)
        {
            return false;
        }

        var projected = new TargetColumns(table, target);
        var rowBytes = 0L;
        var inlineBytes = 0L;
        var nullableColumns = 0;
        foreach (var column in table.Columns)
        {
            var definition = StringComparer.Ordinal.Equals(column.Name, target.Name) ? target : column;
            if (!MySqlProjectedAlterPhysicalSnapshot.TryCreateProjectedColumn(definition, mappings, 4, out var shape)
                || !MySqlProjectedAlterPhysicalSnapshot.TryGetInlineBytes(
                    shape,
                    MySqlProjectedAlterPhysicalSnapshot.IsProjectedClusteredKeyColumn(definition.Name, intent, table),
                    environment,
                    out var inline))
            {
                return false;
            }

            rowBytes += shape.DeclaredRowBytes;
            inlineBytes += inline;
            nullableColumns += definition.IsNullable ? 1 : 0;
        }

        if (rowBytes + ((nullableColumns + 7) / 8) > 65535
            || !MySqlProjectedAlterPhysicalSnapshot.InlineRowFits(inlineBytes, nullableColumns, environment))
        {
            return false;
        }

        if (table.PrimaryKey is { } primaryKey
            && MySqlProjectedIndexPhysicalShape.Validate(primaryKey, projected, environment, mappings)
                != MySqlProjectedIndexPhysicalShapeResult.Feasible)
        {
            return false;
        }

        return table.UniqueConstraints.All(key =>
                MySqlProjectedIndexPhysicalShape.Validate(key, projected, environment, mappings)
                    == MySqlProjectedIndexPhysicalShapeResult.Feasible)
            && table.Indexes.All(key =>
                MySqlProjectedIndexPhysicalShape.Validate(key, projected, environment, mappings)
                    == MySqlProjectedIndexPhysicalShapeResult.Feasible);
    }

    private static bool SupportedFamily(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        IRelationalTypeMappingSource mappings
    )
    {
        var sourceType = StoreType(source, mappings);
        var targetType = StoreType(target, mappings);
        if (sourceType is null
            || targetType is null)
        {
            return false;
        }

        if (source.ClrType == typeof(bool)
            || source.ClrType == typeof(bool?))
        {
            return sourceType == "bit(1)"
                && targetType == "tinyint(1)"
                && BooleanDefault(source.DefaultValue)
                && BooleanDefault(target.DefaultValue);
        }

        if (source.ClrType != typeof(string))
        {
            return false;
        }

        var sourceVarchar = MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(sourceType, out var sourceLength);
        var sourceText = MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(sourceType, out var sourceCapacity);
        if (!sourceVarchar
            && !sourceText)
        {
            return false;
        }

        if (MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(targetType, out _))
        {
            return true;
        }

        return MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(targetType, out var targetCapacity)
            && (sourceVarchar ? (ulong)sourceLength * 4 : sourceCapacity) <= targetCapacity;
    }

    private static bool BooleanDefault(SafeMigrationDefaultValue value) =>
        value.Kind == SafeMigrationDefaultValueKind.None
        || (value.Kind == SafeMigrationDefaultValueKind.Literal && value.GetLiteralValue() is null or bool);

    /// <summary>Matches runtime's strict-mode prerequisite for a potentially narrowing string domain.</summary>
    /// <param name="sourceType">The resolved source store type, or null when unavailable.</param>
    /// <param name="targetType">The resolved target store type, or null when unavailable.</param>
    /// <returns>Whether the resolved target may narrow the source character domain.</returns>
    internal static bool RequiresStrictConversion(
        string? sourceType,
        string? targetType
    ) => sourceType is not null
        && targetType is not null
        && MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(targetType, out var targetLength)
        && (!MySqlSafeMigrationCatalogSqlBuilder.TryParseVarcharLength(sourceType, out var sourceLength)
            || sourceLength > targetLength);

    private static bool BackfillIsRepresentable(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        SafeMigrationIndexPhysicalEnvironment environment,
        IRelationalTypeMappingSource mappings,
        bool canReuseCreationCharacterSet
    )
    {
        // WHY: AlterDatabase preserves existing tables but can replace the
        // charset inherited by a later table. Such a table cannot reuse the
        // original default; an explicit, captured column collation still can.
        var characterSet = canReuseCreationCharacterSet ? environment.DefaultCharacterSet : null;
        if (source.Collation is { } collation)
        {
            characterSet = collation.Schema is null
                && environment.CollationCharacterSets is { } characterSets
                && characterSets.TryGetValue(collation.Name, out var resolved) ? resolved : null;
        }

        // WHY: Empty projection must obey the same DDL/backfill contract as
        // runtime, even though its UPDATE would affect no rows. Only captured
        // charset facts can authorize literal encoding; names are not guessed.
        return MySqlSafeMigrationCatalogSqlBuilder.CanRepresentAlterColumnBackfill(
            source, target, StoreType(source, mappings)!, StoreType(target, mappings)!,
            characterSet, environment.StrictSqlMode, environment.SupportsQuotedExpressionDefaults,
            environment.SupportsTextExpressionControlCharacters);
    }

    private static string? StoreType(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappings
    )
    {
        var storeType = definition.StoreType ?? mappings.FindMapping(
            definition.ClrType, definition.StoreType, keyOrIndex: false, definition.IsUnicode,
            definition.MaxLength, definition.IsRowVersion, definition.IsFixedLength,
            definition.Precision, definition.Scale)?.StoreType;

        return storeType?.Trim().ToLowerInvariant();
    }

    private sealed class TargetColumns(
        ISafeMigrationProjectedAlterTable table,
        ExpectedColumnDefinition target
    ) : ISafeMigrationProjectedColumnSource
    {
        public bool TryGetProjectedColumn(
            string tableName,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            if (StringComparer.Ordinal.Equals(column, target.Name))
            {
                definition = target;

                return true;
            }

            return table.TryGetProjectedColumn(tableName, schema, column, out definition);
        }
    }
}
