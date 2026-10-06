namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Retains bounded catalog shapes for cumulative ordered column transitions.</summary>
/// <param name="columns">The original physical column shapes, keyed by exact column name.</param>
/// <param name="indexes">The original ordered physical index parts.</param>
/// <param name="environment">The captured storage limits for the original table.</param>
internal sealed partial class MySqlProjectedAlterPhysicalSnapshot(
    IReadOnlyDictionary<string, MySqlAlterColumnPhysicalShape> columns,
    IReadOnlyList<MySqlAlterIndexPhysicalShape> indexes,
    SafeMigrationIndexPhysicalEnvironment environment
)
{
    /// <summary>Checks the complete physical row and keys after overlaying accepted changes.</summary>
    /// <param name="intent">The current exact-source transition.</param>
    /// <param name="table">The accepted columns and keys in the ordered stream.</param>
    /// <param name="mappings">The active provider mappings.</param>
    /// <returns>Whether the cumulative target has proven physical capacity.</returns>
    internal bool IsSafe(
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        IRelationalTypeMappingSource mappings
    )
    {
        if (environment.MaximumKeyBytes <= 0
            || !columns.ContainsKey(intent.Definition.Name))
        {
            return false;
        }

        var rowBytes = 0L;
        var inlineBytes = 0L;
        var nullableColumns = 0;
        foreach (var name in columns.Keys)
        {
            if (!TryGetColumn(name, intent, table, mappings, out var shape))
            {
                return false;
            }

            rowBytes += shape.DeclaredRowBytes;
            if (!TryGetInlineBytes(
                    shape,
                    IsClusteredKeyColumn(name, intent, table, mappings),
                    environment,
                    out var inline))
            {
                return false;
            }

            inlineBytes += inline;
            nullableColumns += shape.IsNullable ? 1 : 0;
        }

        // WHY: Convergence may have added columns that were absent from the
        // immutable catalog. Their contribution must not disappear merely
        // because this transition's exact source column existed beforehand.
        foreach (var definition in table.Columns)
        {
            if (columns.ContainsKey(definition.Name))
            {
                continue;
            }

            if (!TryCreateProjectedColumn(definition, mappings, 4, out var shape))
            {
                return false;
            }

            rowBytes += shape.DeclaredRowBytes;
            if (!TryGetInlineBytes(
                    shape,
                    IsClusteredKeyColumn(definition.Name, intent, table, mappings),
                    environment,
                    out var inline))
            {
                return false;
            }

            inlineBytes += inline;
            nullableColumns += shape.IsNullable ? 1 : 0;
        }

        if (rowBytes + ((nullableColumns + 7L) / 8) > 65535
            || !InlineRowFits(inlineBytes, nullableColumns, environment))
        {
            return false;
        }

        foreach (var index in indexes)
        {
            // FULLTEXT retains its physical representation across the supported
            // text families; it does not use the ordinary InnoDB key-byte cap.
            if (index.IsFullText)
            {
                continue;
            }

            var keyBytes = 0L;
            foreach (var part in index.Parts)
            {
                if (!TryGetKeyBytes(part.Column, part.PrefixLength, intent, table, mappings, out var bytes))
                {
                    return false;
                }

                keyBytes += bytes;
            }

            if (index.Parts.Count > MySqlProjectedIndexPhysicalShape.MaximumKeyParts
                || keyBytes > environment.MaximumKeyBytes)
            {
                return false;
            }
        }

        if (table.PrimaryKey is { } primaryKey
            && !UnprefixedKeyFits(primaryKey.Columns, intent, table, mappings))
        {
            return false;
        }

        foreach (var key in table.UniqueConstraints)
        {
            if (!UnprefixedKeyFits(key.Columns, intent, table, mappings))
            {
                return false;
            }
        }

        foreach (var index in table.Indexes)
        {
            var keyBytes = 0L;
            foreach (var part in index.Keys)
            {
                if (!TryGetKeyBytes(part.Column, part.PrefixLength, intent, table, mappings, out var bytes))
                {
                    return false;
                }

                keyBytes += bytes;
            }

            if (index.Keys.Count > MySqlProjectedIndexPhysicalShape.MaximumKeyParts
                || keyBytes > environment.MaximumKeyBytes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates a conservative declared-row shape from one physical catalog column.</summary>
    /// <param name="dataType">The catalog store-type family without length or precision arguments.</param>
    /// <param name="isNullable">Whether the physical column permits NULL values.</param>
    /// <param name="characterMaximumLength">
    /// The catalog character or prefix-unit limit, or null when unavailable.
    /// </param>
    /// <param name="characterOctetLength">The maximum encoded value width in bytes, or null when unavailable.</param>
    /// <param name="numericPrecision">The catalog numeric precision, or null when inapplicable.</param>
    /// <param name="numericScale">The catalog fractional digit count, or null when inapplicable.</param>
    /// <param name="dateTimePrecision">The fractional-second precision, or null when inapplicable.</param>
    /// <param name="characterBytes">The character-set maximum bytes per character; one for binary values.</param>
    /// <param name="collation">The exact catalog collation name, or null when unavailable.</param>
    /// <param name="characterSet">The exact catalog character-set name, or null when unavailable.</param>
    /// <param name="hasForeignKey">Whether the column participates in an incoming or outgoing foreign key.</param>
    /// <returns>A conservative row/key shape; unproven row widths exceed the SQL row limit.</returns>
    internal static MySqlAlterColumnPhysicalShape CreateCatalogColumn(
        string dataType,
        bool isNullable,
        long? characterMaximumLength,
        long? characterOctetLength,
        int? numericPrecision,
        int? numericScale,
        int? dateTimePrecision,
        int characterBytes,
        string? collation = null,
        string? characterSet = null,
        bool hasForeignKey = false
    )
    {
        var type = dataType.ToLowerInvariant();
        var width = type switch
        {
            "varchar" or "varbinary" => characterOctetLength is { } length
                ? length + (length <= 255 ? 1 : 2) : 65536,
            "char" or "binary" => characterOctetLength ?? 65536,
            "tinyblob" or "tinytext" or "blob" or "text" or "mediumblob" or "mediumtext"
                or "longblob" or "longtext" or "json" or "geometry" => 12,
            "tinyint" => 1,
            "smallint" => 2,
            "mediumint" => 3,
            "int" or "integer" => 4,
            "float" => numericPrecision is > 24 ? 8 : 4,
            "bigint" or "double" => 8,
            // WHY: Separate integer/fractional groups can need one extra byte
            // beyond ceil(total digits / 2). Thirty-six bounds every DECIMAL
            // precision accepted by the engines without undercounting a row.
            "decimal" or "numeric" => 36,
            "bit" => ((numericPrecision ?? 64) + 7) / 8,
            "date" => 3,
            "time" or "datetime" or "timestamp" => 8,
            "year" => 1,
            "enum" => 2,
            "set" => 8,
            _ => 65536,
        };

        var indexShape = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
            dataType, characterMaximumLength, characterOctetLength,
            numericPrecision, numericScale, dateTimePrecision);

        if (type is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext")
        {
            // Catalog byte/character maxima for TEXT can both represent the
            // byte capacity. The character-set MAXLEN remains the safe prefix
            // multiplier, including when a projected TEXT has no length facet.
            indexShape = indexShape with { BytesPerPrefixUnit = characterBytes };
        }

        var dynamicInline = type switch
        {
            "varchar" or "varbinary" => Math.Min(characterOctetLength ?? 65536, 255),
            "char" or "binary" when characterOctetLength >= 768 => 767,
            "tinyblob" or "tinytext" or "blob" or "text" or "mediumblob" or "mediumtext"
                or "longblob" or "longtext" or "json" or "geometry" => 40,
            _ => width,
        };

        var compactInline = type switch
        {
            "varchar" or "varbinary" or "char" or "binary" => characterOctetLength is <= 768
                ? characterOctetLength.Value : 788,
            "tinyblob" or "tinytext" or "blob" or "text" or "mediumblob" or "mediumtext"
                or "longblob" or "longtext" or "json" or "geometry" => 788,
            _ => width,
        };

        return new MySqlAlterColumnPhysicalShape(
            width,
            isNullable,
            characterBytes,
            indexShape,
            collation,
            dynamicInline,
            compactInline,
            type,
            characterSet,
            hasForeignKey);
    }

    /// <summary>Calculates a projected column using its proven encoding, or a conservative new-column bound.</summary>
    /// <param name="definition">The accepted target column definition.</param>
    /// <param name="mappings">The provider mappings used when the store type is implicit.</param>
    /// <param name="characterBytes">The proven or conservative maximum bytes per character.</param>
    /// <param name="shape">The resulting physical shape, or the default value when it cannot be resolved.</param>
    /// <returns>Whether the column has a supported, bounded declared row representation.</returns>
    internal static bool TryCreateProjectedColumn(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappings,
        int characterBytes,
        out MySqlAlterColumnPhysicalShape shape
    )
    {
        var storeType = definition.StoreType ?? mappings.FindMapping(
            definition.ClrType, definition.StoreType, keyOrIndex: false, definition.IsUnicode,
            definition.MaxLength, definition.IsRowVersion, definition.IsFixedLength,
            definition.Precision, definition.Scale)?.StoreType;

        if (storeType is null
            || characterBytes is < 1 or > 4)
        {
            shape = default;

            return false;
        }

        var value = storeType.AsSpan().Trim();
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        var nameEnd = value.IndexOfAny('(', ' ');
        var type = (nameEnd < 0 ? value : value[..nameEnd]).ToString().ToLowerInvariant();
        long? units = null;
        if (type is "varchar" or "char" or "varbinary" or "binary")
        {
            if (open < 0
                || close <= open
                || !int.TryParse(value[(open + 1)..close], CultureInfo.InvariantCulture, out var length)
                || length <= 0)
            {
                shape = default;

                return false;
            }

            units = length;
        }

        var binary = type is "varbinary" or "binary";
        var bytes = binary ? 1 : characterBytes;
        var precision = definition.Precision;
        var scale = definition.Scale;
        if (open >= 0
            && close > open
            && type is not ("enum" or "set"))
        {
            var arguments = value[(open + 1)..close];
            var comma = arguments.IndexOf(',');
            var first = comma < 0 ? arguments : arguments[..comma];
            if (int.TryParse(first.Trim(), CultureInfo.InvariantCulture, out var parsedPrecision))
            {
                precision = parsedPrecision;
            }

            if (comma >= 0
                && int.TryParse(arguments[(comma + 1)..].Trim(), CultureInfo.InvariantCulture, out var parsedScale))
            {
                scale = parsedScale;
            }
        }

        shape = CreateCatalogColumn(
            type, definition.IsNullable, units, units * bytes,
            precision, scale, precision, bytes, definition.Collation?.Name);

        return shape.DeclaredRowBytes <= 65535;
    }

    private bool UnprefixedKeyFits(
        IReadOnlyList<string> keyColumns,
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        IRelationalTypeMappingSource mappings
    )
    {
        var total = 0L;
        foreach (var column in keyColumns)
        {
            if (!TryGetKeyBytes(column, null, intent, table, mappings, out var bytes))
            {
                return false;
            }

            total += bytes;
        }

        return keyColumns.Count <= MySqlProjectedIndexPhysicalShape.MaximumKeyParts
            && total <= environment.MaximumKeyBytes;
    }

    /// <summary>Bounds one in-page field including its directory entry under the captured row format.</summary>
    /// <param name="shape">The declared physical column shape.</param>
    /// <param name="primaryKey">Whether the field belongs to a retained clustered-key candidate.</param>
    /// <param name="environment">The captured row format and page size.</param>
    /// <param name="bytes">The conservative inline byte count, or zero when unproven.</param>
    /// <returns>Whether the storage format provides a bounded inline representation.</returns>
    internal static bool TryGetInlineBytes(
        MySqlAlterColumnPhysicalShape shape,
        bool primaryKey,
        SafeMigrationIndexPhysicalEnvironment environment,
        out long bytes
    )
    {
        var dynamic = StringComparer.OrdinalIgnoreCase.Equals(environment.StorageRowFormat, "Dynamic");
        var compact = StringComparer.OrdinalIgnoreCase.Equals(environment.StorageRowFormat, "Compact")
            || StringComparer.OrdinalIgnoreCase.Equals(environment.StorageRowFormat, "Redundant");

        if ((!dynamic
                && !compact)
            || environment.StoragePageSize is not (4096 or 8192 or 16384 or 32768 or 65536)
            || primaryKey
                && shape.IndexShape.FullWidthBytes is null)
        {
            bytes = 0;

            return false;
        }

        // WHY: Primary keys cannot move to overflow pages. Other long values
        // either retain their row-format prefix or a 20-byte external pointer;
        // short values below the overflow threshold still need their full
        // bytes. Two directory bytes per field cover both compact and legacy
        // formats without relying on actual row contents.
        bytes = (primaryKey ? shape.DeclaredRowBytes
            : dynamic ? shape.DynamicInlineBytes : shape.CompactInlineBytes) + 2;

        return true;
    }

    /// <summary>Applies a conservative in-page record ceiling, independently of the 65,535-byte SQL limit.</summary>
    /// <param name="fieldBytes">The sum of inline value and directory bytes for every field.</param>
    /// <param name="nullableColumns">The number of fields requiring NULL-bitmap bits.</param>
    /// <param name="environment">The captured physical page size.</param>
    /// <returns>Whether the row fits below the reserved in-page ceiling.</returns>
    internal static bool InlineRowFits(
        long fieldBytes,
        int nullableColumns,
        SafeMigrationIndexPhysicalEnvironment environment
    )
    {
        if (environment.StoragePageSize is not (4096 or 8192 or 16384 or 32768 or 65536))
        {
            return false;
        }

        // WHY: InnoDB requires an in-page record below half a page (16 KiB for
        // 64 KiB pages). The 256-byte reserve exceeds the documented fixed
        // record/system-field overhead; field directories and NULL bits are
        // accounted separately. COMPRESSED needs its own KEY_BLOCK_SIZE proof
        // and is rejected by TryGetInlineBytes rather than guessed here.
        var maximum = Math.Min(environment.StoragePageSize.Value / 2, 16384) - 256;

        return fieldBytes + ((nullableColumns + 7L) / 8) <= maximum;
    }

    private bool IsClusteredKeyColumn(
        string name,
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        IRelationalTypeMappingSource mappings
    )
    {
        if (table.PrimaryKey?.Columns.Contains(name, StringComparer.Ordinal) == true)
        {
            return true;
        }

        foreach (var index in indexes)
        {
            if (index.IsPrimary
                && index.Parts.Any(part => StringComparer.Ordinal.Equals(part.Column, name)))
            {
                return true;
            }
        }

        // WHY: Without a primary key InnoDB clusters the first all-NOT-NULL
        // unique key. Retaining every eligible candidate avoids guessing its
        // physical creation order after ordered key/column changes. A retained
        // snapshot PRIMARY may have been dropped, so it cannot suppress these
        // alternative clustered candidates in the projected stream.
        foreach (var index in indexes)
        {
            if (index.IsUnique
                && index.Parts.Any(part => StringComparer.Ordinal.Equals(part.Column, name))
                && index.Parts.All(part => part.Column is not null
                    && TryGetColumn(part.Column, intent, table, mappings, out var shape)
                    && !shape.IsNullable))
            {
                return true;
            }
        }

        return IsProjectedClusteredKeyColumn(name, intent, table);
    }

    /// <summary>Identifies columns that cannot overflow in an accepted clustered-key candidate.</summary>
    /// <param name="name">The exact column name to classify.</param>
    /// <param name="intent">The current transition, including its target nullability.</param>
    /// <param name="table">The accepted table columns and keys.</param>
    /// <returns>Whether an accepted primary or eligible unique key retains this field inline.</returns>
    internal static bool IsProjectedClusteredKeyColumn(
        string name,
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table
    )
    {
        var target = intent.Definition;
        if (table.PrimaryKey?.Columns.Contains(name, StringComparer.Ordinal) == true)
        {
            return true;
        }

        foreach (var key in table.UniqueConstraints)
        {
            if (key.Columns.Contains(name, StringComparer.Ordinal)
                && key.Columns.All(IsRequired))
            {
                return true;
            }
        }

        foreach (var key in table.Indexes)
        {
            if (key.Unique
                && key.Keys.Any(part => StringComparer.Ordinal.Equals(part.Column, name))
                && key.Keys.All(part => part.Column is not null
                    && IsRequired(part.Column)))
            {
                return true;
            }
        }

        return false;

        bool IsRequired(string column) => StringComparer.Ordinal.Equals(column, target.Name)
            ? !target.IsNullable
            : table.TryGetProjectedColumn(intent.Table, intent.Schema, column, out var definition)
                && !definition.IsNullable;
    }

    private bool TryGetKeyBytes(
        string? name,
        int? prefix,
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        IRelationalTypeMappingSource mappings,
        out long bytes
    )
    {
        bytes = 0;
        if (name is null
            || !TryGetColumn(name, intent, table, mappings, out var shape))
        {
            return false;
        }

        if (prefix is { } units)
        {
            if (!shape.IndexShape.SupportsPrefix
                || units <= 0
                || shape.IndexShape.MaximumPrefixUnits is { } maximum
                    && units > maximum)
            {
                return false;
            }

            bytes = units * shape.IndexShape.BytesPerPrefixUnit;

            return bytes > 0;
        }

        if (shape.IndexShape.FullWidthBytes is not { } fullWidth)
        {
            return false;
        }

        bytes = fullWidth;

        return true;
    }

    private bool TryGetColumn(
        string name,
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        IRelationalTypeMappingSource mappings,
        out MySqlAlterColumnPhysicalShape shape
    )
    {
        var exists = columns.TryGetValue(name, out shape);
        var definition = StringComparer.Ordinal.Equals(name, intent.Definition.Name)
            ? intent.Definition
            : table.TryGetProjectedColumn(intent.Table, intent.Schema, name, out var accepted) ? accepted : null;

        if (definition is null)
        {
            return exists;
        }

        // WHY: An accepted sibling alteration may have changed its encoding.
        // Only an explicit matching collation proves that the captured MAXLEN
        // still applies. The current target has a separate exact-source and
        // target-collation proof in its source-bound provider analysis.
        var retainsEncoding = exists
            && (StringComparer.Ordinal.Equals(name, intent.Definition.Name)
            || definition.Collation is not null
                && definition.Collation.Schema is null
                && StringComparer.OrdinalIgnoreCase.Equals(definition.Collation.Name, shape.Collation));

        return TryCreateProjectedColumn(definition, mappings, retainsEncoding ? shape.CharacterBytes : 4, out shape);
    }
}

/// <summary>Stores physical row and index contributions without retaining user data.</summary>
/// <param name="DeclaredRowBytes">
/// The declared SQL row contribution in bytes; an excessive value means unproven.
/// </param>
/// <param name="IsNullable">Whether the original column permits NULL values.</param>
/// <param name="CharacterBytes">The captured maximum encoded bytes per character.</param>
/// <param name="IndexShape">The catalog-derived key width and prefix-unit limits.</param>
/// <param name="Collation">The captured collation identity, or null when unavailable.</param>
/// <param name="DynamicInlineBytes">The DYNAMIC value contribution in bytes, excluding directory overhead.</param>
/// <param name="CompactInlineBytes">The COMPACT/REDUNDANT value contribution, excluding directory overhead.</param>
/// <param name="StoreTypeFamily">The normalized catalog type family without arguments.</param>
/// <param name="CharacterSet">The captured character-set identity, or null when unavailable.</param>
/// <param name="HasForeignKey">Whether a live incoming or outgoing foreign key references the column.</param>
internal readonly record struct MySqlAlterColumnPhysicalShape(
    long DeclaredRowBytes,
    bool IsNullable,
    int CharacterBytes,
    SafeMigrationIndexColumnPhysicalShape IndexShape,
    string? Collation,
    long DynamicInlineBytes,
    long CompactInlineBytes,
    string StoreTypeFamily,
    string? CharacterSet,
    bool HasForeignKey
);

/// <summary>Stores ordered live index parts, including unresolvable expression markers.</summary>
/// <param name="IsFullText">Whether the index uses FULLTEXT rather than ordinary key-byte limits.</param>
/// <param name="Parts">The ordered captured parts, retaining null column names for unresolvable expressions.</param>
/// <param name="IsPrimary">Whether the physical index is the primary key.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
internal sealed record MySqlAlterIndexPhysicalShape(
    bool IsFullText,
    IReadOnlyList<MySqlAlterIndexPart> Parts,
    bool IsPrimary = false,
    bool IsUnique = false
);

/// <summary>Stores one live index column and its optional prefix in character units.</summary>
/// <param name="Column">The exact column name, or null for an unresolvable expression part.</param>
/// <param name="PrefixLength">The character/binary-byte prefix units, or null for an unprefixed key.</param>
internal readonly record struct MySqlAlterIndexPart(
    string? Column,
    int? PrefixLength
);
