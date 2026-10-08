namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that ordinary column matching does not certify legacy RULE or encrypted storage.</summary>
public sealed class SqlServerColumnStorageContractTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=column_storage_contract;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Every column-matching entry point includes both unsupported-storage facets.</summary>
    /// <param name="kind">The standalone or table-level matching entry point.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.EnsureTable)]
    public void MatchingContract_RejectsBoundRuleAndEncryptedStorage(SafeMigrationOperationKind kind)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var column = new ExpectedColumnDefinition("Value", typeof(int), false, "int");
        SafeMigrationIntent intent = kind switch
        {
            SafeMigrationOperationKind.EnsureColumn => new EnsureColumnIntent("items", column),
            SafeMigrationOperationKind.AlterColumn => new AlterColumnIntent("items", column, column),
            SafeMigrationOperationKind.EnsureTable => new EnsureTableIntent(
                new ExpectedTableDefinition("items", [column]), SafeMigrationTableMode.StrictDefinition),
            _ => throw new UnreachableException(),
        };

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Contains("c.rule_object_id = 0", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("c.encryption_type IS NULL", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("c.rule_object_id = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.encryption_type IS NULL", plan.StateExpression, StringComparison.Ordinal);
    }
}
