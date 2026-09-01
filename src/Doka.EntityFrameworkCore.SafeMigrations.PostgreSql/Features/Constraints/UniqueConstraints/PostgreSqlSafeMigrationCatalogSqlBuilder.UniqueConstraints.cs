namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureUnique(
        EnsureUniqueConstraintIntent intent
    ) => BuildEnsureConstraint(
        intent.Definition.Table,
        intent.Definition.Schema,
        intent.Definition.Name,
        'u',
        ConstraintColumnsMatch(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Name,
            'u',
            intent.Definition.Columns),
        UniqueConstraintDataBlocked(intent.Definition),
        identityConflict: ConstraintColumnsMatch(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Name,
            'u',
            intent.Definition.Columns,
            requireExpectedName: false,
            requireLocalIdentity: false),
        identityConflictCode: "unique_constraint_semantic_identity_conflict");

    private PostgreSqlSafeMigrationRuntimePlan BuildDropUnique(
        DropUniqueConstraintIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'u');

    private string UniqueConstraintDataBlocked(
        ExpectedUniqueConstraintDefinition definition
    )
    {
        var keys = definition
            .Columns
            .Select(Delimited)
            .ToArray();

        var nonNull = string.Join(" AND ", keys.Select(static key => $"{key} IS NOT NULL"));

        return DuplicateDataExists(definition.Table, definition.Schema, keys, nonNull);
    }
}
