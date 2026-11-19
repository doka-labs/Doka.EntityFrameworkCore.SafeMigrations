namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Verifies authoring helpers capture one complete immutable intent and reject incomplete contracts before publication.
/// </summary>
public sealed partial class SafeMigrationBuilderExtensionsTests
{
    private static ExpectedColumnDefinition Column(
        string name,
        bool nullable
    ) => new(name, typeof(string), nullable, storeType: "varchar(100)", maxLength: 100);
}
