namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureSchema(EnsureSchemaIntent intent)
    {
        var exists = SchemaExists(intent.Name);

        return Plan(
            $"CASE WHEN {exists} THEN N'matching' ELSE N'missing' END",
            Bit(exists));
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropSchema(DropSchemaIntent intent)
    {
        var exists = SchemaExists(intent.Name);
        var schemaId = $"SCHEMA_ID({Literal(intent.Name)})";
        var reserved = $"{schemaId} IN (1, 2, 3, 4)";
        var empty = $"NOT EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = {schemaId}) "
            + $"AND NOT EXISTS (SELECT 1 FROM sys.types t WHERE t.schema_id = {schemaId} AND t.is_user_defined = 1) "
            + $"AND NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections x WHERE x.schema_id = {schemaId})";

        return Plan(
            $"CASE WHEN {reserved} THEN N'unsupported' WHEN NOT {exists} THEN N'missing' "
            + $"WHEN {empty} THEN N'matching' ELSE N'different' END",
            Bit($"NOT {exists}")) with
        {
            ClassificationCodeExpression = $"CASE WHEN {reserved} THEN N'reserved_schema' ELSE NULL END",
        };
    }

    private string SchemaExists(string schema)
        => $"EXISTS (SELECT 1 FROM sys.schemas s WHERE s.name = {Literal(schema)})";

    private static bool IsReservedSchema(string schema)
        => schema.TrimEnd(' ').ToUpperInvariant() is "DBO" or "GUEST" or "SYS" or "INFORMATION_SCHEMA";
}
