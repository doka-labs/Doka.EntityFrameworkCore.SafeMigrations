namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Native execution retains each guard number and readable operation context.</summary>
    /// <param name="state">The prepared native classification.</param>
    /// <param name="number">The unchanged native SafeMigrations error number.</param>
    /// <param name="guardCode">The unchanged token prefix.</param>
    [SqlServerLiveTheory]
    [InlineData("different", 51001, "doka_sm_different")]
    [InlineData("unsupported", 51002, "doka_sm_unsupported")]
    [InlineData("data_blocked", 51003, "doka_sm_data_blocked")]
    [InlineData("prerequisite_missing", 51004, "doka_sm_prerequisite_missing")]
    [InlineData("missing", 51005, "doka_sm_postcondition")]
    public async Task NativeRuntimeDiagnosticPreservesGuardIdentity(string state, int number, string guardCode)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var operation = SqlServerRuntimeDiagnosticTests.ColumnOperation("diagnostic_owner", "RequiredId");
        var plan = new SqlServerSafeMigrationRuntimePlan("N'" + state + "'", "0",
            SafeMigrationRepairCapability.None, "0");
        var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], [], operationOrdinal: 7);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(number, native.Number);
        Assert.Equal(1, native.State);
        Assert.StartsWith(guardCode + ":", native.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_owner", native.Message, StringComparison.Ordinal);
        Assert.Contains("RequiredId", native.Message, StringComparison.Ordinal);
        Assert.Contains("operation 7", native.Message, StringComparison.Ordinal);
        Assert.Contains("reason=", native.Message, StringComparison.Ordinal);
        Assert.Contains("next=", native.Message, StringComparison.Ordinal);
        Assert.InRange(native.Message.Length, 1, 2048);
    }

    /// <summary>Percent and apostrophe identities survive native THROW without formatting or scope injection.</summary>
    [SqlServerLiveFact]
    public async Task NativeMessagePreservesPercentAndQuoteIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var operation = SqlServerRuntimeDiagnosticTests.ColumnOperation("owner'%; THROW 51099, N'injected', 1; --",
            "RequiredId");
        var plan = SqlServerRuntimeDiagnosticTests.Plan() with { StateExpression = "N'different'" };
        var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(51001, native.Number);
        Assert.Contains("owner'%", native.Message, StringComparison.Ordinal);
    }

    /// <summary>Maximal percent expansion cannot replace an owned guard with native formatting error 35100.</summary>
    [SqlServerLiveFact]
    public async Task NativeMaximumPercentIdentityRetainsOwnedGuard()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var operation = SqlServerRuntimeDiagnosticTests.MaximumPercentForeignKeyOperation();
        var plan = SqlServerRuntimeDiagnosticTests.Plan() with { StateExpression = "N'different'" };
        var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(51001, native.Number);
        Assert.Equal(1, native.State);
        Assert.StartsWith("doka_sm_different:", native.Message, StringComparison.Ordinal);
        Assert.Contains("owner", native.Message, StringComparison.Ordinal);
        Assert.Contains("...", native.Message, StringComparison.Ordinal);
        Assert.Contains("reason=", native.Message, StringComparison.Ordinal);
        Assert.Contains("next=", native.Message, StringComparison.Ordinal);
        Assert.InRange(native.Message.Length, 1, 2048);
    }

    /// <summary>Native capacity refusals preserve the selected cause, including an honest unknown-layout case.</summary>
    /// <param name="reasonCode">The metadata-only capacity failure selected by the original proof.</param>
    [SqlServerLiveTheory]
    [InlineData("column_limit")]
    [InlineData("column_fixed_row_limit")]
    [InlineData("column_layout_unproven")]
    public async Task NativeLayoutDiagnosticRetainsSelectedCause(string reasonCode)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var operation = SqlServerRuntimeDiagnosticTests.ColumnOperation("layout_owner", "AddedColumn");
        var plan = SqlServerRuntimeDiagnosticTests.Plan() with
        {
            ColumnLayoutFailureExpression = "N'" + reasonCode + "'",
        };

        var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, sql));

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(51002, native.Number);
        Assert.Equal(1, native.State);
        Assert.Contains("reasonCode=" + reasonCode, native.Message, StringComparison.Ordinal);
        Assert.Contains("layout_owner", native.Message, StringComparison.Ordinal);
        Assert.Contains("AddedColumn", native.Message, StringComparison.Ordinal);
    }

    /// <summary>Existing caller session ownership remains untouched and error 51007 names the authored table.</summary>
    [SqlServerLiveFact]
    public async Task NativeIdentityScopeDiagnosticDoesNotReplaceCallerState()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var setup = connection.CreateCommand();
        setup.CommandText = "EXEC sys.sp_set_session_context @key = N'"
            + SqlServerSafeMigrationIdentityInsertScope.ContextKey + "', @value = N'caller-owned';";
        await setup.ExecuteNonQueryAsync(CancellationToken.None);
        var operation = SqlServerRuntimeDiagnosticTests.ColumnOperation("identity_owner", "Id");
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(
            "0123456789ABCDEF0123456789ABCDEF", SafeMigrationDiagnosticMessages.FormatRuntime(operation,
                "doka_sm_identity_scope_in_use", 3));

        // Act
        var failure = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(CancellationToken.None));
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT CONVERT(nvarchar(80), SESSION_CONTEXT(N'"
            + SqlServerSafeMigrationIdentityInsertScope.ContextKey + "'));";
        var retained = await check.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(51007, native.Number);
        Assert.Contains("identity_owner", native.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-owned", native.Message, StringComparison.Ordinal);
        Assert.Equal("caller-owned", retained);
    }

    /// <summary>Ordinary foreign-key errors remain native error 547, not a SafeMigrations rejection.</summary>
    [SqlServerLiveFact]
    public async Task OrdinaryForeignKeyErrorRetainsItsNativeIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.native_parent (Id int PRIMARY KEY); "
            + "CREATE TABLE dbo.native_child (Id int, ParentId int REFERENCES dbo.native_parent(Id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("INSERT INTO dbo.native_child VALUES (1, 999);");

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var native = Assert.IsType<SqlException>(failure);
        Assert.Equal(547, native.Number);
        Assert.DoesNotContain("doka_sm_", native.Message, StringComparison.Ordinal);
    }

    /// <summary>New messages leave successful create and matching replay outcomes unchanged.</summary>
    [SqlServerLiveFact]
    public async Task RuntimeDiagnosticsPreserveCreateAndMatchingReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.diagnostic_tags (Id int PRIMARY KEY); "
            + "INSERT INTO dbo.diagnostic_tags VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("IsActive", "diagnostic_tags", type: "bit", nullable: false,
            defaultValue: true);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var activeRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.diagnostic_tags WHERE IsActive = 1;");

        // Assert
        Assert.Equal(1, activeRows);
    }
}
