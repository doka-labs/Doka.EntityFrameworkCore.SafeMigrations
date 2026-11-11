namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Preserves the generator's exact transport boundaries while ignoring SQL presentation layout.</summary>
public sealed class SqlServerGeneratedTransportReaderTests
{
    /// <summary>Actual MAX/NCHAR transport preserves UTF-16 text and quoted nested commands exactly.</summary>
    /// <param name="body">The native source whose value must survive the owned carrier.</param>
    [Theory]
    [InlineData("DECLARE @doka_state int; SELECT N'O''Brien';")]
    [InlineData("DECLARE @doka_state int;\r\nSELECT N'line1\r\nGO\nline2';")]
    [InlineData("EXEC sys.sp_executesql N'SELECT N''quoted''''value'';';")]
    public void ProtectedTransportDecodesOnlyTheOwnedExpression(
        string body
    )
    {
        // Arrange
        var command = SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(
            body, MigrationsSqlGenerationOptions.Idempotent);

        var nativeCarrier = SqlServerGuardedSqlTestContract.DecodeScope(command);

        // Act
        var decoded = SqlServerGuardedSqlTestContract.DecodeProtectedPayload(nativeCarrier);

        // Assert
        Assert.Equal(body, decoded);
        Assert.Contains("CAST(N'' AS nvarchar(max))", nativeCarrier, StringComparison.Ordinal);
    }

    /// <summary>An altered owned carrier fails loudly rather than interpreting an arbitrary SQL expression.</summary>
    /// <param name="original">The actual producer fragment being changed.</param>
    /// <param name="replacement">The invalid expression or suffix.</param>
    [Theory]
    [InlineData("CAST(N'' AS nvarchar(max))", "N''")]
    [InlineData("NCHAR(13)", "NCHAR(14)")]
    [InlineData("NCHAR(13)", "arbitrary_function()")]
    [InlineData("EXEC sys.sp_executesql @doka_script_payload;", "EXEC other_procedure @doka_script_payload;")]
    [InlineData("EXEC sys.sp_executesql @doka_script_payload;", "")]
    [InlineData("EXEC sys.sp_executesql @doka_script_payload;",
        "EXEC sys.sp_executesql @doka_script_payload; SELECT 1;")]
    public void AlteredProtectedTransportCannotBeCertified(
        string original,
        string replacement
    )
    {
        // Arrange
        var command = SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(
            "DECLARE @doka_state int;\r\nSELECT 1;", MigrationsSqlGenerationOptions.Idempotent);

        var nativeCarrier = SqlServerGuardedSqlTestContract.DecodeScope(command);
        Assert.Contains(original, nativeCarrier, StringComparison.Ordinal);
        var altered = nativeCarrier.Replace(original, replacement, StringComparison.Ordinal);

        // Act
        var failure = Record.Exception(() => SqlServerGuardedSqlTestContract.DecodeProtectedPayload(altered));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Generated scope classification tolerates token layout without changing its native payload.</summary>
    /// <param name="prefix">The executable transport prefix with independently varied presentation.</param>
    [Theory]
    [InlineData("EXEC\nsys.sp_executesql\tN")]
    [InlineData("EXEC /* boundary */ sys . sp_executesql N")]
    [InlineData("\r\nexec sys.sp_executesql N")]
    public void GeneratedScopeLayoutPreservesExactBodyAndGuardRole(
        string prefix
    )
    {
        // Arrange
        const string body = "DECLARE @doka_state int; SELECT N'O''Brien\r\nGO\n';";
        var generated = SqlServerGuardedSqlTestContract.EncodeScope(body);
        var reflowed = prefix + generated["EXEC sys.sp_executesql N".Length..];

        // Act
        var decoded = SqlServerGuardedSqlTestContract.DecodeScope(reflowed);
        var isGuard = SqlServerGuardedSqlTestContract.IsOperationGuard(reflowed);

        // Assert
        Assert.Equal(body, decoded);
        Assert.True(isGuard);
        Assert.Equal(1, SqlServerGuardedSqlTestContract.CountStateDeclarations(decoded));
    }
}
