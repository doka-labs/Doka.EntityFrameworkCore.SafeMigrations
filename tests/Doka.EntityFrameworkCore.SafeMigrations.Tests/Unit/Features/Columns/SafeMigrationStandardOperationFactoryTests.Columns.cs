namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    [Fact]
    public void LiteralNull_RemainsDistinctFromNoDefaultInEfOperation()
    {
        var withLiteralNull = Assert.IsType<AddColumnOperation>(
            SafeMigrationStandardOperationFactory.Create(
                new EnsureColumnIntent(
                    "items",
                    new ExpectedColumnDefinition(
                        "value",
                        typeof(string),
                        true,
                        defaultValue: SafeMigrationDefaultValue.Literal(null)))));

        var withoutDefault = Assert.IsType<AddColumnOperation>(
            SafeMigrationStandardOperationFactory.Create(
                new EnsureColumnIntent("items", new ExpectedColumnDefinition("other", typeof(string), true))));

        Assert.Equal("NULL", withLiteralNull.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValue);
    }
}
