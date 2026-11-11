namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects complete table-definition predicates at narrow and wide column counts.</summary>
public sealed class PostgreSqlCatalogSqlAllocationTests
{
    private const string OfflineConnectionString =
        "Host=127.0.0.1;Port=1;Database=allocation_bound;Username=test;Password=test";

    /// <summary>Composition includes every column and shares the exact execution postcondition.</summary>
    /// <param name="columnCount">The number of independently named expected columns.</param>
    [Theory]
    [InlineData(4)]
    [InlineData(64)]
    public void TableDefinitionComposition_PreservesEveryExpectedColumn(
        int columnCount
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnectionString, registerSafeMigrations: false);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var helper = context.GetService<ISqlGenerationHelper>();
        var operation = BuildOperation(context, columnCount);

        // Act
        var plan = new PostgreSqlSafeMigrationCatalogSqlBuilder(mappings, helper).Build(operation);

        // Assert
        Assert.Same(plan.Postcondition, plan.ExecutionPostcondition);
        Assert.Contains(plan.Postcondition, plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(
            "AND a.attnum > 0 AND NOT a.attisdropped) = " + columnCount.ToString(CultureInfo.InvariantCulture),
            plan.Postcondition,
            StringComparison.Ordinal);

        for (var index = 0; index < columnCount; index++)
        {
            Assert.Contains(
                "'column_" + index.ToString(CultureInfo.InvariantCulture) + "'",
                plan.Postcondition,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Creates the strict immutable table contract without opening a connection.</summary>
    private static SafeMigrationOperation BuildOperation(
        SafeMigrationDbContext context,
        int columnCount
    )
    {
        var columns = Enumerable
            .Range(0, columnCount)
            .Select(static index => new ExpectedColumnDefinition(
                "column_" + index.ToString(CultureInfo.InvariantCulture),
                typeof(int),
                false,
                "integer"))
            .ToArray();

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureTable(
            new ExpectedTableDefinition("allocation_table", columns),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        return (SafeMigrationOperation)migration.Operations[0];
    }
}
