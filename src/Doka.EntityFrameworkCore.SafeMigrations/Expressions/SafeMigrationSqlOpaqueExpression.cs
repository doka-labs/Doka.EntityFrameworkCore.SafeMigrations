namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents opaque SQL that cannot prove catalog equivalence.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlOpaqueExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes opaque SQL.</summary>
    /// <param name="sql">The opaque SQL text.</param>
    public SafeMigrationSqlOpaqueExpression(
        string sql
    ) : this(sql, followsIdentifierRename: false) { }

    internal SafeMigrationSqlOpaqueExpression(
        string sql,
        bool followsIdentifierRename
    )
    {
        Sql = SafeMigrationDefinitionValidator.Required(sql, nameof(sql));
        FollowsIdentifierRename = followsIdentifierRename;
    }

    /// <summary>Gets the opaque SQL.</summary>
    public string Sql { get; }

    /// <summary>Gets whether an identifier rename made this expression unproven.</summary>
    public bool FollowsIdentifierRename { get; }
}
