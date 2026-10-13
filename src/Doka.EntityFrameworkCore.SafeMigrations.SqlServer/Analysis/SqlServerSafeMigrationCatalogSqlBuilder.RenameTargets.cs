namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Resolves owner-local member occupancy without relying on missing-source classification.</summary>
    /// <param name="intent">The immutable column or index rename destination.</param>
    /// <returns>The native bit-valued catalog occupancy predicate.</returns>
    internal string BuildMemberRenameDestinationExpression(SafeMigrationIntent intent) => intent switch
    {
        RenameColumnIntent column => MemberRenameBit(ColumnExists(column.Table, column.Schema, column.NewName)),
        // WHY: SQL Server index names are local to the physical table; an
        // identically named index on another owner is not a collision.
        RenameIndexIntent index => MemberRenameBit(IndexExists(index.NewName, index.Table, index.Schema)),
        _ => throw new ArgumentException("The operation is not a supported member rename.", nameof(intent)),
    };

    /// <summary>Separates index source ownership/kind from a destination-only collision.</summary>
    /// <param name="intent">The immutable index rename with schema and physical owner.</param>
    /// <returns>Whether the existing source is an independently owned index the operation can rename.</returns>
    internal string BuildIndexRenameSourceSupportedExpression(RenameIndexIntent intent) =>
        MemberRenameBit(IndependentIndexExists(intent.Name, intent.Table, intent.Schema));

    /// <summary>Renders a native Boolean scalar for the strictly typed member-probe reader.</summary>
    /// <param name="predicate">The provider-built catalog predicate.</param>
    /// <returns>A bit-valued CASE expression compatible with nullable bit UNION branches.</returns>
    private static string MemberRenameBit(string predicate)
    {
        // WHY: The shared Bit renderer intentionally returns integer 0/1 for
        // runtime comparisons. These probes need SqlClient Boolean metadata;
        // otherwise integer CASE branches also promote nullable bit UNION rows.
        return $"CAST(CASE WHEN {predicate} THEN 1 ELSE 0 END AS bit)";
    }
}
