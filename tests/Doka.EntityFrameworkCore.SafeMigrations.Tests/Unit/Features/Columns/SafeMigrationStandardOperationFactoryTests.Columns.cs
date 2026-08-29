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

    [Fact]
    public void EnsureColumnRepairUsesAlterOperationWithOnlyMutableOldFacetsChanged()
    {
        var target = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: false,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical",
            defaultValue: SafeMigrationDefaultValue.Literal("fallback"));

        var operation = Assert.IsType<AlterColumnOperation>(
            SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", target)));

        Assert.Equal("items", operation.Table);
        Assert.Equal("value", operation.Name);
        Assert.False(operation.IsNullable);
        Assert.Equal("canonical", operation.Comment);
        Assert.Equal("fallback", operation.DefaultValue);
        Assert.True(operation.OldColumn.IsNullable);
        Assert.Null(operation.OldColumn.Comment);
        Assert.Null(operation.OldColumn.DefaultValue);
        Assert.Null(operation.OldColumn.DefaultValueSql);
        Assert.Equal(operation.ColumnType, operation.OldColumn.ColumnType);
        Assert.Equal(operation.MaxLength, operation.OldColumn.MaxLength);
    }

    [Fact]
    public void EnsureColumnRepairRejectsProviderOwnedDefinitions()
    {
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
        };

        providerOperation["Test:Identity"] = "identity";
        var definition = SafeMigrationExpectedDefinitionFactory.From(providerOperation);

        Assert.Throws<NotSupportedException>(() =>
            SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", definition)));
    }
}
