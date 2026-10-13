namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Resolves native member occupancy independently of missing-source replay classification.</summary>
    /// <param name="intent">The immutable table, column or index rename.</param>
    /// <returns>The exact namespace-bound catalog predicate used by the corresponding runtime guard.</returns>
    internal string BuildRenameDestinationExistsExpression(SafeMigrationIntent intent) => intent switch
    {
        RenameTableIntent table => BuildRenameTargetExistsExpression(table),
        RenameColumnIntent column => ColumnExists(column.Table, column.Schema, column.NewName),
        // WHY: PostgreSQL indexes share the schema's relation namespace, even
        // when an occupant belongs to another table or is not an index at all.
        RenameIndexIntent index => RelationNameExists(index.NewName, index.Schema),
        _ => throw new ArgumentException("The operation is not a supported rename.", nameof(intent)),
    };

    /// <summary>Separates index source ownership/kind from a destination-only collision.</summary>
    /// <param name="intent">The immutable index rename with schema and physical owner.</param>
    /// <returns>Whether the existing source is the independently owned index this operation can rename.</returns>
    internal string BuildIndexRenameSourceSupportedExpression(RenameIndexIntent intent) =>
        IndependentIndexExists(intent.Name, intent.Schema, intent.Table);
}
