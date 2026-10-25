namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlColumnRepairTransition BuildColumnRepairTransition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        bool includeTransitionEvidence
    )
    {
        var storeType = ResolveStoreType(definition);
        if (TryParseVarcharLength(storeType, out var targetLength)
            && definition.ClrType == typeof(string))
        {
            var narrowing = $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS narrowing_column "
                + "WHERE narrowing_column.TABLE_SCHEMA = DATABASE() "
                + $"AND narrowing_column.TABLE_NAME = {Literal(table)} "
                + $"AND narrowing_column.COLUMN_NAME = {Literal(definition.Name)} "
                + "AND (LOWER(narrowing_column.DATA_TYPE) <> 'varchar' "
                + $"OR narrowing_column.CHARACTER_MAXIMUM_LENGTH > {targetLength}))";

            var strictMode = $"NOT ({narrowing}) OR {StrictSqlModeExpression}";

            return new MySqlColumnRepairTransition(
                MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
                strictMode,
                new MySqlSafeMigrationDataProbe(
                    table,
                    definition.Name,
                    targetLength,
                    Delimited(table, schema),
                    Delimited(definition.Name),
                    includeTransitionEvidence
                        ? BuildVarcharTransitionColumnExists(table, definition, targetLength, isMariaDb)
                        : null,
                    narrowing),
                SafeMigrationOperationalImpact.TableRewritePossible);
        }

        if (TryGetTextCapacity(storeType, out var targetCapacity)
            && definition.ClrType == typeof(string))
        {
            return new MySqlColumnRepairTransition(
                BuildTextTransitionColumnExists(table, definition, targetCapacity, isMariaDb),
                "FALSE",
                "TRUE",
                DataProbe: null,
                SafeMigrationOperationalImpact.TableRewritePossible);
        }

        if (IsBooleanTinyInt(storeType, definition.ClrType))
        {
            if (!IsSupportedBooleanTarget(definition))
            {
                return MySqlColumnRepairTransition.None;
            }

            var columnInvariant = "LOWER(c.DATA_TYPE) = 'bit' AND LOWER(c.COLUMN_TYPE) = 'bit(1)' "
                + $"AND {BooleanSourceDefaultIsRepresentable()} "
                + $"AND {BuildComputedMatches(definition, isMariaDb)} "
                + $"AND {BuildValueGenerationMatches(definition, temporalRowVersion: false)} "
                + $"AND {OrdinaryColumnExtraMatches()} "
                + $"AND {NoForeignKeyDependency(table, definition.Name)}";

            var transitionInvariant = ColumnWithInvariantExists(table, definition.Name, columnInvariant);

            return new MySqlColumnRepairTransition(
                transitionInvariant,
                "FALSE",
                "TRUE",
                DataProbe: null,
                SafeMigrationOperationalImpact.TableRewritePossible);
        }

        return MySqlColumnRepairTransition.None;
    }

    private static bool IsSupportedBooleanTarget(
        ExpectedColumnDefinition definition
    )
    {
        if (!MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
            || metadata.GuidFormat is not null
            || metadata.ValueGenerationStrategy is not null and not MySqlValueGenerationStrategy.None)
        {
            return false;
        }

        return definition.DefaultValue.Kind switch
        {
            SafeMigrationDefaultValueKind.None => true,
            SafeMigrationDefaultValueKind.Literal => definition.DefaultValue.GetLiteralValue() is null or bool,
            SafeMigrationDefaultValueKind.Sql => false,
            _ => false,
        };
    }

    private static string BooleanSourceDefaultIsRepresentable() =>
        // WHY: A BIT(1) value is losslessly representable as TINYINT(1), but an
        // arbitrary default expression is a separate behavioral contract. Only
        // the catalog spellings proven to represent the Boolean literal domain
        // are eligible for automatic repair.
        "(c.COLUMN_DEFAULT IS NULL OR UPPER(TRIM(c.COLUMN_DEFAULT)) " + "IN ('NULL', '0', '1', 'B''0''', 'B''1'''))";

    private string NoForeignKeyDependency(
        string table,
        string column
    )
    {
        var builder = new StringBuilder(768);

        AppendNoForeignKeyDependency(builder, table, column);

        return builder.ToString();
    }

    private void AppendNoForeignKeyDependency(
        StringBuilder builder,
        string table,
        string column
    )
    {
        // WHY: MySQL requires compatible types on both sides of a foreign key.
        // A single-column repair cannot prove or atomically apply the required
        // coupled transition, so ownership remains with an explicit migration.
        builder
            .Append("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE dependency ")
            // WHY: Incoming FKs can belong to another database. Restrict only
            // the dependent owner of the outgoing arm, never the complete
            // inventory before testing this database's referenced owner.
            .Append("WHERE dependency.REFERENCED_TABLE_NAME IS NOT NULL AND ((")
            .Append("dependency.TABLE_SCHEMA = DATABASE() AND dependency.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND dependency.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(") OR (dependency.REFERENCED_TABLE_SCHEMA = DATABASE() ")
            .Append("AND dependency.REFERENCED_TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND dependency.REFERENCED_COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(")))");
    }

    private string ColumnWithInvariantExists(
        string table,
        string column,
        string invariant
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)} AND ({invariant}))";

    private string BuildVarcharTransitionColumnExists(
        string table,
        ExpectedColumnDefinition definition,
        int targetLength,
        bool isMariaDb
    )
    {
        // WHY: The fixed predicate body is approximately 4.9K characters and
        // references seven table plus six column literals. The bounded
        // headroom keeps ordinary identifiers on one backing buffer;
        // StringBuilder can still grow for heavily escaped or long names.
        var initialCapacity = checked(5800
            + (Literal(table).Length * 7)
            + (Literal(definition.Name).Length * 6));

        var builder = new StringBuilder(initialCapacity);

        builder
            .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c ")
            .Append("WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND c.COLUMN_NAME = ")
            .Append(Literal(definition.Name))
            .Append(" AND (LOWER(c.DATA_TYPE) IN ")
            .Append("('varchar', 'tinytext', 'text', 'mediumtext', 'longtext') ")
            .Append("AND NOT (LOWER(c.DATA_TYPE) = 'varchar' AND c.CHARACTER_MAXIMUM_LENGTH = ")
            .Append(targetLength.ToString(CultureInfo.InvariantCulture))
            .Append(") AND ");

        AppendSupportedStringRepairTable(builder, table);
        builder
            .Append(" AND ")
            .Append(
                BuildCollationContract(table, definition.Collation, mariaDbJsonAlias: false)
                    .MatchExpression)
            .Append(" AND ")
            .Append(BuildComputedMatches(definition, isMariaDb))
            .Append(" AND ")
            .Append(BuildValueGenerationMatches(definition, temporalRowVersion: false))
            .Append(" AND ")
            .Append(OrdinaryColumnExtraMatches())
            .Append(" AND ");

        AppendTargetDeclaredRowSizeFits(builder, table, definition.Name, targetLength);
        builder.Append(" AND ");
        AppendDependentIndexesRemainRepresentable(builder, table, definition.Name, targetLength);
        builder.Append(" AND ");
        AppendNoForeignKeyDependency(builder, table, definition.Name);
        builder.Append("))");

        return builder.ToString();
    }

    private string BuildTextTransitionColumnExists(
        string table,
        ExpectedColumnDefinition definition,
        ulong targetCapacity,
        bool isMariaDb
    )
    {
        var initialCapacity = checked(3900
            + (Literal(table).Length * 6)
            + (Literal(definition.Name).Length * 5));

        var builder = new StringBuilder(initialCapacity);

        builder
            .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c ")
            .Append("WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND c.COLUMN_NAME = ")
            .Append(Literal(definition.Name))
            .Append(" AND (LOWER(c.DATA_TYPE) IN ")
            .Append("('varchar', 'tinytext', 'text', 'mediumtext', 'longtext') ")
            .Append("AND LOWER(c.DATA_TYPE) <> ")
            .Append(
                Literal(
                    ResolveStoreType(definition)
                        .Trim()
                        .ToLowerInvariant()))
            .Append(" AND COALESCE(c.CHARACTER_OCTET_LENGTH, 4294967296) <= ")
            .Append(targetCapacity.ToString(CultureInfo.InvariantCulture))
            .Append(" AND ");

        AppendSupportedStringRepairTable(builder, table);
        builder
            .Append(" AND ")
            .Append(
                BuildCollationContract(table, definition.Collation, mariaDbJsonAlias: false)
                    .MatchExpression)
            .Append(" AND ")
            .Append(BuildComputedMatches(definition, isMariaDb))
            .Append(" AND ")
            .Append(BuildValueGenerationMatches(definition, temporalRowVersion: false))
            .Append(" AND ")
            .Append(OrdinaryColumnExtraMatches())
            .Append(" AND ");

        AppendTargetDeclaredRowSizeFits(builder, table, definition.Name, targetLength: null);
        builder.Append(" AND ");
        AppendDependentIndexesRemainRepresentable(builder, table, definition.Name, targetLength: null);
        builder.Append(" AND ");
        AppendNoForeignKeyDependency(builder, table, definition.Name);
        builder.Append("))");

        return builder.ToString();
    }

    private void AppendSupportedStringRepairTable(
        StringBuilder builder,
        string table
    ) => builder
        .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES repair_table ")
        .Append("WHERE repair_table.TABLE_SCHEMA = DATABASE() AND repair_table.TABLE_NAME = ")
        .Append(Literal(table))
        .Append(" AND repair_table.TABLE_TYPE = 'BASE TABLE' ")
        .Append("AND UPPER(repair_table.ENGINE) = 'INNODB')");

    private void AppendTargetDeclaredRowSizeFits(
        StringBuilder builder,
        string table,
        string column,
        int? targetLength,
        int? targetScalarBytes = null
    )
    {
        // WHY: MySQL rejects a declared row above 65,535 bytes before
        // InnoDB can move long values off-page. Unknown families consume the
        // complete budget so incomplete catalog knowledge fails closed.
        // MariaDB UUID contributes sixteen bytes without string overhead.
        builder
            .Append("COALESCE((SELECT SUM(CASE WHEN row_column.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" THEN ");

        if (targetScalarBytes is { } scalarBytes)
        {
            builder.Append(scalarBytes.ToString(CultureInfo.InvariantCulture)).Append(' ');
        }
        else if (targetLength is { } varcharLength)
        {
            var targetLengthSql = varcharLength.ToString(CultureInfo.InvariantCulture);

            builder
                .Append('(')
                .Append(targetLengthSql)
                .Append(" * COALESCE(character_set.MAXLEN, 1)) + CASE WHEN ")
                .Append(targetLengthSql)
                .Append(" * COALESCE(character_set.MAXLEN, 1) <= 255 THEN 1 ELSE 2 END ");
        }
        else
        {
            builder.Append("12 ");
        }

        builder
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('varchar', 'varbinary') ")
            .Append("THEN row_column.CHARACTER_OCTET_LENGTH ")
            .Append("+ CASE WHEN row_column.CHARACTER_OCTET_LENGTH <= 255 THEN 1 ELSE 2 END ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('char', 'binary') ")
            .Append("THEN row_column.CHARACTER_OCTET_LENGTH ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('tinyblob', 'tinytext', 'blob', 'text', ")
            .Append("'mediumblob', 'mediumtext', 'longblob', 'longtext', 'json', 'geometry') THEN 12 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'uuid' THEN 16 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'tinyint' THEN 1 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'smallint' THEN 2 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'mediumint' THEN 3 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('int', 'integer') THEN 4 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'float' ")
            .Append("THEN CASE WHEN row_column.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('bigint', 'double') THEN 8 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'decimal' ")
            .Append("THEN CEIL(COALESCE(row_column.NUMERIC_PRECISION, 65) / 2) ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'bit' ")
            .Append("THEN CEIL(COALESCE(row_column.NUMERIC_PRECISION, 64) / 8) ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'date' THEN 3 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('time', 'datetime', 'timestamp') THEN 8 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'year' THEN 1 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'enum' THEN 2 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'set' THEN 8 ")
            .Append("ELSE 65536 END) ")
            .Append("+ CEIL(SUM(CASE WHEN row_column.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END) / 8) ")
            .Append("FROM INFORMATION_SCHEMA.COLUMNS row_column ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS character_set ")
            .Append("ON character_set.CHARACTER_SET_NAME = row_column.CHARACTER_SET_NAME ")
            .Append("WHERE row_column.TABLE_SCHEMA = DATABASE() AND row_column.TABLE_NAME = ")
            .Append(Literal(table))
            .Append("), 65536) <= 65535");
    }

    private void AppendDependentIndexesRemainRepresentable(
        StringBuilder builder,
        string table,
        string column,
        int? targetLength,
        int? targetScalarBytes = null
    )
    {
        builder
            .Append("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS ti ")
            .Append("WHERE ti.TABLE_SCHEMA = DATABASE() AND ti.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND ti.COLUMN_NAME = ")
            .Append(Literal(column));

        if (targetLength is null && targetScalarBytes is null)
        {
            // MySQL and MariaDB require a prefix for non-FULLTEXT indexes over
            // a TEXT column. Existing prefix and FULLTEXT indexes retain their
            // physical key shape across this transition.
            builder
                .Append(" AND ti.SUB_PART IS NULL ")
                .Append("AND UPPER(COALESCE(ti.INDEX_TYPE, '')) <> 'FULLTEXT')");

            return;
        }

        builder
            .Append(" AND ti.SUB_PART IS NULL ")
            .Append("AND UPPER(COALESCE(ti.INDEX_TYPE, '')) <> 'FULLTEXT' ")
            .Append("AND (SELECT COALESCE(SUM(CASE ")
            .Append("WHEN ip.COLUMN_NAME IS NULL THEN 3073 ")
            .Append("WHEN ip.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" AND ip.SUB_PART IS NULL THEN ")
            .Append(targetScalarBytes is { } scalarBytes
                ? scalarBytes.ToString(CultureInfo.InvariantCulture)
                : targetLength!.Value.ToString(CultureInfo.InvariantCulture) + " * COALESCE(cs.MAXLEN, 1)")
            .Append(' ')
            .Append("WHEN ip.SUB_PART IS NOT NULL ")
            .Append("THEN ip.SUB_PART * CASE WHEN ic.COLLATION_NAME IS NULL ")
            .Append("THEN 1 ELSE COALESCE(cs.MAXLEN, 1) END ")
            .Append("WHEN ic.CHARACTER_OCTET_LENGTH IS NOT NULL ")
            .Append("THEN ic.CHARACTER_OCTET_LENGTH ")
            // WHY: A native UUID key retains its full sixteen-byte value; it
            // must not inherit an unknown-family overflow during sibling repair.
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'uuid' THEN 16 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'tinyint' THEN 1 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'smallint' THEN 2 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'mediumint' THEN 3 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) IN ('int', 'integer') THEN 4 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'float' ")
            .Append("THEN CASE WHEN ic.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END ")
            .Append("WHEN LOWER(ic.DATA_TYPE) ")
            .Append("IN ('bigint', 'double', 'datetime', 'timestamp') THEN 8 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'date' THEN 3 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'time' THEN 6 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'year' THEN 1 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'decimal' ")
            .Append("THEN CEIL(COALESCE(ic.NUMERIC_PRECISION, 65) / 2) ")
            .Append("ELSE 3073 END), 0) FROM INFORMATION_SCHEMA.STATISTICS ip ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLUMNS ic ")
            .Append("ON ic.TABLE_SCHEMA = ip.TABLE_SCHEMA ")
            .Append("AND ic.TABLE_NAME = ip.TABLE_NAME ")
            .Append("AND ic.COLUMN_NAME = ip.COLUMN_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLLATIONS co ")
            .Append("ON co.COLLATION_NAME = ic.COLLATION_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS cs ")
            .Append("ON cs.CHARACTER_SET_NAME = co.CHARACTER_SET_NAME ")
            .Append("WHERE ip.TABLE_SCHEMA = ti.TABLE_SCHEMA ")
            .Append("AND ip.TABLE_NAME = ti.TABLE_NAME ")
            .Append("AND ip.INDEX_NAME = ti.INDEX_NAME) > ")
            .Append("COALESCE((SELECT CASE WHEN UPPER(dt.ENGINE) = 'INNODB' THEN ")
            .Append(BuildMaximumIndexKeyWidth("dt"))
            .Append(" ELSE 0 END FROM INFORMATION_SCHEMA.TABLES dt ")
            .Append("WHERE dt.TABLE_SCHEMA = ti.TABLE_SCHEMA ")
            .Append("AND dt.TABLE_NAME = ti.TABLE_NAME LIMIT 1), 0))");
    }

    private string ResolveStoreType(
        ExpectedColumnDefinition definition
    ) => definition.StoreType
        ?? _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?.StoreType
        ?? throw new InvalidOperationException($"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

    /// <summary>Parses the variable-character length shared by runtime and ordered projection.</summary>
    internal static bool TryParseVarcharLength(
        string storeType,
        out int length
    )
    {
        var value = storeType.AsSpan().Trim();
        const string prefix = "varchar(";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value[^1] != ')'
            || !int.TryParse(value[prefix.Length..^1], CultureInfo.InvariantCulture, out length))
        {
            length = 0;

            return false;
        }

        return length > 0;
    }

    /// <summary>Resolves a text family's byte capacity for runtime and ordered projection proofs.</summary>
    internal static bool TryGetTextCapacity(
        string storeType,
        out ulong capacity
    )
    {
        capacity = storeType
                .Trim()
                .ToLowerInvariant() switch
            {
                "tinytext" => byte.MaxValue,
                "text" => ushort.MaxValue,
                "mediumtext" => 16777215UL,
                "longtext" => uint.MaxValue,
                _ => 0,
            };

        return capacity > 0;
    }

    private static bool IsBooleanTinyInt(
        string storeType,
        Type clrType
    )
    {
        var underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return underlyingType == typeof(bool)
            && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "tinyint(1)");
    }

    private sealed record MySqlColumnRepairTransition(
        string InvariantExpression,
        string DataBlockedExpression,
        string ExecutionInvariantExpression,
        MySqlSafeMigrationDataProbe? DataProbe,
        SafeMigrationOperationalImpact OperationalImpact
    )
    {
        public bool HasDataProbe => DataProbe is not null;

        public static MySqlColumnRepairTransition None { get; } = new(
            "FALSE",
            "FALSE",
            "TRUE",
            DataProbe: null,
            SafeMigrationOperationalImpact.NotApplicable);
    }

    private static string OrdinaryColumnExtraMatches() =>
        // MySQL and MariaDB require the complete column definition for MODIFY
        // COLUMN. Reject unmodeled EXTRA metadata because omitting ON UPDATE,
        // INVISIBLE, or a similar modifier would silently erase it. MySQL may
        // expose DEFAULT_GENERATED for an otherwise modeled expression default.
        "TRIM(REPLACE(LOWER(COALESCE(c.EXTRA, '')), 'default_generated', '')) = ''";

    private static string BuildValueGenerationMatches(
        ExpectedColumnDefinition definition,
        bool temporalRowVersion
    )
    {
        if (!MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata))
        {
            return "FALSE";
        }

        if (metadata.ValueGenerationStrategy == MySqlValueGenerationStrategy.AutoIncrement)
        {
            return "LOCATE('auto_increment', LOWER(c.EXTRA)) > 0";
        }

        if (temporalRowVersion)
        {
            // Doka materializes a temporal row version as both a generated
            // default and an ON UPDATE expression. Comparing the complete
            // normalized EXTRA value prevents an unrelated provider modifier
            // from being accepted as part of that owned contract.
            return "LOCATE('auto_increment', LOWER(c.EXTRA)) = 0 "
                + "AND TRIM(REPLACE(LOWER(COALESCE(c.EXTRA, '')), "
                + "'default_generated', '')) = 'on update current_timestamp(6)'";
        }

        return "LOCATE('auto_increment', LOWER(c.EXTRA)) = 0";
    }

    private static bool IsTemporalRowVersion(
        ExpectedColumnDefinition definition
    )
    {
        if (!definition.IsRowVersion)
        {
            return false;
        }

        if (definition.StoreType is not null)
        {
            var normalizedStoreType = definition
                .StoreType
                .AsSpan()
                .TrimStart();

            return normalizedStoreType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
                || normalizedStoreType.StartsWith("datetime", StringComparison.OrdinalIgnoreCase);
        }

        // This is the same fallback Doka applies to a hand-authored migration
        // without ColumnType. Keeping the predicate aligned with the provider's
        // DDL decision prevents inferred type mappings from changing ownership.
        var clrType = Nullable.GetUnderlyingType(definition.ClrType) ?? definition.ClrType;

        return clrType == typeof(byte[]) || clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset);
    }
}
