namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureSchema(
        EnsureSchemaIntent intent
    )
    {
        var exists = SchemaExists(intent.Name);
        return Plan($"CASE WHEN {exists} THEN 'matching' ELSE 'missing' END", exists);
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropSchema(
        DropSchemaIntent intent
    )
    {
        var exists = SchemaExists(intent.Name);
        return Plan($"CASE WHEN {exists} THEN 'matching' ELSE 'missing' END", $"NOT {exists}");
    }

    private string SchemaExists(
        string schema
    ) => $"EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname = {Literal(schema)})";
}
