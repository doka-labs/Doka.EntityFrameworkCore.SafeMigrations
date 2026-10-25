namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    /// <summary>Shares known-table member renaming without inferring live or opaque dependency shapes.</summary>
    internal static bool TryProjectKnownColumnRename(
        ExpectedTableDefinition definition,
        RenameColumnIntent rename,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        [NotNullWhen(true)] out ExpectedTableDefinition? renamed
    )
    {
        var table = new ProjectedTable(
            definition,
            dataMutationVersion: 0,
            normalizer,
            sharesUniqueIndexIdentity: false);

        renamed = null;
        if (!table.Columns.ContainsKey(rename.Name)
            || table.Columns.ContainsKey(rename.NewName))
        {
            return false;
        }

        table.RenameColumn(rename.Name, rename.NewName);
        foreach (var column in table.Columns.Values)
        {
            if (column.ComputedExpression is not null
                && !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(column.ComputedExpression))
            {
                return false;
            }
        }

        foreach (var check in table.CheckConstraints.Values)
        {
            if (check.Expression is not null
                && !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(check.Expression))
            {
                return false;
            }
        }

        renamed = table.Definition;

        return true;
    }
}
