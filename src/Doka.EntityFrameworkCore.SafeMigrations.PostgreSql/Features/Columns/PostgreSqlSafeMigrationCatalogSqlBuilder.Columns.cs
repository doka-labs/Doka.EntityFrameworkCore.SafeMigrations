namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private string? GetUnsupportedColumnFeature(
        SafeMigrationIntent intent
    )
    {
        var definitions = intent switch
        {
            EnsureTableIntent value => value.Definition.Columns,
            EnsureColumnIntent value => [value.Definition],
            AlterColumnIntent value => [value.Definition],
            _ => [],
        };

        if (definitions.Any(definition => !CanMap(definition)))
        {
            return "column_type_mapping";
        }

        return definitions.Any(static definition =>
            definition.ComputedColumnSql is not null && definition.IsStored == false)
            ? "virtual_generated_column"
            : null;
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureColumn(
        EnsureColumnIntent intent
    )
    {
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var unsafeAdd = intent.Definition is
        {
            IsNullable: false,
            DefaultValue.Kind: SafeMigrationDefaultValueKind.None,
            ComputedColumnSql: null,
        };

        var dataBlocked = unsafeAdd
            ? $"EXISTS (SELECT 1 FROM {Qualified(intent.Table, intent.Schema)} LIMIT 1)"
            : "FALSE";

        return Plan(
            $"CASE WHEN NOT {table} THEN 'data_blocked' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' WHEN {matching} THEN 'matching' "
            + "ELSE 'different' END",
            matching);
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropColumn(
        DropColumnIntent intent
    )
    {
        var relation = RelationExists(intent.Table, intent.Schema);
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {relation} THEN 'missing' WHEN NOT {table} THEN 'different' "
            + $"WHEN {exists} THEN 'matching' ELSE 'missing' END",
            $"NOT {exists}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildRenameColumn(
        RenameColumnIntent intent
    )
    {
        var table = TableExists(intent.Table, intent.Schema);
        var source = ColumnExists(intent.Table, intent.Schema, intent.Name);
        var target = ColumnExists(intent.Table, intent.Schema, intent.NewName);

        return Plan(
            $"CASE WHEN NOT {source} THEN 'missing' WHEN NOT {table} THEN 'different' "
            + $"WHEN {target} THEN 'different' ELSE 'matching' END",
            $"NOT {source}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildAlterColumn(
        AlterColumnIntent intent
    )
    {
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var repair = intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        var repairPrecondition = repair == SafeMigrationRepairCapability.Safe
            ? ColumnMatches(intent.Table, intent.Schema, intent.OldDefinition!)
            : "FALSE";

        var nullBlocked =
            repair == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
                ? $"({repairPrecondition}) AND EXISTS (SELECT 1 FROM {Qualified(intent.Table, intent.Schema)} WHERE "
                + $"{_sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name)} IS NULL LIMIT 1)"
                : "FALSE";

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'different' WHEN {matching} THEN 'matching' "
            + $"WHEN {nullBlocked} THEN 'data_blocked' ELSE 'different' END",
            matching,
            repair,
            repairPrecondition);
    }

    private string ColumnMatches(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        int? ordinal = null
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?? throw new InvalidOperationException(
                $"No PostgreSQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var conditions = new List<string>
        {
            $"pg_catalog.format_type(a.atttypid, a.atttypmod) = {Literal(storeType)}",
            $"a.attnotnull = {(!definition.IsNullable).ToString().ToUpperInvariant()}",
            CollationMatches(definition),
            $"pg_catalog.col_description(c.oid, a.attnum) IS NOT DISTINCT FROM "
            + (definition.Comment is null ? "NULL" : Literal(definition.Comment)),
            DefaultAndGenerationMatches(definition, mapping),
        };

        if (ordinal is not null)
        {
            conditions.Add($"a.attnum = {ordinal.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
            + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum "
            + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
            + $"AND a.attname = {Literal(definition.Name)} AND a.attnum > 0 AND NOT a.attisdropped "
            + $"AND {string.Join(" AND ", conditions)})";
    }

    private string CollationMatches(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.Collation is null)
        {
            return "TRUE";
        }

        return "EXISTS (SELECT 1 FROM pg_catalog.pg_collation coll "
            + $"WHERE coll.oid = a.attcollation AND coll.collname = {Literal(definition.Collation)})";
    }

    private string DefaultAndGenerationMatches(
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping
    )
    {
        if (definition.ComputedColumnSql is not null)
        {
            var generation = definition.IsStored == false ? "'v'" : "'s'";

            return $"a.attgenerated = {generation} AND d.oid IS NOT NULL AND "
                + ExpressionMatches("pg_catalog.pg_get_expr(d.adbin, d.adrelid)", definition.ComputedColumnSql);
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
        {
            return "a.attgenerated = '' AND d.oid IS NULL";
        }

        var expected = definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql
            ? definition.DefaultValue.SqlExpression!
            : mapping.GenerateSqlLiteral(definition.DefaultValue.GetLiteralValue());

        var expressionMatches = definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? LiteralDefaultMatches(
                "pg_catalog.pg_get_expr(d.adbin, d.adrelid)",
                expected,
                definition.DefaultValue.GetLiteralValue())
            : ExpressionMatches("pg_catalog.pg_get_expr(d.adbin, d.adrelid)", expected);

        return "a.attgenerated = '' AND d.oid IS NOT NULL AND " + expressionMatches;
    }

    private bool CanMap(
        ExpectedColumnDefinition definition
    )
    {
        try
        {
            return _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private string ColumnExists(
        string table,
        string? schema,
        string column
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
        + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND a.attname = {Literal(column)} AND a.attnum > 0 AND NOT a.attisdropped)";

    private string LiteralDefaultMatches(
        string catalogExpression,
        string expected,
        object? value
    )
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { expected };
        AddLiteralDefaultCandidates(candidates, value);
        var conditions = new List<string>();
        foreach (var candidate in candidates)
        {
            var castPrefix = Literal($"{candidate}::");
            var castExpression = $"({castPrefix} || pg_catalog.format_type(a.atttypid, NULL))";
            conditions.Add($"{catalogExpression} = {Literal(candidate)}");
            conditions.Add($"{catalogExpression} = {Literal($"({candidate})")}");
            conditions.Add($"{catalogExpression} = {castExpression}");
            conditions.Add($"{catalogExpression} = ('(' || {castExpression} || ')')");
        }

        return $"({string.Join(" OR ", conditions)})";
    }

    private static void AddLiteralDefaultCandidates(
        HashSet<string> candidates,
        object? value
    )
    {
        switch (value)
        {
            case null:
                candidates.Add("NULL");
                break;
            case bool boolean:
                candidates.Add(boolean ? "true" : "false");
                break;
            case byte[] bytes:
                candidates.Add($"'\\x{Convert.ToHexStringLower(bytes)}'");
                break;
            case string text:
                candidates.Add(QuoteStoredLiteral(text));
                break;
            case char character:
                var charLiteral = QuoteStoredLiteral(character.ToString());
                candidates.Add(charLiteral);
                candidates.Add($"{charLiteral}::bpchar");
                break;
            case Guid guid:
                candidates.Add(QuoteStoredLiteral(guid.ToString("D")));
                break;
            case DateOnly date:
                candidates.Add(QuoteStoredLiteral(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                break;
            case TimeOnly time:
                candidates.Add(QuoteStoredLiteral(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)));
                break;
            case DateTime dateTime:
                candidates.Add(
                    QuoteStoredLiteral(dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)));
                break;
            case DateTimeOffset dateTimeOffset:
                var utc = dateTimeOffset.ToUniversalTime();
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:sszz", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss.ffffffzz", CultureInfo.InvariantCulture)));
                break;
            case TimeSpan timeSpan:
                candidates.Add(QuoteStoredLiteral(timeSpan.ToString("c", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)));
                break;
            case Enum enumeration:
                candidates.Add(
                    Convert.ToString(
                        Convert.ChangeType(
                            enumeration,
                            Enum.GetUnderlyingType(enumeration.GetType()),
                            CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture)!);
                break;
            default:
                var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (invariant is not null)
                {
                    candidates.Add(invariant);
                    AddNumericParserCandidates(candidates, value, invariant);
                }

                break;
        }
    }

    private static void AddNumericParserCandidates(
        HashSet<string> candidates,
        object value,
        string invariant
    )
    {
        string[] casts = value switch
        {
            sbyte or byte or short or ushort =>
            [
                "smallint",
                "integer",
            ],
            int or uint =>
            [
                "integer",
                "bigint",
            ],
            long or ulong =>
            [
                "bigint",
                "numeric",
            ],
            decimal => ["numeric"],
            float =>
            [
                "real",
                "double precision",
            ],
            double =>
            [
                "double precision",
                "numeric",
            ],
            _ => [],
        };

        if (casts.Length == 0)
        {
            return;
        }

        var quoted = QuoteStoredLiteral(invariant);
        candidates.Add(quoted);
        foreach (var cast in casts)
        {
            candidates.Add($"{quoted}::{cast}");
        }
    }

    private static string QuoteStoredLiteral(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
