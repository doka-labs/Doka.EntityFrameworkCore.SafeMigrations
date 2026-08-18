namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    private static IReadOnlyList<SafeMigrationIntent> CreateIntents()
    {
        var column = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(100)");
        var table = new ExpectedTableDefinition("items", [column]);
        return
        [
            new EnsureSchemaIntent("app"),
            new DropSchemaIntent("app"),
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            new DropTableIntent("items"),
            new RenameTableIntent("old_items", "items"),
            new EnsureColumnIntent("items", column),
            new DropColumnIntent("value", "items"),
            new RenameColumnIntent("old_value", "items", "value"),
            new AlterColumnIntent("items", column, column),
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_value",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "value")])),
            new DropIndexIntent("ix_items_value", "items"),
            new RenameIndexIntent("ix_old", "items", "ix_new"),
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["value"])),
            new DropPrimaryKeyIntent("pk_items", "items"),
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_items_value", "items", ["value"])),
            new DropUniqueConstraintIntent("uq_items_value", "items"),
            new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("ck_items", "items", "value IS NOT NULL")),
            new DropCheckConstraintIntent("ck_items", "items"),
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition("fk_items_parent", "items", ["value"], "parents", ["id"])),
            new DropForeignKeyIntent("fk_items_parent", "items"),
        ];
    }
}
