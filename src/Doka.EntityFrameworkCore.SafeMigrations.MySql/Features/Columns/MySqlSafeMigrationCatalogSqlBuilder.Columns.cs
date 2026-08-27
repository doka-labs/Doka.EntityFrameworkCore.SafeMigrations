namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private const string ValueGenerationStrategyAnnotation = "Doka:MySql:ValueGenerationStrategy";

    private string? GetUnsupportedColumnFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features,
        MySqlServerVersion serverVersion
    )
    {
        var isMariaDb = serverVersion.IsMariaDb;
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

        if (definitions.Any(static definition => definition.Collation?.Schema is not null))
        {
            return "schema_qualified_collation";
        }

        if (definitions.Any(definition => !CanRepresentLiteralDefault(definition, serverVersion)))
        {
            return "literal_default_catalog_representation";
        }

        if (definitions.Any(static definition => definition.ProviderAnnotations.Any(annotation =>
                !StringComparer.Ordinal.Equals(annotation.Name, ValueGenerationStrategyAnnotation)
                || annotation.Value is not (MySqlValueGenerationStrategy.None
                    or MySqlValueGenerationStrategy.AutoIncrement))))
        {
            return "provider_column_annotation";
        }

        if (definitions.Any(definition =>
                (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
                && !Supported(
                    features,
                    definition.IsStored == true
                        ? MySqlMigrationFeature.StoredGeneratedColumns
                        : MySqlMigrationFeature.VirtualGeneratedColumns)))
        {
            return "generated_column";
        }

        return definitions.Any(static definition => definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql)
            && !Supported(features, MySqlMigrationFeature.ExpressionDefaults)
                ? "expression_default"
                : null;
    }

    private MySqlSafeMigrationRuntimePlan BuildEnsureColumn(
        EnsureColumnIntent intent,
        bool isMariaDb
    )
    {
        var tableExists = BaseTableExists(intent.Table);
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var unsafeAdd = !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition);

        var dataBlocked = unsafeAdd ? $"EXISTS (SELECT 1 FROM {Delimited(intent.Table)} LIMIT 1)" : "FALSE";

        return Plan(
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN NOT {columnExists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {columnExists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
    }

    private MySqlSafeMigrationRuntimePlan BuildDropColumn(
        DropColumnIntent intent
    )
    {
        var objectExists = TableExists(intent.Table);
        var tableExists = BaseTableExists(intent.Table);
        var columnExists = ColumnExists(intent.Table, intent.Name);

        return Plan(
            $"CASE WHEN NOT {objectExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN NOT {columnExists} THEN 'missing' ELSE 'matching' END",
            $"NOT {columnExists}");
    }

    private MySqlSafeMigrationRuntimePlan BuildRenameColumn(
        RenameColumnIntent intent
    )
    {
        var tableExists = BaseTableExists(intent.Table);
        var sourceExists = ColumnExists(intent.Table, intent.Name);
        var targetExists = ColumnExists(intent.Table, intent.NewName);

        return Plan(
            $"CASE WHEN NOT {sourceExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN {targetExists} THEN 'different' ELSE 'matching' END",
            $"NOT {sourceExists}");
    }

    private MySqlSafeMigrationRuntimePlan BuildAlterColumn(
        AlterColumnIntent intent,
        bool isMariaDb
    )
    {
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var repairCapability = intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)
            : "FALSE";

        var nullBlocked =
            repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
                ? $"({repairPrecondition}) AND EXISTS (SELECT 1 FROM {Delimited(intent.Table)} WHERE "
                + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)"
                : "FALSE";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {columnExists} THEN 'different' "
            + $"WHEN {matching} THEN 'matching' "
            + $"WHEN {nullBlocked} THEN 'data_blocked' ELSE 'different' END",
            matching,
            repairCapability,
            repairPrecondition);
    }

    private string BuildColumnMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        int? ordinal = null
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?? throw new InvalidOperationException(
                $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var conditions = new List<string>
        {
            BuildStoreTypeMatches(storeType, isMariaDb),
            $"c.IS_NULLABLE = {(definition.IsNullable ? "'YES'" : "'NO'")}",
            BuildCollationMatches(table, definition.Collation),
            $"COALESCE(c.COLUMN_COMMENT, '') = {Literal(definition.Comment ?? string.Empty)}",
            BuildDefaultMatches("c.COLUMN_DEFAULT", definition.DefaultValue, definition.IsNullable, mapping),
            BuildComputedMatches(definition, isMariaDb),
            BuildValueGenerationMatches(definition),
        };

        if (ordinal is not null)
        {
            conditions.Add($"c.ORDINAL_POSITION = {ordinal.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME = {Literal(definition.Name)} AND {string.Join(" AND ", conditions)})";
    }

    private static string BuildValueGenerationMatches(
        ExpectedColumnDefinition definition
    )
    {
        var strategy = definition.ProviderAnnotations
            .SingleOrDefault(annotation =>
                StringComparer.Ordinal.Equals(annotation.Name, ValueGenerationStrategyAnnotation))
            ?.Value as MySqlValueGenerationStrategy?;

        return strategy == MySqlValueGenerationStrategy.AutoIncrement
            ? "LOCATE('auto_increment', LOWER(c.EXTRA)) > 0"
            : "LOCATE('auto_increment', LOWER(c.EXTRA)) = 0";
    }

    private string BuildCollationMatches(
        string table,
        SafeMigrationCollationIdentifier? expectedCollation
    )
    {
        if (expectedCollation is not null)
        {
            return $"c.COLLATION_NAME <=> {Literal(expectedCollation.Name)}";
        }

        return "(c.COLLATION_NAME IS NULL OR c.COLLATION_NAME <=> "
            + "(SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)}))";
    }

    private string BuildStoreTypeMatches(
        string storeType,
        bool isMariaDb
    )
    {
        if (!isMariaDb
            || storeType.Contains('(', StringComparison.Ordinal))
        {
            return $"LOWER(c.COLUMN_TYPE) = LOWER({Literal(storeType)})";
        }

        var normalized = storeType
            .Trim()
            .ToLowerInvariant();

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var integerType = parts[0] is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";
        if (!integerType)
        {
            return $"LOWER(c.COLUMN_TYPE) = {Literal(normalized)}";
        }

        var canonicalType = parts[0] == "integer" ? "int" : parts[0];
        var expectedUnsigned = parts.Contains("unsigned", StringComparer.Ordinal);
        var expected = expectedUnsigned ? $"{canonicalType} unsigned" : canonicalType;

        return $"CONCAT(LOWER(c.DATA_TYPE), "
            + $"CASE WHEN LOCATE('unsigned', LOWER(c.COLUMN_TYPE)) > 0 "
            + $"THEN ' unsigned' ELSE '' END) = {Literal(expected)}";
    }

    private string BuildDefaultMatches(
        string catalogExpression,
        SafeMigrationDefaultValue expected,
        bool isNullable,
        RelationalTypeMapping mapping
    )
    {
        if (expected.Kind == SafeMigrationDefaultValueKind.None)
        {
            return isNullable
                ? $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')"
                : $"{catalogExpression} IS NULL";
        }

        if (expected.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            var expression = expected.SqlExpression ?? _expressionRenderer.Render(expected.StructuredExpression!);
            var sqlCandidates = BuildDefaultSqlCandidates(expression)
                .Select(Literal);

            return $"{catalogExpression} IN ({string.Join(", ", sqlCandidates)})";
        }

        var value = expected.GetLiteralValue();
        if (value is null)
        {
            return $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')";
        }

        if (value is byte[] bytes)
        {
            return BuildBinaryDefaultMatches(catalogExpression, bytes);
        }

        var providerLiteral = mapping.GenerateSqlLiteral(value);
        var candidates = new HashSet<string>(StringComparer.Ordinal) { providerLiteral };
        AddSimpleStringLiteralDisplayCandidate(candidates, providerLiteral);
        AddExpressionDefaultDisplayCandidate(candidates, providerLiteral);

        var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (invariant is not null)
        {
            candidates.Add(invariant);
        }

        if (value is bool boolean)
        {
            candidates.Add(boolean ? "1" : "0");
        }

        AddTemporalDefaultCandidates(candidates, value);

        return $"{catalogExpression} IN ({string.Join(", ", candidates.Select(Literal))})";
    }

    private static void AddSimpleStringLiteralDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Length < 2
            || providerLiteral[0] != '\''
            || providerLiteral[^1] != '\'')
        {
            return;
        }

        var interior = providerLiteral[1..^1];
        var unescaped = interior.Replace("''", "'", StringComparison.Ordinal);
        var escapedQuoteCount = interior.Count(static character => character == '\'');
        var unescapedQuoteCount = unescaped.Count(static character => character == '\'');

        if (unescapedQuoteCount * 2 != escapedQuoteCount)
        {
            return;
        }

        candidates.Add(unescaped);
    }

    private static void AddExpressionDefaultDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Contains('\'', StringComparison.Ordinal))
        {
            candidates.Add(providerLiteral.Replace("'", "\\'", StringComparison.Ordinal));
        }
    }

    private string BuildBinaryDefaultMatches(
        string catalogExpression,
        byte[] value
    )
    {
        var hex = Convert.ToHexString(value);
        var textualForms = new[] { $"0X{hex}", $"X'{hex}'", }.Select(Literal);

        var encodedForms = new[] { hex, $"27{hex}27", }.Select(Literal);

        return $"(UPPER({catalogExpression}) IN ({string.Join(", ", textualForms)}) "
            + $"OR UPPER(HEX({catalogExpression})) IN ({string.Join(", ", encodedForms)}))";
    }

    private static string[] BuildDefaultSqlCandidates(
        string expression
    )
    {
        var trimmed = expression.Trim();
        var candidates = new List<string>
        {
            trimmed,
            $"({trimmed})",
        };

        const string currentTimestamp = "CURRENT_TIMESTAMP";
        if (trimmed.StartsWith(currentTimestamp, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[currentTimestamp.Length..];
            if (suffix.Length == 0
                || (suffix is ['(', _, ..]
                    && suffix[^1] == ')'
                    && suffix[1..^1]
                        .All(char.IsAsciiDigit)))
            {
                candidates.Add($"current_timestamp{suffix}");
                candidates.Add($"now{suffix}");
            }
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddTemporalDefaultCandidates(
        HashSet<string> candidates,
        object value
    )
    {
        switch (value)
        {
            case DateOnly date:
                AddTemporalTypedCandidate(
                    candidates,
                    "DATE",
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                AddTemporalTypedCandidate(candidates, "TIME", time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                AddQuotedCandidate(candidates, dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                AddQuotedCandidate(
                    candidates,
                    dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void AddTemporalTypedCandidate(
        HashSet<string> candidates,
        string keyword,
        string value
    )
    {
        AddQuotedCandidate(candidates, value);
        candidates.Add($"{keyword} '{value}'");
        candidates.Add($"{keyword}'{value}'");
        candidates.Add($"{keyword}\\'{value}\\'");
        candidates.Add($"_utf8mb4'{value}'");
        candidates.Add($"_utf8mb4\\'{value}\\'");
    }

    private static void AddQuotedCandidate(
        HashSet<string> candidates,
        string value
    )
    {
        candidates.Add(value);
        candidates.Add($"'{value.Replace("'", "''", StringComparison.Ordinal)}'");
    }

    private string BuildComputedMatches(
        ExpectedColumnDefinition definition,
        bool isMariaDb
    )
    {
        if (definition.ComputedColumnSql is null
            && definition.ComputedExpression is null)
        {
            return "(c.GENERATION_EXPRESSION IS NULL OR c.GENERATION_EXPRESSION = '')";
        }

        var expression = definition.ComputedColumnSql ?? _expressionRenderer.Render(definition.ComputedExpression!);
        var candidates = new[] { expression, $"({expression})" }
            .Concat(
                MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
                    expression,
                    includeMySqlEncodedDisplay: !isMariaDb))
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);

        var storage = definition.IsStored == true ? "STORED GENERATED" : "VIRTUAL GENERATED";

        return $"c.GENERATION_EXPRESSION IN ({string.Join(", ", candidates)}) "
            + $"AND LOCATE({Literal(storage)}, c.EXTRA) > 0";
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
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private bool CanRepresentLiteralDefault(
        ExpectedColumnDefinition definition,
        MySqlServerVersion serverVersion
    )
    {
        if (definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.Literal)
        {
            return true;
        }

        var value = definition.DefaultValue.GetLiteralValue();
        if (value is null)
        {
            return true;
        }

        var mapping = _typeMappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            keyOrIndex: false,
            definition.IsUnicode,
            definition.MaxLength,
            definition.IsRowVersion,
            definition.IsFixedLength,
            definition.Precision,
            definition.Scale);

        var storeType = definition.StoreType ?? mapping?.StoreType;
        if (value is Guid
            && storeType?.StartsWith("binary", StringComparison.OrdinalIgnoreCase) == true)
        {
            var version = serverVersion.Version;

            return serverVersion.IsMariaDb
                && version is { Major: 11, Minor: 8 } or { Major: 12, Minor: 3 };
        }

        return true;
    }

    private string ColumnExists(
        string table,
        string column
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)})";
}
