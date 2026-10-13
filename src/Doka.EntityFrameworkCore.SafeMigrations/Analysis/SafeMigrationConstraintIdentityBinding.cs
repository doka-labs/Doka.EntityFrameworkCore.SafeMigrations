namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Separately certifies a table-matched constraint's authored alias and actual physical name.</summary>
/// <param name="Kind">The member family whose provider semantics were matched.</param>
/// <param name="AuthoredName">The exact alias in the accepted table contract.</param>
/// <param name="PhysicalName">The actual named catalog member; anonymous constraints cannot supply this proof.</param>
internal readonly record struct SafeMigrationConstraintIdentityBinding(
    SafeMigrationDatabaseObjectKind Kind,
    string AuthoredName,
    string PhysicalName
);
