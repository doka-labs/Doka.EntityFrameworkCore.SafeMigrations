namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationBuilderExtensionsTests
{
    [Fact]
    public void FamiliarCreateTableApiCapturesCompleteImmutableDefinition()
    {
        var builder = new MigrationBuilder("test");
        builder.CreateTableIfNotExists(
            "orders",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
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
            },
            comment: "orders");

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var intent = Assert.IsType<EnsureTableIntent>(operation.Intent);
        Assert.Equal(2, intent.Definition.Columns.Count);
        Assert.NotNull(intent.Definition.PrimaryKey);
        Assert.Single(intent.Definition.UniqueConstraints);
        Assert.Single(intent.Definition.CheckConstraints);
        Assert.Equal("orders", intent.Definition.Comment);
        Assert.Equal(SafeMigrationDefaultValueKind.Literal, intent.Definition.Columns[1].DefaultValue.Kind);
    }
}
