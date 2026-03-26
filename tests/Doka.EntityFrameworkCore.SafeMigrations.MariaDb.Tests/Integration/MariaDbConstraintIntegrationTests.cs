namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public sealed class MariaDbConstraintIntegrationTests : MariaDbIntegrationTestBase
{
    public MariaDbConstraintIntegrationTests(MariaDbContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `TenantId` int NOT NULL,
    PRIMARY KEY (`TenantId`, `Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PRIMARY",
            table: "Employees",
            columns: ["Id", "TenantId"],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for primary key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PRIMARY",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'PRIMARY'
  AND CONSTRAINT_TYPE = 'PRIMARY KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PRIMARY",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'PRIMARY'
  AND CONSTRAINT_TYPE = 'PRIMARY KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"]);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'AK_Employees_Email'
  AND CONSTRAINT_TYPE = 'UNIQUE';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'AK_Employees_Email'
  AND CONSTRAINT_TYPE = 'UNIQUE';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'AK_Employees_Email'
  AND CONSTRAINT_TYPE = 'UNIQUE';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_RepairMode_RejectsConflictingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    `TenantId` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `AK_Employees_Email` UNIQUE (`TenantId`, `Email`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for unique constraint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Age` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `CK_Employees_Age` CHECK (`Age` >= 21)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "`Age` >= 18",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for check constraint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Age` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "`Age` >= 18",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'CK_Employees_Age'
  AND CONSTRAINT_TYPE = 'CHECK';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Age` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "`Age` >= 18",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'CK_Employees_Age'
  AND CONSTRAINT_TYPE = 'CHECK';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_RepairMode_RejectsConflictingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Age` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `CK_Employees_Age` CHECK (`Age` >= 21)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "`Age` >= 18",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for check constraint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            onDelete: ReferentialAction.Cascade);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            onDelete: ReferentialAction.Cascade,
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_RepairMode_FailsWhenExistingDataViolatesConstraint()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`)
);

INSERT INTO `Employees` (`Id`, `DepartmentId`) VALUES (1, 999);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            onDelete: ReferentialAction.Cascade,
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Employees_Departments_DepartmentId`
        FOREIGN KEY (`DepartmentId`) REFERENCES `Departments` (`Id`)
        ON DELETE RESTRICT
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            onDelete: ReferentialAction.Cascade,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for foreign key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropPrimaryKeyIfExists_IsIdempotentAgainstRealMariaDb()
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
        migrationBuilder.DropPrimaryKeyIfExists(
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_TYPE = 'PRIMARY KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropForeignKeyIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Departments` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DepartmentId` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Employees_Departments_DepartmentId`
        FOREIGN KEY (`DepartmentId`) REFERENCES `Departments` (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropForeignKeyIfExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'FK_Employees_Departments_DepartmentId'
  AND CONSTRAINT_TYPE = 'FOREIGN KEY';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropUniqueConstraintIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Email` varchar(200) NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `AK_Employees_Email` UNIQUE (`Email`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropUniqueConstraintIfExists(
            name: "AK_Employees_Email",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'AK_Employees_Email'
  AND CONSTRAINT_TYPE = 'UNIQUE';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropCheckConstraintIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Age` int NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `CK_Employees_Age` CHECK (`Age` >= 18)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropCheckConstraintIfExists(
            name: "CK_Employees_Age",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND CONSTRAINT_NAME = 'CK_Employees_Age'
  AND CONSTRAINT_TYPE = 'CHECK';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }
}
