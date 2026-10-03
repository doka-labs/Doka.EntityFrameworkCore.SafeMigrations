namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies private operation scopes survive EF batching and repeated generator invocations.</summary>
public sealed class SqlServerGuardScopeTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=guard_scopes;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Every operation owns its declarations even when callers concatenate different generation results.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardedCommands_IsolateDeclarationsAcrossSharedBatches(
        bool separateGeneration
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Caption", "O'Brien", type: "varchar(80)", maxLength: 80,
            nullable: false, defaultValue: "Caption O'Brien",
            collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS"));

        builder.EnsureModelManagedDataFromModel("O'Brien", ["Id"], ["int"], ["Id", "Caption"],
            ["int", "varchar(80)"], new object?[,] { { 7, "O'Brien" } });
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var defaultMapping = context.GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(string), "varchar(80)")
            ?? throw new InvalidOperationException("The ANSI fixture default has no SQL Server type mapping.");

        var nestedDefaultLiteral = defaultMapping.GenerateSqlLiteral("Caption O'Brien")
            .Replace("'", "''", StringComparison.Ordinal);

        // Act
        var commands = separateGeneration
            ? builder.Operations.SelectMany(operation => generator.Generate([operation], context.Model)).ToArray()
            : generator.Generate(builder.Operations, context.Model).ToArray();

        var scopes = commands.Select(static command =>
            SqlServerGuardedSqlTestContract.DecodeScope(command.CommandText)).ToArray();

        var guards = scopes.Where(static body => body.Contains("DECLARE @doka_state", StringComparison.Ordinal))
            .ToArray();

        // Assert
        Assert.Equal(2, guards.Length);
        Assert.All(commands, static command => Assert.False(command.TransactionSuppressed));
        Assert.All(guards, static body =>
        {
            Assert.Equal(1, body.Split("DECLARE @doka_state", StringSplitOptions.None).Length - 1);
            Assert.Contains("DECLARE @doka_action", body, StringComparison.Ordinal);
            Assert.Contains("DECLARE @doka_repair_ok", body, StringComparison.Ordinal);
            Assert.Contains("EXEC sys.sp_executesql", body, StringComparison.Ordinal);
            Assert.Contains("THROW 51001, N'doka_sm_different'", body, StringComparison.Ordinal);
            Assert.Contains("THROW 51005, N'doka_sm_postcondition'", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\nGO\n", body, StringComparison.Ordinal);
        });

        Assert.Contains("DECLARE @doka_default_supported", guards[0], StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_postcondition", guards[1], StringComparison.Ordinal);

        // WHY: Metadata sees the qualified table literal at the guard level,
        // while the ANSI payload remains quoted inside its deferred DDL batch.
        Assert.Contains("N'[dbo].[O''Brien]'", guards[0], StringComparison.Ordinal);
        Assert.Contains(nestedDefaultLiteral, guards[0], StringComparison.Ordinal);
        Assert.Contains("SET IDENTITY_INSERT", guards[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// One-level decoding preserves nested apostrophes rather than flattening their executable literals.
    /// </summary>
    [Fact]
    public void ScopeDecoder_DecodesOnlyTheOuterLiteral()
    {
        // Arrange
        const string body = "DECLARE @doka_state nvarchar(32);\n"
            + "EXEC sys.sp_executesql N'SELECT N''O''''Brien'';';\n";

        var command = SqlServerGuardedSqlTestContract.EncodeScope(body);

        // Act
        var decoded = SqlServerGuardedSqlTestContract.DecodeScope(command);

        // Assert
        Assert.Equal(body, decoded);
        Assert.Contains("N''O''''Brien''", decoded, StringComparison.Ordinal);
    }

    /// <summary>Malformed scopes and caller-visible declarations cannot masquerade as isolated guards.</summary>
    [Theory]
    [InlineData("DECLARE @doka_state nvarchar(32);")]
    [InlineData("EXEC sys.sp_executesql N'SELECT 1;")]
    [InlineData("EXEC sys.sp_executesql N'SELECT 1;'; DECLARE @doka_state int;")]
    public void ScopeDecoder_RejectsUnisolatedOrIncompleteCommands(
        string command
    )
    {
        // Arrange
        Func<string> decode = () => SqlServerGuardedSqlTestContract.DecodeScope(command);

        // Act
        var failure = Record.Exception(() => decode());

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }
}
