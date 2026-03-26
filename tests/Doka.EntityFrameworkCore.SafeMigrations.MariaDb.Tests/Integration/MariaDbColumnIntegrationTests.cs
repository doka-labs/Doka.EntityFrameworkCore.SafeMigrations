namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public sealed class MariaDbColumnIntegrationTests : MariaDbIntegrationTestBase
{
    public MariaDbColumnIntegrationTests(MariaDbContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AddColumnIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await using (var setupConnection = new MySqlConnection(connectionString))
        {
            await setupConnection.OpenAsync();
            await using var setupCommand = setupConnection.CreateCommand();
            setupCommand.CommandText = """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DisplayName` varchar(50) NULL,
    PRIMARY KEY (`Id`)
);
""";
            await setupCommand.ExecuteNonQueryAsync();
        }

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_CreatesMissingNullableColumn()
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
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "varchar(200)",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_MatchingExistingColumn_IsNoOp()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DisplayName` varchar(200) NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "varchar(200)",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_StrictMode_AcceptsMatchingStringDefaultLiteral()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `Status` varchar(20) NOT NULL DEFAULT 'active',
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "Status",
            table: "Employees",
            type: "varchar(20)",
            nullable: false,
            defaultValue: "active",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
    }

    [Fact]
    public async Task AddColumnIfNotExists_PreflightOnly_DoesNotCreateMissingColumn()
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
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true),
            type: "varchar(200)",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_RejectsUnsafeMissingColumn()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    PRIMARY KEY (`Id`)
);
INSERT INTO `Employees` (`Id`) VALUES (1);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<int>(
            name: "Age",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "int",
            nullable: false);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe additive-column repair is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlterColumnIfDifferent_AltersExistingColumnAndIsIdempotent()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DisplayName` varchar(50) NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "varchar(50)",
            oldNullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COLUMN_TYPE
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

        var columnType = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Equal("varchar(200)", columnType);
    }

    [Fact]
    public async Task RenameColumnIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DisplayName` varchar(50) NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.RenameColumnIfExists(
            name: "DisplayName",
            table: "Employees",
            newName: "FullName");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'FullName';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DropColumnIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(connectionString, """
CREATE TABLE `Employees` (
    `Id` int NOT NULL,
    `DisplayName` varchar(200) NULL,
    PRIMARY KEY (`Id`)
);
""");

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropColumnIfExists(
            name: "DisplayName",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'Employees'
  AND COLUMN_NAME = 'DisplayName';
""";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(0, count);
    }
}
