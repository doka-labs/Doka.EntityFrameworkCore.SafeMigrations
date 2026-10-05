namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureUnique(EnsureUniqueConstraintIntent intent)
        => BuildEnsureKey(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name,
            intent.Definition.Columns, "UQ");

    private SqlServerSafeMigrationRuntimePlan BuildDropUnique(DropUniqueConstraintIntent intent)
        => BuildDropKey(intent.Table, intent.Schema, intent.Name, "UQ");
}
