using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class PostgreSqlSafeMigrationsSqlGeneratorTests
{
    [Fact]
    public void AddColumnIfNotExists_UsesPostgreSqlNativeSyntax()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "display_name",
            table: "employees",
            type: "text",
            nullable: true);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("ADD COLUMN IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE employees", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddForeignKeyIfNotExists_UsesDoBlockGuard()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddForeignKeyIfNotExists(
            name: "fk_employees_departments_department_id",
            table: "employees",
            columns: ["department_id"],
            principalTable: "departments",
            principalColumns: ["id"]);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_StrictMode_UsesMismatchGuard()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "display_name",
            table: "employees",
            type: "text",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.Contains("ELSIF EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE", sql, StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_StrictMode_WithStringDefault_StripsCatalogTypeCast()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "status",
            table: "employees",
            type: "text",
            nullable: false,
            defaultValue: "active",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("regexp_replace", sql, StringComparison.Ordinal);
        Assert.Contains("::[A-Za-z0-9_", sql, StringComparison.Ordinal);
        Assert.Contains("pg_get_expr(d.adbin, d.adrelid)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_PreflightOnly_DoesNotEmitAddColumnDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "display_name",
            table: "employees",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true),
            type: "text",
            nullable: true);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN IF NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_StrictMode_UsesCatalogComparison()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "ix_employees_email",
            table: "employees",
            columns: ["email"],
            unique: true,
            filter: "\"email\" IS NOT NULL",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("pg_index", sql, StringComparison.Ordinal);
        Assert.Contains("pg_get_expr(i.indpred, i.indrelid)", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_RepairMode_UsesCatalogComparison()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "ix_employees_email",
            table: "employees",
            columns: ["email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            unique: true,
            filter: "\"email\" IS NOT NULL");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("pg_index", sql, StringComparison.Ordinal);
        Assert.Contains("pg_get_expr(i.indpred, i.indrelid)", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_PreflightOnly_DoesNotEmitCreateIndexDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "ix_employees_email",
            table: "employees",
            columns: ["email"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.ThrowIfDifferent,
                PreflightOnly: true));

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE UNIQUE INDEX IF NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlterColumnIfDifferent_UsesMissingGuardAndAlterStatement()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "display_name",
            table: "employees",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(50)",
            oldNullable: true);

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.Contains("Safe migration alter-if-different target column ''display_name''", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameTableIfExists_UsesDoBlockGuard()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameTableIfExists(
            name: "employees",
            newName: "team_members");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.tables", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("RENAME TO", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddUniqueConstraintIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT \"AK_Employees_Email\" UNIQUE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPrimaryKeyIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PK_Employees",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT \"PK_Employees\" PRIMARY KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddForeignKeyIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT \"FK_Employees_Departments_DepartmentId\" FOREIGN KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCheckConstraintIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT \"CK_Employees_Age\" CHECK", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameIndexIfExists_UsesDoBlockGuard()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameIndexIfExists(
            name: "IX_Employees_DisplayName",
            newName: "IX_Employees_FullName",
            table: "Employees");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DO $SAFE$", sql, StringComparison.Ordinal);
        Assert.Contains("pg_index", sql, StringComparison.Ordinal);
        Assert.Contains("IX_Employees_DisplayName", sql, StringComparison.Ordinal);
        Assert.Contains("IX_Employees_FullName", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DropSchemaIfExists_UsesNativeIfExistsSyntax()
    {
        using var context = new PostgreSqlTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.DropSchemaIfExists("tenant_one");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(command => command.CommandText));

        Assert.Contains("DROP SCHEMA IF EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_one", sql, StringComparison.Ordinal);
    }

    private sealed class PostgreSqlTestContext : DbContext
    {
        // No real connection is made — this context exists solely to configure the provider for IMigrationsSqlGenerator resolution.
        private const string _testConnectionStringPlaceholder = "Host=localhost;Database=test;Username=test;Password=;";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .UseNpgsql(_testConnectionStringPlaceholder)
                .UsePostgreSqlSafeMigrations();
        }
    }
}
