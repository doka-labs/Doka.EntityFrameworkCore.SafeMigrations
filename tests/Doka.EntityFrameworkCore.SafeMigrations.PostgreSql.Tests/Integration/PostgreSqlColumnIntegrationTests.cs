namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

public sealed class PostgreSqlColumnIntegrationTests : PostgreSqlIntegrationTestBase
{
    public PostgreSqlColumnIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    public async Task AddColumnIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" character varying(50) NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            type: "text",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe migration strict-mode mismatch for column", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_CreatesMissingNullableColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "text",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_MatchingExistingColumn_IsNoOp()
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
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "text",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_StrictMode_AcceptsMatchingStringDefaultLiteral()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Status" text NOT NULL DEFAULT 'active',
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "Status",
            table: "Employees",
            type: "text",
            nullable: false,
            defaultValue: "active",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
    }

    [Fact]
    public async Task AddColumnIfNotExists_PreflightOnly_DoesNotCreateMissingColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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
            type: "text",
            nullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddColumnIfNotExists_RepairMode_RejectsUnsafeMissingColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            INSERT INTO "Employees" ("Id") VALUES (1);
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddColumnIfNotExists<int>(
            name: "Age",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            type: "integer",
            nullable: false);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains("Safe additive-column repair is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlterColumnIfDifferent_AltersExistingColumnAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DisplayName" character varying(50) NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "DisplayName",
            table: "Employees",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(50)",
            oldNullable: true);

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT data_type
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'DisplayName';
                              """;

        var dataType = await ExecuteScalarAsStringAsync(command);
        Assert.Equal("text", dataType);
    }

    [Fact]
    public async Task RenameColumnIfExists_IsIdempotentAgainstRealPostgreSql()
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
        migrationBuilder.RenameColumnIfExists(name: "DisplayName", table: "Employees", newName: "FullName");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'FullName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DropColumnIfExists_IsIdempotentAgainstRealPostgreSql()
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
        migrationBuilder.DropColumnIfExists(name: "DisplayName", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'Employees'
                                AND column_name = 'DisplayName';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }
}
