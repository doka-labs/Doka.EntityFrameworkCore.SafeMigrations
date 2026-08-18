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
        bool isMariaDb
    )
    {
        var definition = intent.Definition;
        var exists = TableExists(definition.Table);
        var baseTable = BaseTableExists(definition.Table);
        var matching = intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? baseTable
            : BuildTableMatches(definition, isMariaDb);

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
        bool isMariaDb
    )
    {
        var conditions = new List<string>
        {
            BaseTableExists(definition.Table),
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(definition.Table)}) "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}",
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
            + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(definition.Table)} "
            + "AND tc.CONSTRAINT_TYPE = 'UNIQUE') "
            + $"= {definition.UniqueConstraints.Count.ToString(CultureInfo.InvariantCulture)}",
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
            + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(definition.Table)} "
            + "AND tc.CONSTRAINT_TYPE = 'CHECK') "
            + $"= {definition.CheckConstraints.Count.ToString(CultureInfo.InvariantCulture)}",
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
            + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(definition.Table)} "
            + "AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY') "
            + $"= {definition.ForeignKeys.Count.ToString(CultureInfo.InvariantCulture)}",
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

        conditions.AddRange(definition.UniqueConstraints.Select(ConstraintMatches));
        conditions.AddRange(definition.CheckConstraints.Select(CheckConstraintMatches));
        conditions.AddRange(definition.ForeignKeys.Select(ForeignKeyMatches));

        return $"({string.Join(" AND ", conditions)})";
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
