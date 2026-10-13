namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Resolves owner-local member occupancy without relying on missing-source classification.</summary>
    /// <param name="intent">The immutable column or index rename destination.</param>
    /// <returns>The native bit-valued catalog occupancy predicate.</returns>
    internal string BuildMemberRenameDestinationExpression(SafeMigrationIntent intent) => intent switch
    {
        RenameColumnIntent column => Bit(ColumnExists(column.Table, column.Schema, column.NewName)),
        // WHY: SQL Server index names are local to the physical table; an
        // identically named index on another owner is not a collision.
        RenameIndexIntent index => Bit(IndexExists(index.NewName, index.Table, index.Schema)),
        _ => throw new ArgumentException("The operation is not a supported member rename.", nameof(intent)),
    };

    /// <summary>Separates index source ownership/kind from a destination-only collision.</summary>
    /// <param name="intent">The immutable index rename with schema and physical owner.</param>
    /// <returns>Whether the existing source is an independently owned index the operation can rename.</returns>
    internal string BuildIndexRenameSourceSupportedExpression(RenameIndexIntent intent) =>
        Bit(IndependentIndexExists(intent.Name, intent.Table, intent.Schema));
}
