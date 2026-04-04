using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class MariaDbSafeMigrationsSqlGeneratorTests
{
    [Fact]
    public void AddColumnIfNotExists_UsesMariaDbNativeSyntax()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("ADD COLUMN IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE `Employees`", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DropPrimaryKeyIfExists_UsesMetadataGuard()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.DropPrimaryKeyIfExists(table: "Employees");

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("information_schema.TABLE_CONSTRAINTS", sql, StringComparison.Ordinal);
        Assert.Contains("DROP PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("PREPARE safe_migrations_stmt", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_StrictMode_UsesComparisonGuardAndSignal()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.COLUMNS", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
        Assert.Contains(
            "Safe migration strict-mode mismatch for column ''DisplayName''",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_StrictMode_WithStringDefault_UsesNormalizedDefaultCandidates()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "Status",
            table: "Employees",
            type: "varchar(20)",
            nullable: false,
            defaultValue: "active",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("COLUMN_DEFAULT", sql, StringComparison.Ordinal);
        Assert.Contains(" IN (", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COALESCE(COLUMN_DEFAULT, '') = 'active'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddColumnIfNotExists_PreflightOnly_DoesNotEmitAddColumnDdl()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true),
            type: "varchar(200)",
            nullable: true);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN IF NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_StrictMode_UsesStatisticsComparison()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("information_schema.STATISTICS", sql, StringComparison.Ordinal);
        Assert.Contains("COLUMN_LIST", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_StrictMode_WithFilter_ThrowsNotSupported()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            filter: "`DisplayName` IS NOT NULL",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            Assert.Throws<NotSupportedException>(() => generator.Generate(migrationBuilder.Operations, context.Model));

        Assert.Contains("does not support filtered indexes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IX_Employees_DisplayName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_NonStrict_WithFilter_DoesNotThrow()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            filter: "`DisplayName` IS NOT NULL",
            strictMode: SafeMigrationStrictMode.None);

        var commands = generator.Generate(migrationBuilder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(command => command.CommandText));

        Assert.NotEmpty(commands);
        Assert.DoesNotContain("safe_migrations_guard", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_RepairMode_UsesComparisonGuard()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("information_schema.STATISTICS", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexIfNotExists_PreflightOnly_DoesNotEmitCreateIndexDdl()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.ThrowIfDifferent,
                PreflightOnly: true));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE UNIQUE INDEX IF NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlterColumnIfDifferent_UsesMissingGuardAndAlterStatement()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "varchar(50)",
            oldNullable: true);

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.COLUMNS", sql, StringComparison.Ordinal);
        Assert.Contains(
            "Safe migration alter-if-different target column ''DisplayName''",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE `Employees`", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddUniqueConstraintIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT `AK_Employees_Email` UNIQUE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPrimaryKeyIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PRIMARY",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD PRIMARY KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPrimaryKeyIfNotExists_RepairMode_UsesMariaDbPrimaryKeySyntax()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PRIMARY",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("ADD PRIMARY KEY (`Id`)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT `PRIMARY` PRIMARY KEY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AddForeignKeyIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new MariaDbTestContext();
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

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ADD CONSTRAINT `FK_Employees_Departments_DepartmentId` FOREIGN KEY",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddCheckConstraintIfNotExists_PreflightOnly_DoesNotEmitAddConstraintDdl()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "`Age` >= 18",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE PROCEDURE `safe_migrations_guard`()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT `CK_Employees_Age` CHECK", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameColumnIfExists_UsesMetadataGuard()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameColumnIfExists(name: "DisplayName", table: "Employees", newName: "FullName");

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("information_schema.COLUMNS", sql, StringComparison.Ordinal);
        Assert.Contains("FullName", sql, StringComparison.Ordinal);
        Assert.Contains("DisplayName", sql, StringComparison.Ordinal);
        Assert.Contains("PREPARE safe_migrations_stmt", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameIndexIfExists_UsesMetadataGuard()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameIndexIfExists(
            name: "IX_Employees_DisplayName",
            newName: "IX_Employees_FullName",
            table: "Employees");

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("information_schema.STATISTICS", sql, StringComparison.Ordinal);
        Assert.Contains("IX_Employees_DisplayName", sql, StringComparison.Ordinal);
        Assert.Contains("IX_Employees_FullName", sql, StringComparison.Ordinal);
        Assert.Contains("PREPARE safe_migrations_stmt", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DropSchemaIfExists_UsesNativeIfExistsSyntax()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.DropSchemaIfExists("tenant_one");

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("DROP SCHEMA IF EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_one", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSchemaExists_UsesNativeIfNotExistsSyntax()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.EnsureSchemaExists("tenant_one");

        var sql = string.Join(
            "\n",
            generator
                .Generate(migrationBuilder.Operations, context.Model)
                .Select(command => command.CommandText));

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_one", sql, StringComparison.Ordinal);
    }
}
