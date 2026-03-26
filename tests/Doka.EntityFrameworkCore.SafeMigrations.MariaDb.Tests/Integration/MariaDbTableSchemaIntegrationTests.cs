namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public sealed class MariaDbTableSchemaIntegrationTests : MariaDbIntegrationTestBase
{
    public MariaDbTableSchemaIntegrationTests(MariaDbContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CreateTableIfNotExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await using var context = new SafeMigrationDbContext(connectionString);

        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateTableIfNotExists(
            table: "Employees",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                DisplayName = table.Column<string>(type: "varchar(200)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Employees", x => x.Id));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConsolidatedInitialMigration_CanSynchronizeExistingPopulatedMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    `Name` varchar(100) NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`)
);

INSERT INTO `Departments` (`Id`, `Name`) VALUES (10, 'Engineering');
INSERT INTO `Employees` (`Id`, `Email`, `DepartmentId`) VALUES (1, 'dominic@example.com', 10);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.CreateTableIfNotExists(
            table: "Departments",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                Name = table.Column<string>(type: "varchar(100)", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Departments", x => x.Id));

        migrationBuilder.CreateTableIfNotExists(
            table: "Employees",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                Email = table.Column<string>(type: "varchar(200)", nullable: false),
                DepartmentId = table.Column<int>(nullable: false),
                DisplayName = table.Column<string>(type: "varchar(200)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Employees", x => x.Id));

        migrationBuilder.CreateTableIfNotExists(
            table: "AuditEntries",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
                EmployeeId = table.Column<int>(nullable: false),
                Message = table.Column<string>(type: "varchar(500)", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_AuditEntries", x => x.Id));

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "varchar(200)",
            nullable: true);

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            onDelete: ReferentialAction.Cascade,
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM `Employees`
WHERE `Id` = 1
  AND `Email` = 'dominic@example.com'
  AND `DepartmentId` = 10;
""";

            var employeeCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, employeeCount);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

            var columnCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, columnCount);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'AuditEntries';
""";

            var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, tableCount);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND INDEX_NAME = 'IX_Employees_DepartmentId';
""";

            var indexCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.True(indexCount >= 1);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'AK_Employees_Email'
  AND CONSTRAINT_TYPE = 'UNIQUE';
""";

            var uniqueCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, uniqueCount);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

            var foreignKeyCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, foreignKeyCount);
        }
    }

    [Fact]
    public async Task RenameTableIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.RenameTableIfExists(
            name: "Employees",
            newName: "TeamMembers");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'TeamMembers';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EnsureSchemaExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        var schemaName = $"tenant_{Guid.NewGuid():N}";

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureSchemaExists(schemaName);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(_fixture.RootConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT COUNT(*)
FROM information_schema.SCHEMATA
WHERE SCHEMA_NAME = '{schemaName}';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DropSchemaIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        var schemaName = $"tenant_{Guid.NewGuid():N}";
        await ExecuteNonQueryAsync(_fixture.RootConnectionString, $"CREATE SCHEMA `{schemaName}`;");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropSchemaIfExists(schemaName);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(_fixture.RootConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT COUNT(*)
FROM information_schema.SCHEMATA
WHERE SCHEMA_NAME = '{schemaName}';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropTableIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropTableIfExists(table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }
}
