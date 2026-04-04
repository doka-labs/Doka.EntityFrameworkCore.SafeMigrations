namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

public sealed class PostgreSqlIndexIntegrationTests : PostgreSqlIntegrationTestBase
{
    public PostgreSqlIndexIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    public async Task RenameIndexIfExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Employees_DisplayName" ON "Employees" ("DisplayName");
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.RenameIndexIfExists(
            name: "IX_Employees_DisplayName",
            newName: "IX_Employees_FullName",
            table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              WHERE n.nspname = 'public'
                                AND idx.relname = 'IX_Employees_FullName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              JOIN pg_index i ON i.indexrelid = idx.oid
                              JOIN pg_class t ON t.oid = i.indrelid
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND idx.relname = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                "CreatedAtUtc" timestamp without time zone NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Employees_DisplayName" ON "Employees" ("DisplayName", "CreatedAtUtc");
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
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_CreatesMissingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              JOIN pg_index i ON i.indexrelid = idx.oid
                              JOIN pg_class t ON t.oid = i.indrelid
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND idx.relname = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_MatchingExistingIndex_IsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Employees_DisplayName" ON "Employees" ("DisplayName");
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              JOIN pg_index i ON i.indexrelid = idx.oid
                              JOIN pg_class t ON t.oid = i.indrelid
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND idx.relname = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_RepairMode_RejectsConflictingExistingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                "CreatedAtUtc" timestamp without time zone NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Employees_DisplayName" ON "Employees" ("DisplayName", "CreatedAtUtc");
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
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateIndexIfNotExists_PreflightOnly_DoesNotCreateMissingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              JOIN pg_index i ON i.indexrelid = idx.oid
                              JOIN pg_class t ON t.oid = i.indrelid
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND idx.relname = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropIndexIfExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" text NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Employees_DisplayName" ON "Employees" ("DisplayName");
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropIndexIfExists(name: "IX_Employees_DisplayName", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_class idx
                              JOIN pg_namespace n ON n.oid = idx.relnamespace
                              WHERE n.nspname = 'public'
                                AND idx.relname = 'IX_Employees_DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }
}
