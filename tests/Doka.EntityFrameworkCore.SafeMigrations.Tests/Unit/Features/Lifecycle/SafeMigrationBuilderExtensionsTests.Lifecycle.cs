namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationBuilderExtensionsTests
{
    [Fact]
    public void BuilderProducesExactlyOneClosedEnvelopeForEveryOperationKind()
    {
        var builder = new MigrationBuilder("test");
        var column = Column("value", nullable: true);
        var oldColumn = Column("value", nullable: false);
        var table = new ExpectedTableDefinition("items", [Column("id", nullable: false)]);
        var index = new ExpectedIndexDefinition(
            "ix_items_value",
            "items",
            [new ExpectedIndexKeyDefinition(column: "value")]);

        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]);
        var unique = new ExpectedUniqueConstraintDefinition("uq_items_value", "items", ["value"]);
        var check = new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0");
        var foreignKey = new ExpectedForeignKeyDefinition("fk_items_parent", "items", ["parent_id"], "parents", ["id"]);

        builder.EnsureSchemaExists("app");
        builder.DropSchemaIfExists("legacy");
        builder.EnsureTable(table, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropTableIfExists("legacy_items");
        builder.RenameTableIfExists("old_items", "items");
        builder.EnsureColumn("items", column, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("legacy", "items");
        builder.RenameColumnIfExists("old_value", "items", "value");
        builder.AlterColumnIfDifferent("items", column, oldColumn, SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(index, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropIndexIfExists("ix_legacy", "items");
        builder.RenameIndexIfExists("ix_old", "items", "ix_new");
        builder.EnsurePrimaryKey(primaryKey, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropPrimaryKeyIfExists("pk_items", "items");
        builder.EnsureUniqueConstraint(unique, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropUniqueConstraintIfExists("uq_items_value", "items");
        builder.EnsureCheckConstraint(check, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropCheckConstraintIfExists("ck_items_id", "items");
        builder.EnsureForeignKey(foreignKey, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropForeignKeyIfExists("fk_items_parent", "items");

        var operations = builder
            .Operations
            .Cast<SafeMigrationOperation>()
            .ToArray();

        Assert.Equal(
            Enum.GetValues<SafeMigrationOperationKind>()
                .Length,
            operations.Length);
        Assert.Equal(
            Enum
                .GetValues<SafeMigrationOperationKind>()
                .Order(),
            operations
                .Select(static operation => operation.Intent.Kind)
                .Order());
        Assert.All(operations, static operation => Assert.Empty(operation.GetAnnotations()));
    }
}
