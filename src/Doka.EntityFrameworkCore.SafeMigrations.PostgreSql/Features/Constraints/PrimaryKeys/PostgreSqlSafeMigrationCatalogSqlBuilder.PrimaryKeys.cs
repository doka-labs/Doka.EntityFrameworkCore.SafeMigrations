namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsurePrimaryKey(
        EnsurePrimaryKeyIntent intent
    ) => BuildEnsureConstraint(
        intent.Definition.Table,
        intent.Definition.Schema,
        intent.Definition.Name,
        'p',
        ConstraintColumnsMatch(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Name,
            'p',
            intent.Definition.Columns),
        PrimaryKeyDataBlocked(intent.Definition),
        identityConflict: AnyConstraint(intent.Definition.Table, intent.Definition.Schema, 'p'),
        identityConflictCode: "primary_key_identity_conflict");

    private PostgreSqlSafeMigrationRuntimePlan BuildDropPrimaryKey(
        DropPrimaryKeyIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'p');

    private string PrimaryKeyDataBlocked(
        ExpectedPrimaryKeyDefinition definition
    )
    {
        var nulls = string.Join(" OR ", definition.Columns.Select(column => $"{Delimited(column)} IS NULL"));

        return $"(EXISTS (SELECT 1 FROM {Qualified(definition.Table, definition.Schema)} "
            + $"WHERE {nulls} LIMIT 1) OR "
            + DuplicateDataExists(definition.Table, definition.Schema, definition.Columns.Select(Delimited), "TRUE")
            + ")";
    }
}
