namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>
    /// Qualifies catalog collation before an orphan probe can bind a potentially illegal string comparison.
    /// </summary>
    /// <param name="definition">The exact physical FK endpoints used by the row probe.</param>
    /// <returns>A catalog-only comparison-binding prerequisite, not a new candidate-key or storage policy.</returns>
    private string ForeignKeyDataBindingPrerequisites(ExpectedForeignKeyDefinition definition)
    {
        var pairs = new string[definition.Columns.Count];
        for (var index = 0; index < pairs.Length; index++)
        {
            pairs[index] = "(dependent_column.COLUMN_NAME = " + Literal(definition.Columns[index])
                + " AND principal_column.COLUMN_NAME = " + Literal(definition.PrincipalColumns[index]) + ")";
        }

        // WHY: Even a CASE branch that will not execute still binds its joins.
        // Different implicit string collations can therefore throw before a
        // planner sees the state. This barrier checks comparison legality only:
        // it does not impose the sparse certificate's stricter exact storage or
        // full unique-key contract on previously valid runtime operations.
        return "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS dependent_column "
            + "JOIN INFORMATION_SCHEMA.COLUMNS principal_column "
            + "ON principal_column.TABLE_SCHEMA = dependent_column.TABLE_SCHEMA "
            + "AND principal_column.TABLE_NAME = " + Literal(definition.PrincipalTable)
            + " AND " + BuildRequiredColumnAddOwnerMatches("principal_column.TABLE_NAME",
                Literal(definition.PrincipalTable))
            + " AND (" + string.Join(" OR ", pairs) + ") "
            + "WHERE dependent_column.TABLE_SCHEMA = DATABASE() AND dependent_column.TABLE_NAME = "
            + Literal(definition.Table)
            + " AND " + BuildRequiredColumnAddOwnerMatches("dependent_column.TABLE_NAME", Literal(definition.Table))
            + " AND dependent_column.COLLATION_NAME <=> principal_column.COLLATION_NAME) = "
            + pairs.Length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Resolves a projected endpoint with exactly the mapping used by normal column DDL.</summary>
    /// <param name="definition">The accepted, immutable projected column.</param>
    /// <returns>The provider's physical storage contract.</returns>
    internal string ResolveLiveForeignKeyStoreType(ExpectedColumnDefinition definition) => ResolveStoreType(definition);

    /// <summary>Captures all complete native unique keys without reading or binding application rows.</summary>
    /// <param name="command">The bounded catalog transport command.</param>
    /// <param name="definition">The exact authored FK endpoints.</param>
    /// <param name="databaseScopedNames">Whether FK symbols belong to the database rather than their table.</param>
    /// <param name="isMariaDb">The verified engine-specific dependent-index eligibility contract.</param>
    /// <returns>The UTF-8 bound of all parameter values, including transport overhead.</returns>
    internal static int BuildLiveForeignKeyCandidateProbe(
        SafeMigrationCatalogCommand command,
        ExpectedForeignKeyDefinition definition,
        bool databaseScopedNames,
        bool isMariaDb = false
    )
    {
        var valueBytes = 0;
        var table = AddLivePrerequisiteParameter(command, definition.PrincipalTable, ref valueBytes);
        var dependent = AddLivePrerequisiteParameter(command, definition.Table, ref valueBytes);
        var name = AddLivePrerequisiteParameter(command, definition.Name, ref valueBytes);
        var parts = new string[definition.PrincipalColumns.Count];
        var principalColumns = new string[definition.PrincipalColumns.Count];

        for (var index = 0; index < parts.Length; index++)
        {
            var column = AddLivePrerequisiteParameter(command, definition.PrincipalColumns[index], ref valueBytes);
            principalColumns[index] = column;
            parts[index] = "SUM(CASE WHEN s.SEQ_IN_INDEX = "
                + (index + 1).ToString(CultureInfo.InvariantCulture)
                + " AND s.COLUMN_NAME = " + column + " AND "
                + BuildNativeCaseInsensitiveIdentifierMatches("s.COLUMN_NAME", column)
                + " AND s.SUB_PART IS NULL THEN 1 ELSE 0 END) = 1";
        }

        var collisionOwner = databaseScopedNames ? string.Empty
            : " AND " + BuildRequiredColumnAddOwnerMatches("rc.TABLE_NAME", dependent);
        var localColumns = new string[definition.Columns.Count];
        for (var index = 0; index < localColumns.Length; index++)
        {
            localColumns[index] = AddLivePrerequisiteParameter(command, definition.Columns[index], ref valueBytes);
        }

        var coveringIndexes = BuildForeignKeyChildCoveringIndexQuery(dependent, localColumns, isMariaDb);
        var indexNameOccupied = IsSupportingIndexNameOccupied(dependent, name);
        var multiValuedRisk = BuildForeignKeyMultiValuedIndexRisk(dependent, localColumns, isMariaDb);
        var unsafeIndexes = BuildForeignKeyMultiValuedIndexQuery(dependent, localColumns, isMariaDb);
        var matchingForeignKeys = BuildLiveForeignKeyMatchingAliases(definition, dependent, table,
            localColumns, principalColumns);

        // WHY: Every candidate has its own row and complete ordered parts.
        // GROUP_CONCAT can truncate aliases, and a prefix or partial match is
        // not a standard candidate key. Constraints retain their backing-index
        // identity so an earlier drop cannot revive a historical key.
        command.CommandText = "SELECT certificate.names_free, certificate.owned_collision, "
            + "certificate.local_index_name_occupied, "
            + "certificate.multi_valued_index_risk, "
            + "candidate.INDEX_NAME, candidate.key_kind "
            + "FROM (SELECT NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + "WHERE rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.CONSTRAINT_NAME = " + name
            + " AND " + BuildNativeCaseInsensitiveIdentifierMatches("rc.CONSTRAINT_NAME", name)
            + collisionOwner + ") AS names_free, "
            + "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + "WHERE rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.CONSTRAINT_NAME = " + name
            + " AND " + BuildNativeCaseInsensitiveIdentifierMatches("rc.CONSTRAINT_NAME", name)
            + " AND " + BuildRequiredColumnAddOwnerMatches("rc.TABLE_NAME", dependent) + ") "
            + "AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + "WHERE rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.CONSTRAINT_NAME = " + name
            + " AND " + BuildNativeCaseInsensitiveIdentifierMatches("rc.CONSTRAINT_NAME", name)
            + (databaseScopedNames
                ? " AND NOT " + BuildRequiredColumnAddOwnerMatches("rc.TABLE_NAME", dependent) : " AND FALSE")
            + ") AS owned_collision, " + indexNameOccupied + " AS local_index_name_occupied, "
            + multiValuedRisk + " AS multi_valued_index_risk) certificate "
            + "LEFT JOIN (SELECT s.INDEX_NAME, CASE WHEN s.INDEX_NAME = 'PRIMARY' THEN 'primary_key' "
            + "WHEN MAX(tc.CONSTRAINT_NAME) IS NOT NULL THEN 'unique_constraint' ELSE 'index' END AS key_kind "
            + "FROM INFORMATION_SCHEMA.STATISTICS s LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
            + "ON CAST(tc.CONSTRAINT_SCHEMA AS BINARY) = CAST(s.TABLE_SCHEMA AS BINARY) "
            + "AND CAST(tc.TABLE_NAME AS BINARY) = CAST(s.TABLE_NAME AS BINARY) "
            + "AND tc.CONSTRAINT_NAME = s.INDEX_NAME AND "
            + BuildNativeCaseInsensitiveIdentifierMatches("tc.CONSTRAINT_NAME", "s.INDEX_NAME")
            + " AND tc.CONSTRAINT_TYPE = 'UNIQUE' "
            + "WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = " + table
            + " AND " + BuildRequiredColumnAddOwnerMatches("s.TABLE_NAME", table)
            + " AND s.NON_UNIQUE = 0 AND s.INDEX_TYPE = 'BTREE' "
            + "GROUP BY s.INDEX_NAME, CAST(s.INDEX_NAME AS BINARY) HAVING COUNT(*) = "
            + parts.Length.ToString(CultureInfo.InvariantCulture) + " AND " + string.Join(" AND ", parts)
            + " UNION ALL SELECT local_covering.INDEX_NAME, 'local_covering_index' AS key_kind FROM ("
            + coveringIndexes + ") local_covering "
            + "UNION ALL SELECT unsafe_native.INDEX_NAME, 'local_multi_valued_index' AS key_kind FROM ("
            + unsafeIndexes + ") unsafe_native "
            + "UNION ALL SELECT matching_fk.INDEX_NAME, 'local_matching_foreign_key' AS key_kind FROM ("
            + matchingForeignKeys + ") matching_fk) candidate "
            + "ON TRUE ORDER BY candidate.key_kind, candidate.INDEX_NAME;";

        return valueBytes;
    }

    /// <summary>
    /// Captures complete physically matching FK aliases without trusting requested-name absence alone.
    /// </summary>
    private static string BuildLiveForeignKeyMatchingAliases(
        ExpectedForeignKeyDefinition definition,
        string dependent,
        string principal,
        string[] localColumns,
        string[] principalColumns
    )
    {
        var parts = new string[localColumns.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = "SUM(CASE WHEN fk_column.ORDINAL_POSITION = "
                + (index + 1).ToString(CultureInfo.InvariantCulture)
                + " AND fk_column.COLUMN_NAME = " + localColumns[index]
                + " AND " + BuildNativeCaseInsensitiveIdentifierMatches("fk_column.COLUMN_NAME", localColumns[index])
                + " AND fk_column.REFERENCED_COLUMN_NAME = " + principalColumns[index] + " AND "
                + BuildNativeCaseInsensitiveIdentifierMatches("fk_column.REFERENCED_COLUMN_NAME",
                    principalColumns[index]) + " THEN 1 ELSE 0 END) = 1";
        }

        var updateRules = string.Join(", ", ReferentialRules(definition.OnUpdate)
            .Select(static rule => "'" + rule + "'"));

        var deleteRules = string.Join(", ", ReferentialRules(definition.OnDelete)
            .Select(static rule => "'" + rule + "'"));


        // WHY: A requested symbol can be absent while another physically
        // matching FK still exists. Releasing unsafe index metadata alone must
        // not authorize a no-op on that historically unvalidated constraint.
        // Complete native aliases retain the normal referential-rule semantics
        // and never read application rows or truncate their identity inventory.
        return "SELECT fk.CONSTRAINT_NAME AS INDEX_NAME FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS fk "
            + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE fk_column "
            + "ON fk_column.CONSTRAINT_SCHEMA = fk.CONSTRAINT_SCHEMA "
            + "AND fk_column.TABLE_NAME = fk.TABLE_NAME AND fk_column.CONSTRAINT_NAME = fk.CONSTRAINT_NAME AND "
            + BuildRequiredColumnAddOwnerMatches("fk_column.CONSTRAINT_SCHEMA", "fk.CONSTRAINT_SCHEMA")
            + " AND " + BuildRequiredColumnAddOwnerMatches("fk_column.TABLE_NAME", "fk.TABLE_NAME")
            + " AND " + BuildNativeCaseInsensitiveIdentifierMatches("fk_column.CONSTRAINT_NAME", "fk.CONSTRAINT_NAME")
            + " "
            + "WHERE fk.CONSTRAINT_SCHEMA = DATABASE() AND fk.TABLE_NAME = " + dependent
            + " AND " + BuildRequiredColumnAddOwnerMatches("fk.TABLE_NAME", dependent)
            + " GROUP BY fk.CONSTRAINT_NAME, CAST(fk.CONSTRAINT_NAME AS BINARY), fk.UPDATE_RULE, fk.DELETE_RULE, "
            + "fk_column.REFERENCED_TABLE_SCHEMA, fk_column.REFERENCED_TABLE_NAME HAVING COUNT(*) = "
            + localColumns.Length.ToString(CultureInfo.InvariantCulture) + " AND " + string.Join(" AND ", parts)
            + " AND " + BuildRequiredColumnAddOwnerMatches("fk_column.REFERENCED_TABLE_SCHEMA", "DATABASE()")
            + " AND " + BuildRequiredColumnAddOwnerMatches("fk_column.REFERENCED_TABLE_NAME", principal)
            + " AND fk.UPDATE_RULE IN (" + updateRules + ") AND fk.DELETE_RULE IN (" + deleteRules + ")";
    }

    /// <summary>Uses bound strings rather than splicing untrusted catalog identities into SQL.</summary>
    /// <param name="command">The bounded probe command.</param>
    /// <param name="value">The requested catalog identifier.</param>
    /// <param name="valueBytes">The aggregate UTF-8 transport estimate.</param>
    /// <returns>The newly bound parameter name.</returns>
    internal static string AddLivePrerequisiteParameter(
        SafeMigrationCatalogCommand command,
        string value,
        ref int valueBytes
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@live_" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
        valueBytes = checked(valueBytes + Encoding.UTF8.GetByteCount(value) + 32);

        return parameter.ParameterName;
    }
}
