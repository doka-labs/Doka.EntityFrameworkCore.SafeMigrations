namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Validates physical conversion limits for complete, empty projected tables.</summary>
internal static class MySqlProjectedColumnTransition
{
    /// <summary>Requires captured atomic staging prerequisites without inferring server feature versions.</summary>
    /// <param name="isInnoDb">Whether the relevant owner engine is InnoDB.</param>
    /// <param name="strictSqlMode">Whether strict assignment semantics were captured.</param>
    /// <param name="checksEnforced">Whether the engine session enforces CHECK constraints.</param>
    /// <param name="supportsCheck">The canonical configured CHECK capability supplied by the runtime plan.</param>
    /// <returns>Whether required no-default staging retains every captured environmental prerequisite.</returns>
    internal static bool HasSafeAtomicRequiredColumnAddEnvironment(
        bool isInnoDb,
        bool strictSqlMode,
        bool checksEnforced,
        bool supportsCheck
    ) => isInnoDb && strictSqlMode && checksEnforced && supportsCheck;

    /// <summary>Checks a declared member list under the captured native literal-decoding mode.</summary>
    /// <param name="definition">The exact immutable target, including ordered ENUM/SET member spelling.</param>
    /// <param name="requiresBackslashEscapes">Whether the canonical protocol requires standard escapes.</param>
    /// <param name="backslashEscapesEnabled">Whether captured SQL mode decodes backslash escapes.</param>
    /// <param name="doubleQuotesAreLiteral">Whether captured SQL mode treats double quotes as literals.</param>
    /// <returns>Whether the declared member list is valid in the captured session.</returns>
    internal static bool HasSafeRequiredAddLiteralMode(
        ExpectedColumnDefinition definition,
        bool requiresBackslashEscapes,
        bool backslashEscapesEnabled,
        bool doubleQuotesAreLiteral
    )
    {
        if (requiresBackslashEscapes && !backslashEscapesEnabled)
        {
            return false;
        }

        var storeType = definition.StoreType?.TrimStart();
        if (storeType is null || (!storeType.StartsWith("enum", StringComparison.OrdinalIgnoreCase)
                && !storeType.StartsWith("set", StringComparison.OrdinalIgnoreCase)))
        {
            return !requiresBackslashEscapes;
        }

        // WHY: A declaration valid under both backslash modes can still use
        // double-quoted members, which ANSI_QUOTES interprets as identifiers.
        return MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(storeType, backslashEscapesEnabled,
            out var doubleQuotes) is not null && (!doubleQuotes || doubleQuotesAreLiteral);
    }

    /// <summary>Qualifies atomic required SQL-default adds without claiming that an expression is non-null.</summary>
    /// <param name="definition">The immutable reviewed target.</param>
    /// <param name="mappings">The provider mappings used to resolve temporal type roles.</param>
    /// <param name="isInnoDb">Whether the relevant existing or creation engine was captured as InnoDB.</param>
    /// <param name="strictSqlMode">Whether strict assignment semantics were captured.</param>
    /// <param name="explicitTimestampDefaults">Whether explicit TIMESTAMP assignment semantics were captured.</param>
    /// <returns>Whether the captured storage/session facts qualify this SQL-default add.</returns>
    internal static bool HasSafeRequiredSqlDefaultAddEnvironment(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappings,
        bool isInnoDb,
        bool strictSqlMode,
        bool explicitTimestampDefaults
    ) => isInnoDb && strictSqlMode
        && (!MySqlSafeMigrationCatalogSqlBuilder.RequiresExplicitTimestampDefaults(definition,
                ResolveStoreType(definition, mappings)) || explicitTimestampDefaults);

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
            || (source.IsNullable && !target.IsNullable
                || RequiresStrictConversion(ResolveStoreType(source, mappings), ResolveStoreType(target, mappings)))
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
        var sourceType = ResolveStoreType(source, mappings);
        var targetType = ResolveStoreType(target, mappings);
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
            source, target, ResolveStoreType(source, mappings)!, ResolveStoreType(target, mappings)!,
            characterSet, environment.StrictSqlMode, environment.SupportsQuotedExpressionDefaults,
            environment.SupportsTextExpressionControlCharacters);
    }

    /// <summary>Resolves the declared physical type using the same mapping contract as projected repair.</summary>
    /// <param name="definition">The immutable column declaration.</param>
    /// <param name="mappings">The active provider type mappings.</param>
    /// <returns>The normalized physical type, or null when no mapping is available.</returns>
    internal static string? ResolveStoreType(
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
