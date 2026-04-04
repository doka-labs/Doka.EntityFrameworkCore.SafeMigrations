namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public sealed class MariaDbIndexIntegrationTests : MariaDbIntegrationTestBase
{
    public MariaDbIndexIntegrationTests(
        MariaDbContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    public async Task RenameIndexIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(50) NULL,
                PRIMARY KEY (`Id`),
                INDEX `IX_Employees_DisplayName` (`DisplayName`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.RenameIndexIfExists(
            name: "IX_Employees_DisplayName",
            newName: "IX_Employees_FullName",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_FullName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(200) NULL,
                PRIMARY KEY (`Id`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"]);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(200) NULL,
                `CreatedAtUtc` datetime NULL,
                PRIMARY KEY (`Id`),
                INDEX `IX_Employees_DisplayName` (`DisplayName`, `CreatedAtUtc`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_CreatesMissingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(50) NULL,
                PRIMARY KEY (`Id`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_MatchingExistingIndex_IsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(50) NULL,
                PRIMARY KEY (`Id`),
                INDEX `IX_Employees_DisplayName` (`DisplayName`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_RejectsConflictingExistingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(50) NULL,
                `CreatedAtUtc` datetime NULL,
                PRIMARY KEY (`Id`),
                INDEX `IX_Employees_DisplayName` (`DisplayName`, `CreatedAtUtc`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_PreflightOnly_DoesNotCreateMissingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(50) NULL,
                PRIMARY KEY (`Id`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_StrictMode_WithFilter_ThrowsNotSupported()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(200) NULL,
                PRIMARY KEY (`Id`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            filter: "`DisplayName` IS NOT NULL",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteOperationsAsync(
                context,
                migrationBuilder.Operations));
        Assert.Contains("does not support filtered indexes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropIndexIfExists_IsIdempotentAgainstRealMariaDb()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE `Employees` (
                `Id` int NOT NULL,
                `DisplayName` varchar(200) NULL,
                PRIMARY KEY (`Id`),
                INDEX `IX_Employees_DisplayName` (`DisplayName`)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropIndexIfExists(name: "IX_Employees_DisplayName", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.STATISTICS
                              WHERE TABLE_SCHEMA = DATABASE()
                                AND TABLE_NAME = 'Employees'
                                AND INDEX_NAME = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }
}
