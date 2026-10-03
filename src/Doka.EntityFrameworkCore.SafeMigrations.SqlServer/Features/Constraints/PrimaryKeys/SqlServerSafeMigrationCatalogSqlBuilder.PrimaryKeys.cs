namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsurePrimaryKey(EnsurePrimaryKeyIntent intent)
        => BuildEnsureKey(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name,
            intent.Definition.Columns, "PK");

    private SqlServerSafeMigrationRuntimePlan BuildDropPrimaryKey(DropPrimaryKeyIntent intent)
        => BuildDropKey(intent.Table, intent.Schema, intent.Name, "PK");
}
