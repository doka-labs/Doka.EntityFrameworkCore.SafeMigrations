namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

public sealed class PostgreSqlConstraintIntegrationTests : PostgreSqlIntegrationTestBase
{
    public PostgreSqlConstraintIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "TenantId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("TenantId", "Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PK_Employees",
            table: "Employees",
            columns:
            [
                "Id",
                "TenantId"
            ],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for primary key",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PK_Employees",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'PK_Employees'
                                AND c.contype = 'p';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddPrimaryKeyIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PK_Employees",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'PK_Employees'
                                AND c.contype = 'p';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'AK_Employees_Email'
                                AND c.contype = 'u';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'AK_Employees_Email'
                                AND c.contype = 'u';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'AK_Employees_Email'
                                AND c.contype = 'u';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_RepairMode_RejectsConflictingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                "TenantId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_Employees_Email" UNIQUE ("TenantId", "Email")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for unique constraint",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddUniqueConstraintIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                "TenantId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_Employees_Email" UNIQUE ("TenantId", "Email")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for unique constraint",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'CK_Employees_Age'
                                AND c.contype = 'c';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'CK_Employees_Age'
                                AND c.contype = 'c';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'CK_Employees_Age'
                                AND c.contype = 'c';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_RepairMode_RejectsConflictingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_Employees_Age" CHECK ("Age" >= 21)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for check constraint",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddCheckConstraintIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_Employees_Age" CHECK ("Age" >= 21)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "\"Age\" >= 18",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for check constraint",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'FK_Employees_Departments_DepartmentId'
                                AND c.contype = 'f';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_RepairMode_CreatesMissingConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'FK_Employees_Departments_DepartmentId'
                                AND c.contype = 'f';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_PreflightOnly_DoesNotCreateConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'FK_Employees_Departments_DepartmentId'
                                AND c.contype = 'f';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_RepairMode_FailsWhenExistingDataViolatesConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id")
            );
            INSERT INTO "Employees" ("Id", "DepartmentId") VALUES (1, 999);
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'FK_Employees_Departments_DepartmentId'
                                AND c.contype = 'f';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddForeignKeyIfNotExists_StrictMode_ThrowsOnDefinitionMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Employees_Departments_DepartmentId"
                    FOREIGN KEY ("DepartmentId") REFERENCES "Departments" ("Id")
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

        var exception =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, migrationBuilder.Operations));
        Assert.Contains(
            "Safe migration strict-mode mismatch for foreign key",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Provider: PostgreSQL.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropPrimaryKeyIfExists_IsIdempotentAgainstRealPostgreSql()
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
        migrationBuilder.DropPrimaryKeyIfExists(table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.contype = 'p';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropForeignKeyIfExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Departments" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "DepartmentId" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Employees_Departments_DepartmentId"
                    FOREIGN KEY ("DepartmentId") REFERENCES "Departments" ("Id")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropForeignKeyIfExists(name: "FK_Employees_Departments_DepartmentId", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'FK_Employees_Departments_DepartmentId'
                                AND c.contype = 'f';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropUniqueConstraintIfExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Email" text NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_Employees_Email" UNIQUE ("Email")
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropUniqueConstraintIfExists(name: "AK_Employees_Email", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'AK_Employees_Email'
                                AND c.contype = 'u';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DropCheckConstraintIfExists_IsIdempotentAgainstRealPostgreSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "Employees" (
                "Id" integer NOT NULL,
                "Age" integer NOT NULL,
                CONSTRAINT "PK_Employees" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_Employees_Age" CHECK ("Age" >= 18)
            );
            """);

        await using var context = new SafeMigrationDbContext(connectionString);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.DropCheckConstraintIfExists(name: "CK_Employees_Age", table: "Employees");

        await ExecuteOperationsAsync(context, migrationBuilder.Operations);
        await ExecuteOperationsAsync(context, migrationBuilder.Operations);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT COUNT(*)
                              FROM pg_constraint c
                              JOIN pg_class t ON t.oid = c.conrelid
                              JOIN pg_namespace n ON n.oid = t.relnamespace
                              WHERE n.nspname = 'public'
                                AND t.relname = 'Employees'
                                AND c.conname = 'CK_Employees_Age'
                                AND c.contype = 'c';
                              """;

        var count = await ExecuteScalarAsInt32Async(command);
        Assert.Equal(0, count);
    }
}
