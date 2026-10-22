namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Detects a name collision only when native FK creation would need another child index.</summary>
    /// <param name="definition">The requested physical FK owner and ordered dependent columns.</param>
    /// <param name="isMariaDb">Whether native MariaDB automatic-index qualification applies.</param>
    /// <returns>A catalog-only collision predicate for the runtime difference branch.</returns>
    private string ForeignKeyAutoIndexNameCollision(ExpectedForeignKeyDefinition definition, bool isMariaDb)
    {
        var columns = new string[definition.Columns.Count];
        // WHY: Store each rendered column literal at its declared key ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < columns.Length; index++)
        {
            columns[index] = Literal(definition.Columns[index]);
        }

        var table = Literal(definition.Table);
        var occupied = IsSupportingIndexNameOccupied(table, Literal(definition.Name));
        var covering = BuildForeignKeyChildCoveringIndexQuery(table, columns, isMariaDb);

        // WHY: Dropping a foreign key does not drop its supporting index.
        // Reusing its symbol for other columns can therefore collide with
        // automatic index creation. An independent covering index avoids that
        // creation; neither case authorizes dropping the historical index.
        return $"({occupied}) AND NOT EXISTS ({covering})";
    }

    /// <summary>Detects native child-key shapes that cannot guarantee FK enforcement for every row.</summary>
    /// <param name="definition">The requested physical FK owner and ordered dependent columns.</param>
    /// <param name="isMariaDb">Whether MariaDB's absence of MySQL array-index syntax applies.</param>
    /// <returns>A catalog-only integrity-risk predicate independent of names and existing FK matching.</returns>
    private string ForeignKeyMultiValuedIndexRisk(ExpectedForeignKeyDefinition definition, bool isMariaDb)
    {
        if (isMariaDb)
        {
            return "FALSE";
        }

        var columns = new string[definition.Columns.Count];
        // WHY: Store each rendered column literal at its declared key ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < columns.Length; index++)
        {
            columns[index] = Literal(definition.Columns[index]);
        }

        return BuildForeignKeyMultiValuedIndexRisk(Literal(definition.Table), columns, isMariaDb);
    }

    /// <summary>Finds matching native prefixes containing a canonical MySQL multi-valued array-cast part.</summary>
    /// <param name="renderedTable">The bound parameter or escaped SQL literal for the dependent table.</param>
    /// <param name="renderedColumns">Bound parameters or escaped literals for each ordered dependent column.</param>
    /// <param name="isMariaDb">Whether the expression-free MariaDB catalog contract applies.</param>
    /// <returns>An EXISTS predicate over exact native metadata, or FALSE on MariaDB.</returns>
    internal static string BuildForeignKeyMultiValuedIndexRisk(
        string renderedTable,
        IReadOnlyList<string> renderedColumns,
        bool isMariaDb
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(renderedTable);
        ArgumentNullException.ThrowIfNull(renderedColumns);
        if (isMariaDb)
        {
            return "FALSE";
        }

        return "EXISTS (" + BuildForeignKeyMultiValuedIndexQuery(renderedTable, renderedColumns, isMariaDb) + ")";
    }

    /// <summary>Captures exact native unsafe-index identities so accepted drops can retire their lifetime.</summary>
    /// <param name="renderedTable">The bound parameter or escaped SQL literal for the dependent table.</param>
    /// <param name="renderedColumns">The ordered bound parameters or escaped column literals.</param>
    /// <param name="isMariaDb">Whether MariaDB's expression-free catalog shape applies.</param>
    /// <returns>A complete per-index SELECT, or a typed empty SELECT for MariaDB.</returns>
    internal static string BuildForeignKeyMultiValuedIndexQuery(
        string renderedTable,
        IReadOnlyList<string> renderedColumns,
        bool isMariaDb
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(renderedTable);
        ArgumentNullException.ThrowIfNull(renderedColumns);
        if (isMariaDb)
        {
            return "SELECT NULL AS INDEX_NAME WHERE FALSE";
        }

        var prefix = BuildForeignKeyPrefixIndexQuery(renderedTable, renderedColumns, requireAscendingPrefix: false);

        // WHY: MySQL's Item_func_array_cast::print writes these exact outer
        // tokens into STATISTICS.EXPRESSION. Ordinary scalar casts finish with
        // their scalar type, not ARRAY; quoted ARRAY text inside an operand
        // cannot impersonate this native wrapper. Direction metadata is not a
        // discriminator: native array parts can also report COLLATION='A'.
        // Later InnoDB FK lookup ignores direction, unlike automatic-index
        // suppression, so every otherwise matching MVI prefix remains a risk.
        // A matching MVI can omit empty-array rows from its entries, so native
        // CREATE success is not evidence that later parent actions are safe.
        return prefix
            + " AND SUM(CASE WHEN " + BuildNativeMultiValuedIndexPart("supporting_index") + " "
            + "THEN 1 ELSE 0 END) > 0";
    }

    /// <summary>Prevents unsafe native FK backing removal or fallback while actual dependencies remain.</summary>
    /// <param name="intent">The exact physical index-removal target.</param>
    /// <param name="isMariaDb">Whether MariaDB's expression-free catalog contract applies.</param>
    /// <returns>A live catalog-only dependency predicate, or FALSE on MariaDB.</returns>
    private string MultiValuedIndexDropRisk(DropIndexIntent intent, bool isMariaDb)
    {
        if (isMariaDb)
        {
            return "FALSE";
        }

        return "EXISTS (" + BuildMultiValuedIndexBoundForeignKeysQuery(
            Literal(intent.Table), Literal(intent.Name), isMariaDb) + ")";
    }

    /// <summary>Captures FKs whose target index and a native MVI match the complete ordered child prefix.</summary>
    /// <param name="renderedTable">The bound parameter or escaped literal for the physical dependent table.</param>
    /// <param name="renderedIndex">The bound parameter or escaped literal for the physical index name.</param>
    /// <param name="isMariaDb">Whether MariaDB's expression-free catalog contract applies.</param>
    /// <returns>A complete per-FK SELECT, or a typed empty SELECT on MariaDB.</returns>
    internal static string BuildMultiValuedIndexBoundForeignKeysQuery(
        string renderedTable,
        string renderedIndex,
        bool isMariaDb
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(renderedTable);
        ArgumentException.ThrowIfNullOrEmpty(renderedIndex);
        if (isMariaDb)
        {
            return "SELECT NULL AS CONSTRAINT_NAME WHERE FALSE";
        }

        var boundSchemaOwner = BuildRequiredColumnAddOwnerMatches(
            "bound_index.TABLE_SCHEMA", "dependent_fk.TABLE_SCHEMA");

        var boundTableOwner = BuildRequiredColumnAddOwnerMatches("bound_index.TABLE_NAME", "dependent_fk.TABLE_NAME");
        var constraintSchemaOwner = BuildRequiredColumnAddOwnerMatches("dependent_fk.TABLE_SCHEMA", "DATABASE()");
        var constraintTableOwner = BuildRequiredColumnAddOwnerMatches("dependent_fk.TABLE_NAME", renderedTable);
        var arraySchemaOwner = BuildRequiredColumnAddOwnerMatches("array_part.TABLE_SCHEMA", "DATABASE()");
        var arrayTableOwner = BuildRequiredColumnAddOwnerMatches("array_part.TABLE_NAME", renderedTable);
        var unsafeSchemaOwner = BuildRequiredColumnAddOwnerMatches(
            "unsafe_prefix.TABLE_SCHEMA", "dependent_fk.TABLE_SCHEMA");

        var unsafeTableOwner = BuildRequiredColumnAddOwnerMatches(
            "unsafe_prefix.TABLE_NAME", "dependent_fk.TABLE_NAME");

        var unsafeIndexes = "SELECT array_part.INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS array_part "
            + "WHERE array_part.TABLE_SCHEMA = DATABASE() AND array_part.TABLE_NAME = " + renderedTable + " AND "
            + arraySchemaOwner + " AND " + arrayTableOwner + " AND array_part.INDEX_TYPE = 'BTREE' "
            + "GROUP BY array_part.INDEX_NAME, CAST(array_part.INDEX_NAME AS BINARY) HAVING SUM(CASE WHEN "
            + BuildNativeMultiValuedIndexPart("array_part") + " THEN 1 ELSE 0 END) > 0";

        // WHY: Removing an index cannot prove that InnoDB safely rebinds an
        // already unsafe MVI-backed FK. Removing an ordinary covering sibling
        // can also switch enforcement to that unsafe MVI, even for CASCADE.
        // Require an explicit prior FK drop for either exact compatible target;
        // no history variable, automatic deletion or creation-order assumption
        // can discharge a live dependency. Both LEFT JOINs retain every actual
        // FK part: complete ordered counts reject unrelated, reordered and
        // prefixed targets or MVIs without a correlated count per FK. Native
        // owner predicates prevent metadata collation from borrowing a
        // case-distinct physical table.
        // Derive MVI identities once per bound table; DISTINCT preserves one
        // physical FK alias when several compatible native MVIs are present.
        return "SELECT DISTINCT dependent_fk.CONSTRAINT_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE dependent_fk "
            + "JOIN (" + unsafeIndexes + ") unsafe_index ON TRUE "
            + "LEFT JOIN INFORMATION_SCHEMA.STATISTICS bound_index ON "
            + "bound_index.TABLE_SCHEMA = dependent_fk.TABLE_SCHEMA "
            + "AND bound_index.TABLE_NAME = dependent_fk.TABLE_NAME AND "
            + boundSchemaOwner + " AND " + boundTableOwner + " "
            + "AND bound_index.INDEX_NAME = " + renderedIndex + " AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("bound_index.INDEX_NAME", renderedIndex) + " "
            + "AND bound_index.INDEX_TYPE = 'BTREE' "
            + "AND bound_index.SEQ_IN_INDEX = dependent_fk.ORDINAL_POSITION "
            + "AND bound_index.COLUMN_NAME = dependent_fk.COLUMN_NAME AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("bound_index.COLUMN_NAME", "dependent_fk.COLUMN_NAME")
            + " AND bound_index.SUB_PART IS NULL "
            + "LEFT JOIN INFORMATION_SCHEMA.STATISTICS unsafe_prefix ON "
            + "unsafe_prefix.TABLE_SCHEMA = dependent_fk.TABLE_SCHEMA "
            + "AND unsafe_prefix.TABLE_NAME = dependent_fk.TABLE_NAME AND "
            + unsafeSchemaOwner + " AND " + unsafeTableOwner + " "
            + "AND unsafe_prefix.INDEX_NAME = unsafe_index.INDEX_NAME AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("unsafe_prefix.INDEX_NAME", "unsafe_index.INDEX_NAME")
            + " AND unsafe_prefix.INDEX_TYPE = 'BTREE' "
            + "AND unsafe_prefix.SEQ_IN_INDEX = dependent_fk.ORDINAL_POSITION "
            + "AND unsafe_prefix.COLUMN_NAME = dependent_fk.COLUMN_NAME AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("unsafe_prefix.COLUMN_NAME", "dependent_fk.COLUMN_NAME")
            + " AND unsafe_prefix.SUB_PART IS NULL "
            + "WHERE dependent_fk.TABLE_SCHEMA = DATABASE() AND dependent_fk.TABLE_NAME = " + renderedTable + " "
            + "AND " + constraintSchemaOwner + " AND " + constraintTableOwner + " "
            + "AND dependent_fk.REFERENCED_TABLE_NAME IS NOT NULL "
            + "GROUP BY dependent_fk.CONSTRAINT_NAME, CAST(dependent_fk.CONSTRAINT_NAME AS BINARY), "
            + "unsafe_index.INDEX_NAME, CAST(unsafe_index.INDEX_NAME AS BINARY) "
            + "HAVING COUNT(*) = COUNT(bound_index.SEQ_IN_INDEX) AND COUNT(*) = COUNT(unsafe_prefix.SEQ_IN_INDEX)";
    }

    /// <summary>Recognizes only the canonical outer wrapper emitted for a native MySQL array-cast index part.</summary>
    /// <param name="alias">The trusted builder-owned STATISTICS alias.</param>
    /// <returns>An exact native expression-wrapper predicate without interpreting quoted operand text.</returns>
    private static string BuildNativeMultiValuedIndexPart(string alias) =>
        alias + ".EXPRESSION IS NOT NULL "
        + "AND CAST(LEFT(" + alias + ".EXPRESSION, 5) AS BINARY) = CAST('cast(' AS BINARY) "
        + "AND CAST(RIGHT(" + alias + ".EXPRESSION, 7) AS BINARY) = CAST(' array)' AS BINARY)";

    /// <summary>Matches native column/index identities without metadata accent or padding equivalence.</summary>
    /// <param name="catalogExpression">The trusted builder-owned native identifier expression.</param>
    /// <param name="expectedExpression">The bound parameter, escaped literal or trusted counterpart expression.</param>
    /// <returns>A native case-folding equality predicate independent of schema/table case modes.</returns>
    internal static string BuildNativeCaseInsensitiveIdentifierMatches(
        string catalogExpression,
        string expectedExpression
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogExpression);
        ArgumentException.ThrowIfNullOrEmpty(expectedExpression);

        // WHY: Native column/index comparison folds case, but MariaDB's
        // metadata weights can also merge distinct accented identifiers.
        // Compare folded bytes instead; table/schema ownership continues to
        // use its separate lower_case_table_names contract.
        return $"CAST(LOWER({catalogExpression}) AS BINARY) = CAST(LOWER({expectedExpression}) AS BINARY)";
    }

    /// <summary>Captures exact local index-name occupation independently of its usability for a new FK.</summary>
    /// <param name="renderedTable">The bound parameter or escaped SQL literal for the dependent table.</param>
    /// <param name="renderedName">The bound parameter or escaped SQL literal for the requested FK symbol.</param>
    /// <returns>A current-database, dependent-table-local catalog EXISTS predicate.</returns>
    internal static string IsSupportingIndexNameOccupied(string renderedTable, string renderedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(renderedTable);
        ArgumentException.ThrowIfNullOrEmpty(renderedName);

        return "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS automatic_index_name "
            + "WHERE automatic_index_name.TABLE_SCHEMA = DATABASE() AND automatic_index_name.TABLE_NAME = "
            + renderedTable + " AND "
            + BuildRequiredColumnAddOwnerMatches("automatic_index_name.TABLE_SCHEMA", "DATABASE()")
            + " AND " + BuildRequiredColumnAddOwnerMatches("automatic_index_name.TABLE_NAME", renderedTable) + " "
            + "AND automatic_index_name.INDEX_NAME = " + renderedName + " AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("automatic_index_name.INDEX_NAME", renderedName) + ")";
    }

    /// <summary>Finds complete, unprefixed ordered child-key prefixes without imposing parent-key uniqueness.</summary>
    /// <param name="renderedTable">The bound parameter or escaped SQL literal for the dependent table.</param>
    /// <param name="renderedColumns">Bound parameters or escaped literals for each ordered dependent column.</param>
    /// <param name="isMariaDb">Whether native MariaDB automatic-index qualification applies.</param>
    /// <returns>A catalog SELECT yielding every native BTREE supporting-index identity.</returns>
    internal static string BuildForeignKeyChildCoveringIndexQuery(
        string renderedTable,
        IReadOnlyList<string> renderedColumns,
        bool isMariaDb
    ) => BuildForeignKeyPrefixIndexQuery(renderedTable, renderedColumns, requireAscendingPrefix: !isMariaDb);

    /// <summary>Builds exact prefix qualification for native lookup or automatic-index suppression.</summary>
    /// <param name="renderedTable">The bound parameter or escaped literal for the physical dependent table.</param>
    /// <param name="renderedColumns">The ordered bound parameters or escaped column literals.</param>
    /// <param name="requireAscendingPrefix">Whether generated-index suppression requires ASC prefix parts.</param>
    /// <returns>The complete catalog prefix query without application-row binding.</returns>
    private static string BuildForeignKeyPrefixIndexQuery(
        string renderedTable,
        IReadOnlyList<string> renderedColumns,
        bool requireAscendingPrefix
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(renderedTable);
        ArgumentNullException.ThrowIfNull(renderedColumns);
        if (renderedColumns.Count == 0)
        {
            throw new ArgumentException("A supporting-index query requires at least one column.",
                nameof(renderedColumns));
        }

        var result = new StringBuilder("SELECT supporting_index.INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS "
            + "supporting_index WHERE supporting_index.TABLE_SCHEMA = DATABASE() "
            + "AND supporting_index.TABLE_NAME = ");

        result.Append(renderedTable)
            .Append(" AND ")
            .Append(BuildRequiredColumnAddOwnerMatches("supporting_index.TABLE_SCHEMA", "DATABASE()"))
            .Append(" AND ")
            .Append(BuildRequiredColumnAddOwnerMatches("supporting_index.TABLE_NAME", renderedTable))
            .Append(" AND supporting_index.INDEX_TYPE = 'BTREE' GROUP BY supporting_index.INDEX_NAME, ")
            .Append("CAST(supporting_index.INDEX_NAME AS BINARY) ")
            .Append("HAVING COUNT(*) >= ")
            .Append(renderedColumns.Count.ToString(CultureInfo.InvariantCulture));

        // WHY: Match declared key members to their one-based native catalog positions.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < renderedColumns.Count; index++)
        {
            ArgumentException.ThrowIfNullOrEmpty(renderedColumns[index], nameof(renderedColumns));
            result.Append(" AND SUM(CASE WHEN supporting_index.SEQ_IN_INDEX = ")
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" AND supporting_index.COLUMN_NAME = ")
                .Append(renderedColumns[index])
                .Append(" AND ")
                .Append(BuildNativeCaseInsensitiveIdentifierMatches("supporting_index.COLUMN_NAME",
                    renderedColumns[index]));

            if (requireAscendingPrefix)
            {
                result.Append(" AND supporting_index.COLLATION = 'A'");
            }

            result.Append(" AND supporting_index.SUB_PART IS NULL THEN 1 ELSE 0 END) = 1");
        }

        // WHY: The native automatic-index suppression differs from InnoDB's
        // later key lookup: MySQL key_spec.cc compares sort direction only in
        // the generated FK prefix. MariaDB accepts either direction, and both
        // engines permit trailing parts and optimizer-hidden covering keys.
        // The existing native-owner predicate preserves mode-zero identity and
        // mode-one/two aliases without borrowing case-distinct physical owners;
        // per-ordinal sums avoid lossy GROUP_CONCAT and application-row reads.
        // Binary grouping separates physical accented aliases on MariaDB while
        // retaining the textual group key required by ONLY_FULL_GROUP_BY.
        return result.ToString();
    }
}
