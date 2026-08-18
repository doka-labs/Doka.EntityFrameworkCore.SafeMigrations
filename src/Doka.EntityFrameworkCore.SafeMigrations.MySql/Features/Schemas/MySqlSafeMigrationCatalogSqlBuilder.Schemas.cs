namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private static string? GetUnsupportedSchemaFeature(
        SafeMigrationIntent intent
    )
    {
        var schema = intent switch
        {
            EnsureTableIntent value => value.Definition.Schema,
            DropTableIntent value => value.Schema,
            RenameTableIntent value => value.Schema ?? value.NewSchema,
            EnsureColumnIntent value => value.Schema,
            DropColumnIntent value => value.Schema,
            RenameColumnIntent value => value.Schema,
            AlterColumnIntent value => value.Schema,
            EnsureIndexIntent value => value.Definition.Schema,
            DropIndexIntent value => value.Schema,
            RenameIndexIntent value => value.Schema,
            EnsurePrimaryKeyIntent value => value.Definition.Schema,
            DropPrimaryKeyIntent value => value.Schema,
            EnsureUniqueConstraintIntent value => value.Definition.Schema,
            DropUniqueConstraintIntent value => value.Schema,
            EnsureCheckConstraintIntent value => value.Definition.Schema,
            DropCheckConstraintIntent value => value.Schema,
            EnsureForeignKeyIntent value => value.Definition.Schema ?? value.Definition.PrincipalSchema,
            DropForeignKeyIntent value => value.Schema,
            _ => null,
        };
        return schema is null ? null : "schema_qualified_object";
    }

    private static MySqlSafeMigrationRuntimePlan BuildEnsureSchema(
        EnsureSchemaIntent intent
    ) => Unsupported("schema_operations");

    private static MySqlSafeMigrationRuntimePlan BuildDropSchema(
        DropSchemaIntent intent
    ) => Unsupported("schema_operations");
}
