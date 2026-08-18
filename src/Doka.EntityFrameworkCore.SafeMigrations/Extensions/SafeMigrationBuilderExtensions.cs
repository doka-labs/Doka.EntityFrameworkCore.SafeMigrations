namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Adds fail-closed SafeMigrations operations to an EF Core
/// <see cref="MigrationBuilder"/>.
/// </summary>
public static partial class SafeMigrationBuilderExtensions
{
    private static OperationBuilder<SafeMigrationOperation> Add(
        MigrationBuilder migrationBuilder,
        SafeMigrationIntent intent,
        SafeMigrationPolicy policy
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        var operation = new SafeMigrationOperation(intent, policy);
        migrationBuilder.Operations.Add(operation);
        return new OperationBuilder<SafeMigrationOperation>(operation);
    }
}
