namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Checks the server/client ownership bridge without a native SQL Server instance.</summary>
public sealed class SqlServerIdentityInsertScopeTests
{
    /// <summary>
    /// Requires a complete matching invocation token before either OFF or context removal can execute.
    /// </summary>
    [Fact]
    public void Recovery_ProvesTypeSizeTokenAndPhaseBeforeOff()
    {
        // Arrange
        const string token = "0123456789ABCDEF0123456789ABCDEF";

        // Act
        var sql = SqlServerSafeMigrationIdentityInsertScope.BuildRecoverySql(
            token, "SET IDENTITY_INSERT [dbo].[roles] OFF;");
        var off = sql.IndexOf("SET IDENTITY_INSERT", StringComparison.Ordinal);
        var tokenProof = sql.IndexOf("<> 0x" + token + " RETURN;", StringComparison.Ordinal);
        var acquiredProof = sql.IndexOf("SUBSTRING(@doka_identity_scope, 1, 1) = 0x02", StringComparison.Ordinal);

        // Assert
        Assert.Contains("N'BaseType') <> N'varbinary' RETURN;", sql, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(CONVERT(varbinary(max), @doka_identity_owner)) <> 17 RETURN;", sql,
            StringComparison.Ordinal);
        Assert.True(tokenProof >= 0 && tokenProof < acquiredProof && acquiredProof < off);
        Assert.Contains("doka_sm_identity_acquisition_unproven", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.identity_columns", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("read_only", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects a used context key before acquisition and does not mutate identity during bootstrap.
    /// </summary>
    [Fact]
    public void Bootstrap_RejectsExistingMarkerBeforeInitializingOwnScope()
    {
        // Arrange
        const string token = "0123456789ABCDEF0123456789ABCDEF";

        // Act
        var sql = SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(token);
        var rejection = sql.IndexOf("THROW 51007", StringComparison.Ordinal);
        var mutation = sql.IndexOf("EXEC sys.sp_set_session_context", StringComparison.Ordinal);

        // Assert
        Assert.True(rejection >= 0 && rejection < mutation);
        Assert.Contains("@value = 0x00" + token, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IDENTITY_INSERT", sql, StringComparison.Ordinal);
    }

    /// <summary>Cannot disable identity state when the provider command was never dispatched.</summary>
    [Fact]
    public void BootstrapOnlyRecovery_CannotIssueOff()
    {
        // Arrange
        const string token = "0123456789ABCDEF0123456789ABCDEF";

        // Act
        var sql = SqlServerSafeMigrationIdentityInsertScope.BuildRecoverySql(
            token, "SET IDENTITY_INSERT [dbo].[roles] OFF;", allowAcquiredIdentity: false);

        // Assert
        Assert.DoesNotContain("SET IDENTITY_INSERT", sql, StringComparison.Ordinal);
        Assert.Contains("SUBSTRING(@doka_identity_scope, 1, 1) <> 0x00", sql, StringComparison.Ordinal);
        Assert.Contains("@value = NULL", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records attempting before ON and acquired after ON, retaining ready state for ordinary ON failure.
    /// </summary>
    [Fact]
    public void Acquisition_UsesThreeDistinctPhasesAndResetsOnlyItsFailedAttempt()
    {
        // Arrange
        const string table = "[dbo].[roles]";

        // Act
        var sql = SqlServerSafeMigrationIdentityInsertScope.BuildAcquisitionSql(table);
        var attempting = sql.IndexOf("@value = @doka_identity_attempt;", StringComparison.Ordinal);
        var on = sql.IndexOf("SET IDENTITY_INSERT " + table + " ON;", StringComparison.Ordinal);
        var acquired = sql.IndexOf("@value = @doka_identity_acquired;", StringComparison.Ordinal);

        // Assert
        Assert.True(attempting >= 0 && attempting < on && on < acquired);
        Assert.Contains("@doka_identity_scope = 0x00 + CONVERT(binary(16), NEWID())", sql, StringComparison.Ordinal);
        Assert.Contains("@doka_identity_attempt varbinary(17) = 0x01", sql, StringComparison.Ordinal);
        Assert.Contains("@doka_identity_acquired varbinary(17) = 0x02", sql, StringComparison.Ordinal);
        Assert.Contains("@value = @doka_identity_scope; THROW; END CATCH;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" OFF", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps repeatable generated SQL deterministic while execution tokens are allocated only at runtime.
    /// </summary>
    [Fact]
    public void GeneratedSeed_RepeatedGenerationPreservesScriptBytes()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=localhost;Database=ownership;TrustServerCertificate=True");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("roles", ["Id"], ["int"], ["Id"], ["int"],
            new object?[,] { { 7 } });
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var first = generator.Generate(builder.Operations, context.Model).Select(static command => command.CommandText)
            .ToArray();
        var second = generator.Generate(builder.Operations, context.Model).Select(static command => command.CommandText)
            .ToArray();

        // Assert
        Assert.Equal(first, second);
        Assert.Contains(first, static command => command.Contains("NEWID()", StringComparison.Ordinal));
    }

    /// <summary>Separates SQL-only marker ownership from the managed bootstrap recovery boundary.</summary>
    [Fact]
    public void ScriptGeneration_RejectsExistingReadyAndDoesNotClearMarkerOnNoOp()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=localhost;Database=ownership;TrustServerCertificate=True");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("roles", ["Id"], ["int"], ["Id"], ["int"],
            new object?[,] { { 7 } });

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model,
            MigrationsSqlGenerationOptions.Script);
        var seed = Assert.Single(commands, static command =>
            command.CommandText.Contains("DECLARE @doka_identity_insert", StringComparison.Ordinal));

        var scope = SqlServerGuardedSqlTestContract.DecodeScope(seed.CommandText);

        // Assert
        Assert.DoesNotContain(commands, static command => command is SqlServerSafeMigrationIdentityInsertCommand);
        Assert.Contains("IF @doka_identity_owner IS NOT NULL THROW 51007", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("DECLARE @doka_identity_completed", scope, StringComparison.Ordinal);
        Assert.Contains("NEWID()", scope, StringComparison.Ordinal);
    }
}
