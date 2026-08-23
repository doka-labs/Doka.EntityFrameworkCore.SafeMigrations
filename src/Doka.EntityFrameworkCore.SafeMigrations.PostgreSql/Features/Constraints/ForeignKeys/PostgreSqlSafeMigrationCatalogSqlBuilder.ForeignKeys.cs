namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureForeignKey(
        EnsureForeignKeyIntent intent
    ) => BuildEnsureConstraint(
        intent.Definition.Table,
        intent.Definition.Schema,
        intent.Definition.Name,
        'f',
        ForeignKeyMatches(intent.Definition),
        ForeignKeyDataBlocked(intent.Definition),
        TableExists(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema));

    private PostgreSqlSafeMigrationRuntimePlan BuildDropForeignKey(
        DropForeignKeyIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'f');

    private string ForeignKeyDataBlocked(
        ExpectedForeignKeyDefinition definition
    )
    {
        var localNotNull = string.Join(
            " AND ",
            definition.Columns.Select(column => $"d.{Delimited(column)} IS NOT NULL"));

        var join = string.Join(
            " AND ",
            definition.Columns.Zip(
                definition.PrincipalColumns,
                (local, principal) => $"d.{Delimited(local)} = p.{Delimited(principal)}"));

        return $"EXISTS (SELECT 1 FROM {Qualified(definition.Table, definition.Schema)} d "
            + $"LEFT JOIN {Qualified(definition.PrincipalTable, definition.PrincipalSchema)} p ON {join} "
            + $"WHERE {localNotNull} AND p.{Delimited(definition.PrincipalColumns[0])} IS NULL LIMIT 1)";
    }

    private string ForeignKeyMatches(
        ExpectedForeignKeyDefinition definition
    ) => ConstraintBase(definition.Table, definition.Schema, definition.Name, 'f')
        + $" AND ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(definition.Columns)} "
        + $"AND co.confrelid = {QualifiedRegclass(definition.PrincipalTable, definition.PrincipalSchema)} "
        + $"AND ARRAY(SELECT a.attname FROM unnest(co.confkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.confrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(definition.PrincipalColumns)} "
        + $"AND co.confupdtype = {Literal(ReferentialCode(definition.OnUpdate))}::\"char\" "
        + $"AND co.confdeltype = {Literal(ReferentialCode(definition.OnDelete))}::\"char\" "
        + "AND co.convalidated)";

    private static string ReferentialCode(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.NoAction => "a",
        ReferentialAction.Restrict => "r",
        ReferentialAction.Cascade => "c",
        ReferentialAction.SetNull => "n",
        ReferentialAction.SetDefault => "d",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
