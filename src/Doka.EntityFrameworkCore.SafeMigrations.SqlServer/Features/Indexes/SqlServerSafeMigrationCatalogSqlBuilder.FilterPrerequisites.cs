namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Preserves the SQL literal encoding of a parsed raw filtered-index predicate.</summary>
    /// <param name="definition">The immutable authored index contract.</param>
    /// <returns>The structured predicate, including raw ANSI versus Unicode constant types.</returns>
    internal static SafeMigrationSqlExpression? GetStructuredIndexFilter(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.StructuredFilter is { } structured)
        {
            return structured;
        }

        if (definition.Filter is not { } sql || sql.Length > SafeMigrationSqlExpressionParser.MaximumLength)
        {
            return null;
        }

        // WHY: The provider-neutral parser does not accept SQL Server's N
        // prefix. Remove it only from a bounded parsing copy, retaining each
        // literal's source encoding without rewriting the baseline predicate.
        var strings = new Queue<string>();
        var parsingCopy = sql.ToCharArray();
        for (var position = 0; position < sql.Length; position++)
        {
            if (sql[position] == '`')
            {
                return null;
            }

            if (sql[position] is '[' or '"')
            {
                var terminator = sql[position] == '[' ? ']' : '"';
                while (++position < sql.Length)
                {
                    if (sql[position] != terminator)
                    {
                        continue;
                    }

                    if (position + 1 < sql.Length && sql[position + 1] == terminator)
                    {
                        position++;
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (sql[position] != '\'')
            {
                continue;
            }

            var unicode = position > 0 && sql[position - 1] is 'n' or 'N';
            strings.Enqueue(unicode ? "nvarchar(max)" : "varchar(max)");
            if (unicode)
            {
                parsingCopy[position - 1] = ' ';
            }

            while (++position < sql.Length)
            {
                if (sql[position] != '\'')
                {
                    continue;
                }

                if (position + 1 < sql.Length && sql[position + 1] == '\'')
                {
                    position++;
                    continue;
                }

                break;
            }
        }

        if (!SafeMigrationSqlExpressionParser.TryParse(new string(parsingCopy), out var parsed)
            || !IsSupportedIndexFilter(parsed))
        {
            return null;
        }

        return PreserveLiteralType(parsed);

        SafeMigrationSqlExpression PreserveLiteralType(SafeMigrationSqlExpression expression) => expression switch
        {
            SafeMigrationSqlLiteralExpression { Value: string } literal
                => new SafeMigrationSqlLiteralExpression(literal.Value, strings.Dequeue()),
            SafeMigrationSqlBinaryExpression binary => new SafeMigrationSqlBinaryExpression(
                PreserveLiteralType(binary.Left), binary.Operator, PreserveLiteralType(binary.Right)),
            SafeMigrationSqlInExpression predicate => new SafeMigrationSqlInExpression(
                predicate.Operand, predicate.Values.Select(PreserveLiteralType), predicate.Negated),
            _ => expression,
        };
    }

    private static IEnumerable<string> IndexFilterColumns(
        ExpectedIndexDefinition definition
    ) => GetStructuredIndexFilter(definition) is { } filter && IsSupportedIndexFilter(filter)
        ? GetIndexFilterComparisons(filter).Select(static comparison => comparison.Column)
        : [];

    private string? BuildIndexFilterSupportExpression(
        ExpectedIndexDefinition definition
    )
    {
        var filter = GetStructuredIndexFilter(definition);
        if (filter is null)
        {
            return null;
        }

        var predicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (column, literal) in GetIndexFilterComparisons(filter))
        {
            var conversion = literal is null ? "1 = 1" : BuildPhysicalFilterLiteralProof(literal);
            var physical = "EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
                + $"WHERE c.object_id = {TableId(definition.Table, definition.Schema)} AND c.name = {Literal(column)} "
                + "AND c.is_computed = 0 AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0 "
                + "AND ty.name IN (N'bigint', N'binary', N'bit', N'char', N'date', N'datetime', N'datetime2', "
                + "N'datetimeoffset', N'decimal', N'float', N'int', N'money', N'nchar', N'numeric', N'nvarchar', "
                + "N'real', N'smalldatetime', N'smallint', N'smallmoney', N'time', N'tinyint', N'uniqueidentifier', "
                + $"N'varbinary', N'varchar') AND ({conversion}))";

            // WHY: A missing predicate column remains an ordered prerequisite;
            // an existing forbidden column or invalid constant is immutable
            // Unsupported and must not be promoted to Missing by projection.
            predicates.Add($"(NOT {ColumnExists(definition.Table, definition.Schema, column)} OR {physical})");
        }

        return Bit(string.Join(" AND ", predicates));
    }

    private string BuildPhysicalFilterLiteralProof(
        SafeMigrationSqlLiteralExpression literal
    )
    {
        var predicates = new List<string>();
        string[] scalarTypes =
        [
            "bigint", "bit", "date", "datetime", "float", "int", "money", "real",
            "smalldatetime", "smallint", "smallmoney", "tinyint", "uniqueidentifier",
        ];

        foreach (var type in scalarTypes)
        {
            Add(type, type);
        }

        foreach (var type in new[] { "datetime2", "datetimeoffset", "time" })
        {
            for (var scale = 0; scale <= 7; scale++)
            {
                Add(type, type + "(" + scale.ToString(CultureInfo.InvariantCulture) + ")",
                    $"c.scale = {scale.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        foreach (var type in new[] { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" })
        {
            var canonical = type is "char" or "nchar" or "binary" ? type + "(8000)" : type + "(max)";
            if (type == "nchar")
            {
                canonical = "nchar(4000)";
            }

            var size = literal.Value switch
            {
                string text when type is "nchar" or "nvarchar"
                    => (text.Length * 2).ToString(CultureInfo.InvariantCulture),
                char when type is "nchar" or "nvarchar" => "2",
                byte[] bytes => bytes.Length.ToString(CultureInfo.InvariantCulture),
                _ => $"DATALENGTH(CONVERT(varchar(max), {ManagedValueLiteral(literal.Value, canonical)}))",
            };

            var capacity = $"(c.max_length = -1 OR {size} <= c.max_length)";
            if (IsAnsiManagedStoreType(canonical) && literal.Value is string or char)
            {
                var text = literal.Value is string value ? value : ((char)literal.Value).ToString();
                if (text.Any(static character => character > 127))
                {
                    capacity += " AND c.collation_name = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";
                }

                capacity += " AND (" + ManagedAnsiValueRepresentationGuard(literal.Value, canonical) + ")";
            }

            Add(type, canonical, capacity);
        }

        if (TryGetFilterDecimalValue(literal.Value, out var numeric))
        {
            for (var scale = 0; scale <= 38; scale++)
            {
                var precision = Math.Max(1, scale);
                while (precision <= 38 && !IsManagedValueRepresentable(numeric,
                    $"decimal({precision.ToString(CultureInfo.InvariantCulture)},"
                        + $"{scale.ToString(CultureInfo.InvariantCulture)})"))
                {
                    precision++;
                }

                if (precision <= 38)
                {
                    var canonical = $"decimal(38,{scale.ToString(CultureInfo.InvariantCulture)})";
                    var capacity = $"c.scale = {scale.ToString(CultureInfo.InvariantCulture)} "
                        + $"AND c.precision >= {precision.ToString(CultureInfo.InvariantCulture)}";

                    Add("decimal", canonical, capacity);
                    Add("numeric", canonical, capacity);
                }
            }
        }

        return predicates.Count == 0 ? "1 = 0" : string.Join(" OR ", predicates);

        void Add(
            string type,
            string canonical,
            string additional = "1 = 1"
        )
        {
            if (!FilterLiteralHasCompatibleScalarFamily(literal.Value, type)
                || !FilterSourceHasCompatibleScalarFamily(EmittedIndexFilterLiteralStoreType(literal), type)
                || !IsIndexFilterLiteralCompatible(literal, canonical)
                || literal.Value is string { Length: > 4_000 } && !IsCharacterManagedStoreType(canonical))
            {
                return;
            }

            var source = NonThrowingDefaultLiteral(literal, canonical);
            var intermediate = literal.StoreType is null ? "1 = 1"
                : "(" + ManagedValueRepresentationGuard(literal.Value, literal.StoreType) + ") AND ("
                    + ManagedAnsiValueRepresentationGuard(literal.Value, literal.StoreType) + ")";

            predicates.Add($"(ty.name = {Literal(type)} AND ({additional}) AND ({intermediate}) "
                + $"AND ({ManagedValueRepresentationGuard(literal.Value, canonical)}) "
                + $"AND {NonThrowingScalarConversion(source, literal.Value, canonical)} IS NOT NULL)");
        }
    }

    private static bool FilterSourceHasCompatibleScalarFamily(
        string source,
        string target
    ) => source switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" => target is not ("binary" or "varbinary"),
        "binary" or "varbinary" => target is "binary" or "varbinary",
        "uniqueidentifier" => target == "uniqueidentifier",
        "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime"
            => target is "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime",
        "time" => target == "time",
        "bigint" or "bit" or "decimal" or "float" or "int" or "money" or "numeric"
            or "real" or "smallint" or "smallmoney" or "tinyint"
            => target is "bigint" or "bit" or "decimal" or "float" or "int" or "money" or "numeric"
                or "real" or "smallint" or "smallmoney" or "tinyint",
        _ => false,
    };

    private static bool FilterLiteralHasCompatibleScalarFamily(
        object? value,
        string target
    ) => value switch
    {
        string or char => target is not ("binary" or "varbinary"),
        byte[] => target is "binary" or "varbinary",
        Guid => target == "uniqueidentifier",
        DateTime or DateOnly or DateTimeOffset => target is "date" or "datetime" or "datetime2"
            or "datetimeoffset" or "smalldatetime",
        TimeOnly or TimeSpan => target == "time",
        float or double => target is "float" or "real",
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Enum
            => target is "bigint" or "bit" or "decimal" or "float" or "int" or "money" or "numeric"
                or "real" or "smallint" or "smallmoney" or "tinyint",
        _ => false,
    };

    private static bool TryGetFilterDecimalValue(
        object? value,
        out decimal numeric
    )
    {
        if (value is string text)
        {
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out numeric);
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Enum)
        {
            numeric = Convert.ToDecimal(value, CultureInfo.InvariantCulture);

            return true;
        }

        numeric = 0;

        return false;
    }
}
