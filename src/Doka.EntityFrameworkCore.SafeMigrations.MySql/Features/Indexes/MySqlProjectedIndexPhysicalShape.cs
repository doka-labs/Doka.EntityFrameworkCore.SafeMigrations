namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal enum MySqlProjectedIndexPhysicalShapeResult : byte
{
    Feasible = 1,
    PrefixExceedsTargetColumn = 2,
    MissingRequiredPrefix = 3,
    KeyExceedsPhysicalLimit = 4,
    UnsupportedStorageEngine = 5,
    Unverifiable = 6,
    TooManyKeyParts = 7,
}

internal static class MySqlProjectedIndexPhysicalShape
{
    internal const int MaximumKeyParts = 16;

    // WHY: A projected definition does not always carry an explicit character
    // set. Four bytes is MySQL's widest supported character unit, so this
    // upper bound can prove feasibility without trusting a stale live encoding.
    private const int MaximumCharacterBytes = 4;

    public static MySqlProjectedIndexPhysicalShapeResult Validate(
        ExpectedIndexDefinition index,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationIndexPhysicalEnvironment? environment,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(typeMappingSource);

        if (index.Keys.Count > MaximumKeyParts)
        {
            return MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts;
        }

        if (environment is null)
        {
            return MySqlProjectedIndexPhysicalShapeResult.Unverifiable;
        }

        if (environment.MaximumKeyBytes == 0)
        {
            return MySqlProjectedIndexPhysicalShapeResult.UnsupportedStorageEngine;
        }

        long totalWidth = 0;
        var hasUnprefixedVariableWidth = false;
        foreach (var key in index.Keys)
        {
            if (key.Column is null)
            {
                return MySqlProjectedIndexPhysicalShapeResult.Unverifiable;
            }

            var width = columns.TryGetProjectedColumn(
                index.Table,
                index.Schema,
                key.Column,
                out var column)
                    ? GetProjectedKeyWidth(column, key.PrefixLength, typeMappingSource)
                    : GetLiveKeyWidth(key.Column, key.PrefixLength, environment.Columns);

            if (width.Result != MySqlProjectedIndexPhysicalShapeResult.Feasible)
            {
                return width.Result;
            }

            if (width.Bytes > long.MaxValue - totalWidth)
            {
                return MySqlProjectedIndexPhysicalShapeResult.Unverifiable;
            }

            totalWidth += width.Bytes;
            hasUnprefixedVariableWidth |= width.UnprefixedVariableWidth;
        }

        if (totalWidth <= environment.MaximumKeyBytes)
        {
            return MySqlProjectedIndexPhysicalShapeResult.Feasible;
        }

        return hasUnprefixedVariableWidth
            ? MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix
            : MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit;
    }

    public static MySqlProjectedIndexPhysicalShapeResult Validate(
        ExpectedPrimaryKeyDefinition primaryKey,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationIndexPhysicalEnvironment? environment,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ArgumentNullException.ThrowIfNull(primaryKey);

        return ValidateUnprefixedColumns(
            primaryKey.Table,
            primaryKey.Schema,
            primaryKey.Columns,
            columns,
            environment,
            typeMappingSource);
    }

    public static MySqlProjectedIndexPhysicalShapeResult Validate(
        ExpectedUniqueConstraintDefinition uniqueConstraint,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationIndexPhysicalEnvironment? environment,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ArgumentNullException.ThrowIfNull(uniqueConstraint);

        return ValidateUnprefixedColumns(
            uniqueConstraint.Table,
            uniqueConstraint.Schema,
            uniqueConstraint.Columns,
            columns,
            environment,
            typeMappingSource);
    }

    internal static SafeMigrationIndexColumnPhysicalShape CreateCatalogColumnShape(
        string dataType,
        long? characterMaximumLength,
        long? characterOctetLength,
        int? numericPrecision,
        int? numericScale,
        int? dateTimePrecision
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataType);

        var normalizedType = dataType.ToLowerInvariant();
        if (IsCharacter(normalizedType))
        {
            var bytesPerUnit = BytesPerUnit(characterMaximumLength, characterOctetLength);

            return new SafeMigrationIndexColumnPhysicalShape(
                characterMaximumLength,
                bytesPerUnit,
                IsText(normalizedType) ? null : characterOctetLength,
                SupportsPrefix: true);
        }

        if (IsBinary(normalizedType))
        {
            return new SafeMigrationIndexColumnPhysicalShape(
                characterOctetLength,
                BytesPerPrefixUnit: 1,
                IsBlob(normalizedType) ? null : characterOctetLength,
                SupportsPrefix: true);
        }

        var scalarWidth = GetScalarWidth(
            new StoreTypeShape(normalizedType, numericPrecision, numericScale),
            numericPrecision,
            numericScale,
            dateTimePrecision);

        return new SafeMigrationIndexColumnPhysicalShape(
            MaximumPrefixUnits: null,
            BytesPerPrefixUnit: 0,
            scalarWidth,
            SupportsPrefix: false);
    }

    private static MySqlProjectedIndexPhysicalShapeResult ValidateUnprefixedColumns(
        string table,
        string? schema,
        IReadOnlyList<string> keyColumns,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationIndexPhysicalEnvironment? environment,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(typeMappingSource);

        if (keyColumns.Count > MaximumKeyParts)
        {
            return MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts;
        }

        if (environment is null)
        {
            return MySqlProjectedIndexPhysicalShapeResult.Unverifiable;
        }

        if (environment.MaximumKeyBytes == 0)
        {
            return MySqlProjectedIndexPhysicalShapeResult.UnsupportedStorageEngine;
        }

        long totalWidth = 0;
        foreach (var columnName in keyColumns)
        {
            var width = columns.TryGetProjectedColumn(table, schema, columnName, out var column)
                ? GetProjectedKeyWidth(
                    column,
                    prefixLength: null,
                    typeMappingSource: typeMappingSource)
                : GetLiveKeyWidth(
                    columnName,
                    prefixLength: null,
                    columns: environment.Columns);

            if (width.Result != MySqlProjectedIndexPhysicalShapeResult.Feasible)
            {
                return width.Result;
            }

            if (width.Bytes > long.MaxValue - totalWidth)
            {
                return MySqlProjectedIndexPhysicalShapeResult.Unverifiable;
            }

            totalWidth += width.Bytes;
        }

        return totalWidth <= environment.MaximumKeyBytes
            ? MySqlProjectedIndexPhysicalShapeResult.Feasible
            : MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit;
    }

    private static KeyWidth GetProjectedKeyWidth(
        ExpectedColumnDefinition definition,
        int? prefixLength,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        RelationalTypeMapping? mapping;
        try
        {
            mapping = typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return KeyWidth.Unverifiable;
        }

        var storeType = definition.StoreType ?? mapping?.StoreType;
        if (storeType is null)
        {
            return KeyWidth.Unverifiable;
        }

        var type = StoreTypeShape.Parse(storeType);
        var declaredLength = type.FirstArgument ?? definition.MaxLength ?? mapping?.Size;

        if (IsCharacter(type.Name))
        {
            return VariableWidth(
                prefixLength,
                declaredLength,
                MaximumCharacterBytes,
                hasUnboundedStorage: IsText(type.Name));
        }

        if (IsBinary(type.Name))
        {
            return VariableWidth(
                prefixLength,
                declaredLength,
                bytesPerUnit: 1,
                hasUnboundedStorage: IsBlob(type.Name));
        }

        if (prefixLength is not null)
        {
            return KeyWidth.Unverifiable;
        }

        var scalarWidth = GetScalarWidth(
            type,
            definition.Precision ?? mapping?.Precision,
            definition.Scale ?? mapping?.Scale,
            definition.Precision ?? mapping?.Precision);

        return scalarWidth is null
            ? KeyWidth.Unverifiable
            : KeyWidth.Feasible(scalarWidth.Value, unprefixedVariableWidth: false);
    }

    private static KeyWidth GetLiveKeyWidth(
        string column,
        int? prefixLength,
        IReadOnlyDictionary<string, SafeMigrationIndexColumnPhysicalShape>? columns
    )
    {
        if (columns is null
            || !columns.TryGetValue(column, out var shape))
        {
            return KeyWidth.Unverifiable;
        }

        if (prefixLength is not { } prefix)
        {
            return shape.FullWidthBytes is { } fullWidth
                ? KeyWidth.Feasible(fullWidth, unprefixedVariableWidth: shape.SupportsPrefix)
                : shape.SupportsPrefix
                    ? KeyWidth.MissingRequiredPrefix
                    : KeyWidth.Unverifiable;
        }

        if (!shape.SupportsPrefix
            || shape.MaximumPrefixUnits is not { } maximumPrefixUnits
            || shape.BytesPerPrefixUnit <= 0)
        {
            return KeyWidth.Unverifiable;
        }

        if (prefix > maximumPrefixUnits)
        {
            return KeyWidth.PrefixExceedsTargetColumn;
        }

        return KeyWidth.Feasible(
            prefix * shape.BytesPerPrefixUnit,
            unprefixedVariableWidth: false);
    }

    private static KeyWidth VariableWidth(
        int? prefixLength,
        int? declaredLength,
        int bytesPerUnit,
        bool hasUnboundedStorage
    )
    {
        if (prefixLength is { } prefix)
        {
            if (declaredLength is not null
                && prefix > declaredLength)
            {
                return KeyWidth.PrefixExceedsTargetColumn;
            }

            return KeyWidth.Feasible(
                checked((long)prefix * bytesPerUnit),
                unprefixedVariableWidth: false);
        }

        if (hasUnboundedStorage || declaredLength is null)
        {
            return KeyWidth.MissingRequiredPrefix;
        }

        return KeyWidth.Feasible(
            checked((long)declaredLength.Value * bytesPerUnit),
            unprefixedVariableWidth: true);
    }

    private static long? GetScalarWidth(
        StoreTypeShape type,
        int? precision,
        int? scale,
        int? dateTimePrecision
    ) => type.Name switch
    {
        "tinyint" => 1,
        "smallint" => 2,
        "mediumint" => 3,
        "int" or "integer" => 4,
        "bigint" => 8,
        "float" => (type.FirstArgument ?? precision) <= 24 ? 4 : 8,
        "double" or "real" => 8,
        "decimal" or "numeric" => DecimalWidth(
            type.FirstArgument ?? precision,
            type.SecondArgument ?? scale),
        "bit" => DivideRoundUp(type.FirstArgument ?? precision, 8),
        "date" => 3,
        "year" => 1,
        "time" => 3 + FractionalSeconds(type.FirstArgument ?? dateTimePrecision),
        "datetime" => 5 + FractionalSeconds(type.FirstArgument ?? dateTimePrecision),
        "timestamp" => 4 + FractionalSeconds(type.FirstArgument ?? dateTimePrecision),
        "enum" => 2,
        "set" => 8,
        _ => null,
    };

    private static long BytesPerUnit(
        long? maximumLength,
        long? octetLength
    )
    {
        if (maximumLength is not > 0
            || octetLength is not > 0)
        {
            return 0;
        }

        return (octetLength.Value + maximumLength.Value - 1) / maximumLength.Value;
    }

    private static long? DecimalWidth(
        int? precision,
        int? scale
    )
    {
        if (precision is null || scale is null || scale > precision)
        {
            return null;
        }

        return DecimalDigitsWidth(precision.Value - scale.Value) + DecimalDigitsWidth(scale.Value);
    }

    private static int DecimalDigitsWidth(
        int digits
    ) => (4 * (digits / 9)) + RemainingDecimalDigitsWidth(digits % 9);

    private static int RemainingDecimalDigitsWidth(
        int digits
    ) => digits switch
    {
        0 => 0,
        1 or 2 => 1,
        3 or 4 => 2,
        5 or 6 => 3,
        _ => 4,
    };

    private static int? DivideRoundUp(
        int? value,
        int divisor
    ) => value is null ? null : (value.Value + divisor - 1) / divisor;

    private static int FractionalSeconds(
        int? precision
    ) => precision.GetValueOrDefault() switch
    {
        0 => 0,
        1 or 2 => 1,
        3 or 4 => 2,
        _ => 3,
    };

    private static bool IsCharacter(
        string type
    ) => type is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext";

    private static bool IsText(
        string type
    ) => type is "tinytext" or "text" or "mediumtext" or "longtext";

    private static bool IsBinary(
        string type
    ) => type is "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob";

    private static bool IsBlob(
        string type
    ) => type is "tinyblob" or "blob" or "mediumblob" or "longblob";

    private readonly record struct KeyWidth(
        MySqlProjectedIndexPhysicalShapeResult Result,
        long Bytes,
        bool UnprefixedVariableWidth
    )
    {
        public static KeyWidth MissingRequiredPrefix { get; } = new(
            MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix,
            Bytes: 0,
            UnprefixedVariableWidth: false);

        public static KeyWidth PrefixExceedsTargetColumn { get; } = new(
            MySqlProjectedIndexPhysicalShapeResult.PrefixExceedsTargetColumn,
            Bytes: 0,
            UnprefixedVariableWidth: false);

        public static KeyWidth Unverifiable { get; } = new(
            MySqlProjectedIndexPhysicalShapeResult.Unverifiable,
            Bytes: 0,
            UnprefixedVariableWidth: false);

        public static KeyWidth Feasible(
            long bytes,
            bool unprefixedVariableWidth
        ) => new(
            MySqlProjectedIndexPhysicalShapeResult.Feasible,
            bytes,
            unprefixedVariableWidth);
    }

    private readonly record struct StoreTypeShape(
        string Name,
        int? FirstArgument,
        int? SecondArgument
    )
    {
        public static StoreTypeShape Parse(
            string storeType
        )
        {
            var value = storeType.AsSpan().Trim();
            var nameEnd = value.IndexOfAny('(', ' ');
            var name = (nameEnd < 0 ? value : value[..nameEnd]).ToString().ToLowerInvariant();
            if (nameEnd < 0 || value[nameEnd] != '(')
            {
                return new StoreTypeShape(name, FirstArgument: null, SecondArgument: null);
            }

            var closing = value[(nameEnd + 1)..].IndexOf(')');
            if (closing < 0)
            {
                return new StoreTypeShape(name, FirstArgument: null, SecondArgument: null);
            }

            var arguments = value.Slice(nameEnd + 1, closing);
            var separator = arguments.IndexOf(',');
            var first = separator < 0 ? arguments : arguments[..separator];
            var second = separator < 0 ? default : arguments[(separator + 1)..];

            return new StoreTypeShape(
                name,
                ParseInteger(first),
                separator < 0 ? null : ParseInteger(second));
        }

        private static int? ParseInteger(
            ReadOnlySpan<char> value
        ) => int.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
