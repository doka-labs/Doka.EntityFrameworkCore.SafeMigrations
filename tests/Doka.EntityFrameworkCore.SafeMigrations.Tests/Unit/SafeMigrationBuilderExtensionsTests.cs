namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationBuilderExtensionsTests
{
    private static ExpectedColumnDefinition Column(
        string name,
        bool nullable
    ) => new(name, typeof(string), nullable, storeType: "varchar(100)", maxLength: 100);
}
