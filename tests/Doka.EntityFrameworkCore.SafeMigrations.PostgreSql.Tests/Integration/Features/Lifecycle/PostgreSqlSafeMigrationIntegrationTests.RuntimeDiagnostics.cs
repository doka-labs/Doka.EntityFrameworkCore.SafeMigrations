namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the RuntimeDiagnostics PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlRuntimeDiagnosticsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlRuntimeDiagnosticsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Native clients receive actionable MESSAGE text even when Npgsql redacts all Detail fields.</summary>
    /// <param name="state">The prepared runtime classification used by the native guard.</param>
    /// <param name="sqlState">The unchanged SafeMigrations SQLSTATE.</param>
    /// <param name="guardCode">The unchanged message token prefix.</param>
    [Theory]
    [InlineData("different", "P1001", "doka_sm_different")]
    [InlineData("unsupported", "P1002", "doka_sm_unsupported")]
    [InlineData("data_blocked", "P1003", "doka_sm_data_blocked")]
    [InlineData("prerequisite_missing", "P1004", "doka_sm_prerequisite_missing")]
    [InlineData("missing", "P1005", "doka_sm_postcondition")]
    public async Task NativeRuntimeDiagnosticSurvivesClientDetailRedaction(string state, string sqlState,
        string guardCode)
    {
        // Arrange
        var rawConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            IncludeErrorDetail = false,
        }.ConnectionString;

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("diagnostic_owner",
            new ExpectedColumnDefinition("RequiredId", typeof(int), false, "integer")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'" + state + "'", "FALSE",
            SafeMigrationRepairCapability.None, "FALSE");
        var sql = PostgreSqlRuntimeDiagnosticTests.Render(operation, plan);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<PostgresException>(failure);
        Assert.Equal(sqlState, native.SqlState);
        Assert.StartsWith(guardCode + ":", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("diagnostic_owner", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("RequiredId", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("reason=", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("next=", native.MessageText, StringComparison.Ordinal);
        Assert.InRange(native.MessageText.Length, 1, 2048);
    }

    /// <summary>Search-path refusal remains identifiable without opting into provider Detail disclosure.</summary>
    [Fact]
    public async Task NativeOwnerFailureCodeRemainsVisibleWithRedactedDetail()
    {
        // Arrange
        var rawConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            IncludeErrorDetail = false,
        }.ConnectionString;

        var operation = new SafeMigrationOperation(new DropTableIntent("guarded_owner"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'matching'", "TRUE",
            SafeMigrationRepairCapability.None, "FALSE")
        {
            ObjectIdentityGuardExpression = "FALSE",
            ObjectIdentityGuardFailureCodeExpression = "'schema_search_path_mismatch'",
        };

        var sql = PostgreSqlRuntimeDiagnosticTests.Render(operation, plan);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1002", native.SqlState);
        Assert.Contains("schema_search_path_mismatch", native.MessageText, StringComparison.Ordinal);
        Assert.DoesNotContain("schema_search_path_mismatch", native.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Literal escaping remains safe with nondefault string settings and adversarial identifiers.</summary>
    [Fact]
    public async Task NativeMessageEscapingIsIndependentOfStringSettings()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var operation = new SafeMigrationOperation(new DropTableIntent("owner'\\n$other$\n\u20AC"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'different'", "FALSE",
            SafeMigrationRepairCapability.None, "FALSE");
        var sql = "SET standard_conforming_strings = off; " + PostgreSqlRuntimeDiagnosticTests.Render(operation, plan);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1001", native.SqlState);
        Assert.Equal(SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_different"),
            native.MessageText);
        Assert.Contains("owner'", native.MessageText, StringComparison.Ordinal);
        Assert.All(native.MessageText, static character => Assert.InRange((int)character, 32, 126));
    }

    /// <summary>Hostile identity endpoints cannot terminate the anonymous block before the owned guard executes.</summary>
    /// <param name="placement">The owner, member, principal or rename endpoint receiving delimiter candidates.</param>
    [Theory]
    [InlineData("owner")]
    [InlineData("member")]
    [InlineData("foreign_key_principal")]
    [InlineData("rename_column_target")]
    [InlineData("rename_table_target")]
    public async Task NativeDelimiterCounterprobeRetainsOwnedSqlState(string placement)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var operation = PostgreSqlRuntimeDiagnosticTests.HostileDelimiterOperation(placement);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'different'", "TRUE",
            SafeMigrationRepairCapability.None, "FALSE");
        var sql = PostgreSqlRuntimeDiagnosticTests.Render(operation, plan);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1001", native.SqlState);
        Assert.StartsWith("doka_sm_different:", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("$doka_safe_migration$", native.MessageText, StringComparison.Ordinal);
        Assert.Contains("$doka_safe_migration_1$", native.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Native unique violations from ordinary SQL keep their original SQLSTATE and exception type.</summary>
    [Fact]
    public async Task OrdinaryNativeErrorIsNotReclassifiedAsSafeMigrationGuard()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_error (id integer PRIMARY KEY); "
            + "INSERT INTO native_error VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("INSERT INTO native_error VALUES (1);");

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var native = Assert.IsType<PostgresException>(failure);
        Assert.Equal("23505", native.SqlState);
        Assert.DoesNotContain("doka_sm_", native.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Readable guard branches do not alter successful creation or idempotent matching replays.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsPreserveCreateAndMatchingReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE diagnostic_tags (id integer PRIMARY KEY); "
            + "INSERT INTO diagnostic_tags VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("IsActive", "diagnostic_tags", type: "boolean", nullable: false,
            defaultValue: true);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var activeRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM diagnostic_tags WHERE \"IsActive\" = TRUE;");

        // Assert
        Assert.Equal(1, activeRows);
    }

}
