namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationBuilderExtensionsTests
{
    /// <summary>
    /// Captures every table member and explicit FK endpoint/action through the familiar EF authoring callback
    /// shape.
    /// </summary>
    [Fact]
    public void FamiliarCreateTableApiCapturesCompleteImmutableDefinition()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        builder.CreateTableIfNotExists(
            "orders",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                CustomerId = table.Column<int>(type: "int", nullable: false),
                Code = table.Column<string>(
                    type: "varchar(40)",
                    maxLength: 40,
                    nullable: false,
                    defaultValue: "new",
                    comment: "business code"),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_orders", value => value.Id);
                table.UniqueConstraint("uq_orders_code", value => value.Code);
                table.CheckConstraint("ck_orders_id", "id > 0");
                table.ForeignKey(
                    "fk_orders_customers",
                    value => value.CustomerId,
                    principalTable: "customers",
                    principalColumn: "id",
                    onUpdate: ReferentialAction.Cascade,
                    onDelete: ReferentialAction.Restrict);
            },
            comment: "orders");

        // Assert
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var intent = Assert.IsType<EnsureTableIntent>(operation.Intent);
        var foreignKey = Assert.Single(intent.Definition.ForeignKeys);

        Assert.Equal(3, intent.Definition.Columns.Count);
        Assert.NotNull(intent.Definition.PrimaryKey);
        Assert.Single(intent.Definition.UniqueConstraints);
        Assert.Single(intent.Definition.CheckConstraints);
        Assert.Equal("fk_orders_customers", foreignKey.Name);
        Assert.Equal(["CustomerId"], foreignKey.Columns);
        Assert.Equal("customers", foreignKey.PrincipalTable);
        Assert.Equal(["id"], foreignKey.PrincipalColumns);
        Assert.Equal(ReferentialAction.Cascade, foreignKey.OnUpdate);
        Assert.Equal(ReferentialAction.Restrict, foreignKey.OnDelete);
        Assert.Equal("orders", intent.Definition.Comment);
        Assert.Equal(SafeMigrationDefaultValueKind.Literal, intent.Definition.Columns[2].DefaultValue.Kind);
    }

    /// <summary>
    /// Rejects a table FK whose captured provider operation omitted the principal member contract.
    /// </summary>
    [Fact]
    public void TableDefinitionFactoryRequiresExplicitForeignKeyPrincipalColumns()
    {
        // Arrange
        var operation = new CreateTableOperation
        {
            Name = "orders",
        };

        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "customer_id",
                Table = "orders",
                ClrType = typeof(int),
                IsNullable = false,
            });

        operation.ForeignKeys.Add(
            new AddForeignKeyOperation
            {
                Name = "fk_orders_customers",
                Table = "orders",
                Columns = ["customer_id"],
                PrincipalTable = "customers",
            });

        // Act
        var failure = Record.Exception(() =>
            SafeMigrationExpectedDefinitionFactory.From(operation));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);

        Assert.Contains("explicit principal columns", exception.Message, StringComparison.Ordinal);
    }
}
