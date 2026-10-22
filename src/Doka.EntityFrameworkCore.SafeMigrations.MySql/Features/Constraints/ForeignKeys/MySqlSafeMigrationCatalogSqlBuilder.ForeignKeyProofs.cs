namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Checks exact current storage and a complete unprefixed principal candidate key.</summary>
    /// <param name="definition">The requested foreign-key endpoints.</param>
    /// <returns>A catalog-only predicate; it never reads application rows.</returns>
    private string ForeignKeyPhysicalPrerequisites(ExpectedForeignKeyDefinition definition)
    {
        var pairs = new List<string>(definition.Columns.Count);
        var keyParts = new List<string>(definition.PrincipalColumns.Count);

        // WHY: Compare dependent and principal FK endpoints at the same ordered key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definition.Columns.Count; index++)
        {
            pairs.Add($"(dependent_column.COLUMN_NAME = {Literal(definition.Columns[index])} "
                + $"AND principal_column.COLUMN_NAME = {Literal(definition.PrincipalColumns[index])})");
            keyParts.Add($"SUM(CASE WHEN principal_key.SEQ_IN_INDEX = {index + 1} "
                + $"AND principal_key.COLUMN_NAME = {Literal(definition.PrincipalColumns[index])} "
                + "AND principal_key.SUB_PART IS NULL THEN 1 ELSE 0 END) = 1");
        }

        // WHY: ENUM/SET COLUMN_TYPE text contains member literals. Folding the
        // whole text erases their case-sensitive storage contract. Both sides
        // come from the same catalog statement, so binary comparison needs no
        // reinterpretation of authored literals or session SQL modes.
        const string storageMatches = "(CASE WHEN dependent_column.DATA_TYPE IN ('enum', 'set') "
            + "OR principal_column.DATA_TYPE IN ('enum', 'set') "
            + "THEN CAST(dependent_column.COLUMN_TYPE AS BINARY) = CAST(principal_column.COLUMN_TYPE AS BINARY) "
            + "ELSE LOWER(dependent_column.COLUMN_TYPE) = LOWER(principal_column.COLUMN_TYPE) END)";

        var compatible = "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS dependent_column "
            + "JOIN INFORMATION_SCHEMA.COLUMNS principal_column "
            + "ON principal_column.TABLE_SCHEMA = dependent_column.TABLE_SCHEMA "
            + $"AND principal_column.TABLE_NAME = {Literal(definition.PrincipalTable)} "
            + $"AND ({string.Join(" OR ", pairs)}) "
            + "WHERE dependent_column.TABLE_SCHEMA = DATABASE() "
            + $"AND dependent_column.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND {storageMatches} "
            + "AND dependent_column.COLLATION_NAME <=> principal_column.COLLATION_NAME "
            + "AND COALESCE(dependent_column.GENERATION_EXPRESSION, '') = '' "
            + "AND COALESCE(principal_column.GENERATION_EXPRESSION, '') = '') "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}";

        var candidate = "EXISTS (SELECT principal_key.INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS principal_key "
            + "WHERE principal_key.TABLE_SCHEMA = DATABASE() "
            + $"AND principal_key.TABLE_NAME = {Literal(definition.PrincipalTable)} "
            + "AND principal_key.NON_UNIQUE = 0 "
            + "GROUP BY principal_key.INDEX_NAME "
            + $"HAVING COUNT(*) = {definition.PrincipalColumns.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"AND {string.Join(" AND ", keyParts)})";

        return $"({compatible}) AND ({candidate})";
    }

    /// <summary>Requires every affected local column to permit the requested SET NULL action.</summary>
    /// <param name="definition">The complete requested foreign key.</param>
    /// <returns>The current catalog nullability predicate, or TRUE for another action.</returns>
    private string ForeignKeySetNullIsRepresentable(ExpectedForeignKeyDefinition definition)
        => definition.OnDelete != ReferentialAction.SetNull && definition.OnUpdate != ReferentialAction.SetNull
            ? "TRUE"
            : "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS null_column "
                + "WHERE null_column.TABLE_SCHEMA = DATABASE() "
                + $"AND null_column.TABLE_NAME = {Literal(definition.Table)} "
                + $"AND null_column.COLUMN_NAME IN ({string.Join(", ", definition.Columns.Select(Literal))}) "
                + "AND null_column.IS_NULLABLE = 'YES') "
                + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Captures bounded catalog facts for an ordered, source-bound FK replacement.</summary>
    /// <param name="definition">The immutable replacement endpoints.</param>
    /// <param name="sourceExists">Whether the original live result requires the owned FK to exist.</param>
    /// <returns>Catalog eligibility, nullability and complete matching physical names without reading rows.</returns>
    internal string BuildForeignKeyReplacementProof(ExpectedForeignKeyDefinition definition, bool sourceExists = true)
        => BuildForeignKeyReplacementProbe(definition, includeRows: false, sourceExists);

    /// <summary>Reads rows only after both physical endpoint inventories have qualified.</summary>
    /// <param name="definition">The catalog-qualified replacement endpoints.</param>
    /// <returns>The exact target's orphan-safety predicate.</returns>
    internal string BuildForeignKeyReplacementRowProof(ExpectedForeignKeyDefinition definition)
        => BuildForeignKeyReplacementProbe(definition, includeRows: true, sourceExists: true);

    /// <summary>Builds a bounded probe inside an isolated escaped-literal generation scope.</summary>
    /// <param name="definition">The requested immutable foreign key.</param>
    /// <param name="includeRows">Whether prior catalog qualification permits row binding.</param>
    /// <param name="sourceExists">The original physical presence required only by the catalog phase.</param>
    /// <returns>The catalog or qualified row probe.</returns>
    private string BuildForeignKeyReplacementProbe(
        ExpectedForeignKeyDefinition definition,
        bool includeRows,
        bool sourceExists
    )
    {
        if (_parameterValues is not null)
        {
            throw new InvalidOperationException(
                "The MySQL catalog plan builder does not support reentrant generation.");
        }

        // WHY: This auxiliary probe uses the same escaped identifier literals as
        // ordinary plans, but its bounded batch has no template parameter binder.
        _identifierMarkers = new Dictionary<string, string>(StringComparer.Ordinal);
        _parameterValues = [];
        _renderValuesInline = true;

        try
        {
            if (includeRows)
            {
                return $"SELECT NOT ({ForeignKeyDataBlocked(definition)});";
            }

            return BuildForeignKeyReplacementProofCore(definition, sourceExists);
        }
        finally
        {
            _identifierMarkers = null;
            _parameterValues = null;
            _renderValuesInline = false;
        }
    }

    /// <summary>Builds the replacement certificate inside an active escaped-literal scope.</summary>
    /// <param name="definition">The validated immutable foreign key.</param>
    /// <param name="sourceExists">The physical presence required by the independently classified source.</param>
    /// <returns>Repeated catalog eligibility and nullability with each complete matching physical name.</returns>
    private string BuildForeignKeyReplacementProofCore(ExpectedForeignKeyDefinition definition, bool sourceExists)
    {
        var ownedExists = ConstraintExists(definition.Table, definition.Name, "FOREIGN KEY");
        var sourceIdentity = sourceExists ? $"({ownedExists})" : $"NOT ({ownedExists})";
        var eligible = $"({BaseTableExists(definition.Table)}) "
            + $"AND ({BaseTableExists(definition.PrincipalTable)}) "
            + $"AND ({sourceIdentity}) "
            + $"AND ({ForeignKeyPhysicalPrerequisites(definition)})";

        var nullableBits = new List<string>(definition.Columns.Count);

        // WHY: Assign nullable-column bits from stable ordered FK positions.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definition.Columns.Count; index++)
        {
            nullableBits.Add($"WHEN {Literal(definition.Columns[index])} "
                + $"THEN {(1UL << index).ToString(CultureInfo.InvariantCulture)}");
        }

        var mask = "(SELECT COALESCE(SUM(CASE WHEN local_column.IS_NULLABLE = 'YES' "
            + $"THEN CASE local_column.COLUMN_NAME {string.Join(" ", nullableBits)} ELSE 0 END "
            + "ELSE 0 END), 0) FROM INFORMATION_SCHEMA.COLUMNS local_column "
            + "WHERE local_column.TABLE_SCHEMA = DATABASE() "
            + $"AND local_column.TABLE_NAME = {Literal(definition.Table)})";

        var matchingAliases = ForeignKeyMatchQuery(definition,
            $"rc.CONSTRAINT_NAME <> {Literal(definition.Name)}");

        // WHY: CASE does not prevent binding missing application columns.
        // Qualify catalog identities first; only eligible results enter the
        // separate bounded row-probe batch.
        // WHY: Every matching physical alias survives independently of the
        // requested name. Individual catalog rows avoid GROUP_CONCAT truncation
        // and preserve all identities for ordered removal during projection.
        return "SELECT certificate.eligible, certificate.nullable_mask, matching_alias.candidate_name "
            + $"FROM (SELECT ({eligible}) AS eligible, ({mask}) AS nullable_mask) certificate "
            + $"LEFT JOIN ({matchingAliases}) matching_alias ON certificate.eligible;";
    }
}
