namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private static string? GetUnsupportedTableFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features
    ) => intent is EnsureTableIntent { Definition.CheckConstraints.Count: > 0 }
        && !Supported(features, MySqlMigrationFeature.CheckConstraints)
            ? "check_constraint"
            : null;

    private MySqlSafeMigrationRuntimePlan BuildEnsureTable(
        EnsureTableIntent intent,
        bool isMariaDb,
        IReadOnlyList<ExpectedIndexDefinition>? expectedUniqueIndexes
    )
    {
        var definition = intent.Definition;
        var exists = TableExists(definition.Table);
        var baseTable = BaseTableExists(definition.Table);
        var matching = intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? baseTable
            : BuildTableMatches(definition, isMariaDb, expectedUniqueIndexes);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' "
            + $"WHEN NOT {baseTable} THEN 'unsupported' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
    }

    private MySqlSafeMigrationRuntimePlan BuildDropTable(
        DropTableIntent intent
    ) => Plan(
        $"CASE WHEN NOT {TableExists(intent.Table)} THEN 'missing' "
        + $"WHEN {BaseTableExists(intent.Table)} THEN 'matching' ELSE 'different' END",
        $"NOT {TableExists(intent.Table)}");

    private MySqlSafeMigrationRuntimePlan BuildRenameTable(
        RenameTableIntent intent
    )
    {
        var target = intent.NewName ?? intent.Name;
        var sourceObjectExists = TableExists(intent.Name);
        var sourceExists = BaseTableExists(intent.Name);
        var targetExists = TableExists(target);

        return Plan(
            $"CASE WHEN NOT {sourceObjectExists} THEN 'missing' "
            + $"WHEN NOT {sourceExists} THEN 'different' "
            + $"WHEN {targetExists} THEN 'different' ELSE 'matching' END",
            $"NOT {TableExists(intent.Name)}");
    }

    private string BuildTableMatches(
        ExpectedTableDefinition definition,
        bool isMariaDb,
        IReadOnlyList<ExpectedIndexDefinition>? expectedUniqueIndexes
    )
    {
        var conditions = new List<string>
        {
            BaseTableExists(definition.Table),
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(definition.Table)}) "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}",
            BuildAllUniqueKeysModeled(definition, isMariaDb, expectedUniqueIndexes),
            BuildAllCheckConstraintsModeled(definition, isMariaDb),
            BuildAllForeignKeysModeled(definition),
            $"COALESCE((SELECT t.TABLE_COMMENT FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(definition.Table)}), '') "
            + $"= {Literal(definition.Comment ?? string.Empty)}",
        };

        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            conditions.Add(BuildColumnMatches(definition.Table, definition.Columns[ordinal], isMariaDb, ordinal + 1));
        }

        conditions.Add(
            definition.PrimaryKey is null
                ? $"NOT {PrimaryKeyExists(definition.Table)}"
                : ConstraintColumnsMatch(definition.Table, "PRIMARY", definition.PrimaryKey.Columns, "PRIMARY KEY"));

        conditions.AddRange(definition.UniqueConstraints.Select(UniqueConstraintSatisfied));
        conditions.AddRange(
            definition.CheckConstraints.Select(checkConstraint =>
                CheckConstraintSatisfied(checkConstraint, isMariaDb)));
        conditions.AddRange(definition.ForeignKeys.Select(ForeignKeySatisfied));

        return $"({string.Join(" AND ", conditions)})";
    }

    private string BuildAllUniqueKeysModeled(
        ExpectedTableDefinition definition,
        bool isMariaDb,
        IReadOnlyList<ExpectedIndexDefinition>? expectedUniqueIndexes
    )
    {
        var expectedConstraintShapes = definition.UniqueConstraints.Select(constraint =>
            BuildUniqueConstraintIndexCandidateMatches(constraint, "candidate_unique"));

        var expectedIndexShapes =
            (expectedUniqueIndexes ?? []).Select(index => BuildIndexCandidateMatches(
                index,
                isMariaDb,
                "candidate_unique"));

        var modeledShapes = expectedConstraintShapes
            .Concat(expectedIndexShapes)
            .ToArray();

        var modeled = modeledShapes.Length == 0 ? "FALSE" : $"({string.Join(" OR ", modeledShapes)})";

        // MySQL exposes unique constraints and unique indexes through the same
        // physical index catalog. Check each live object against the complete
        // target definitions: a later EnsureIndex may still be absent here,
        // while any number of semantically equivalent aliases is legitimate.
        return "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS candidate_unique "
            + $"WHERE candidate_unique.TABLE_SCHEMA = DATABASE() "
            + $"AND candidate_unique.TABLE_NAME = {Literal(definition.Table)} "
            + "AND candidate_unique.NON_UNIQUE = 0 "
            + "AND candidate_unique.INDEX_NAME <> 'PRIMARY' "
            + "AND candidate_unique.SEQ_IN_INDEX = 1 "
            + $"AND NOT ({modeled}))";
    }

    private string BuildUniqueConstraintIndexCandidateMatches(
        ExpectedUniqueConstraintDefinition definition,
        string candidate
    )
    {
        var conditions = new List<string>
        {
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS s "
            + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME) "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}",
        };

        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            var position = (ordinal + 1).ToString(CultureInfo.InvariantCulture);

            conditions.Add(
                "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
                + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
                + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME "
                + $"AND s.SEQ_IN_INDEX = {position} "
                + $"AND s.COLUMN_NAME = {Literal(definition.Columns[ordinal])} "
                + "AND s.SUB_PART IS NULL)");
        }

        return $"({string.Join(" AND ", conditions)})";
    }

    private string BuildAllCheckConstraintsModeled(
        ExpectedTableDefinition definition,
        bool isMariaDb
    )
    {
        var implicitJsonChecks = isMariaDb
            ? definition.Columns
                .Where(static column => StringComparer.OrdinalIgnoreCase.Equals(column.StoreType?.Trim(), "json"))
                .Select(MariaDbImplicitJsonCheckMatches)
                .ToArray()
            : [];

        var expectedMatches = definition.CheckConstraints
            .Select(checkConstraint => CheckConstraintMatches(
                checkConstraint,
                isMariaDb,
                "tc.CONSTRAINT_NAME = candidate_tc.CONSTRAINT_NAME"))
            .ToArray();

        var providerGeneratedFilter = implicitJsonChecks.Length == 0
            ? string.Empty
            : $"AND NOT ({string.Join(" OR ", implicitJsonChecks)}) ";

        var modeled = expectedMatches.Length == 0
            ? "FALSE"
            : $"({string.Join(" OR ", expectedMatches)})";

        return "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS candidate_tc "
            + (implicitJsonChecks.Length == 0
                ? string.Empty
                : "JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc "
                    + "ON cc.CONSTRAINT_SCHEMA = candidate_tc.CONSTRAINT_SCHEMA "
                    + "AND cc.TABLE_NAME = candidate_tc.TABLE_NAME "
                    + "AND cc.CONSTRAINT_NAME = candidate_tc.CONSTRAINT_NAME ")
            + $"WHERE candidate_tc.CONSTRAINT_SCHEMA = DATABASE() "
            + $"AND candidate_tc.TABLE_NAME = {Literal(definition.Table)} "
            + "AND candidate_tc.CONSTRAINT_TYPE = 'CHECK' "
            + providerGeneratedFilter
            + $"AND NOT ({modeled}))";
    }

    private string BuildAllForeignKeysModeled(
        ExpectedTableDefinition definition
    )
    {
        var expectedMatches = definition.ForeignKeys
            .Select(foreignKey => ForeignKeyMatches(
                foreignKey,
                "rc.CONSTRAINT_NAME = candidate_rc.CONSTRAINT_NAME"))
            .ToArray();

        var modeled = expectedMatches.Length == 0
            ? "FALSE"
            : $"({string.Join(" OR ", expectedMatches)})";

        return "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS candidate_rc "
            + $"WHERE candidate_rc.CONSTRAINT_SCHEMA = DATABASE() "
            + $"AND candidate_rc.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND NOT ({modeled}))";
    }

    private string MariaDbImplicitJsonCheckMatches(
        ExpectedColumnDefinition column
    )
    {
        // MariaDB names an inline column CHECK after its column. Requiring the
        // expected JSON store type plus the exact JSON_VALID expression keeps
        // unrelated user-authored checks visible to strict comparison.
        return $"(candidate_tc.CONSTRAINT_NAME = {Literal(column.Name)} "
            + "AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE("
            + "cc.CHECK_CLAUSE, '`', ''), ' ', ''), '(', ''), ')', '')) "
            + $"= CONCAT('json_valid', LOWER({Literal(column.Name)})))";
    }

    private string TableExists(
        string table
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES t "
        + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)})";

    private string BaseTableExists(
        string table
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES t "
        + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)} "
        + "AND t.TABLE_TYPE = 'BASE TABLE')";
}
