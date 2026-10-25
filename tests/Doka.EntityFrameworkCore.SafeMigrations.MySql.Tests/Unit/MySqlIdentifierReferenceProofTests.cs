namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards the closed parse-only identifier proof and its session ownership boundary.</summary>
public sealed class MySqlIdentifierReferenceProofTests
{
    private const string Scope = "1234567890abcdef1234567890abcdef";
    private const string Marker = MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix + "0__";

    /// <summary>Native preparation never executes the synthetic identifier SELECT.</summary>
    [Fact]
    public void EvaluationPreparesButNeverExecutesTheSyntheticSource()
    {
        // Arrange
        var proof = new MySqlSafeMigrationIdentifierReferenceProof(Marker, "value", Scope);

        // Act
        var sql = MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, "TRUE", "TRUE");
        var block = DecodeSelectedBlock(sql);

        // Assert
        Assert.Contains("DECLARE EXIT HANDLER FOR 1064", block, StringComparison.Ordinal);
        Assert.Contains("PREPARE " + proof.InnerStatementName + " FROM ", block, StringComparison.Ordinal);
        Assert.Contains("DEALLOCATE PREPARE " + proof.InnerStatementName, block, StringComparison.Ordinal);
        Assert.DoesNotContain(";EXECUTE " + proof.InnerStatementName, block, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLEXCEPTION", block, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(
            "SELECT value FROM (SELECT 0 AS value) doka_sm_source")), block, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN NOT ATOMIC", sql, StringComparison.Ordinal);
    }

    /// <summary>SQL text, delimited names and malformed identifiers cannot enter the private query.</summary>
    /// <param name="head">The invalid caller-controlled identifier head.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1value")]
    [InlineData("a.b")]
    [InlineData("`value`")]
    [InlineData("\"value\"")]
    [InlineData("[value]")]
    [InlineData("value;SELECT 1")]
    [InlineData("value--")]
    [InlineData("value/*x*/")]
    [InlineData("a\0b")]
    [InlineData("caf\u00e9")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidIdentifierHeadsAreRejected(string head)
    {
        // Arrange
        var marker = Marker;

        // Act
        var exception = Record.Exception(() =>
            new MySqlSafeMigrationIdentifierReferenceProof(marker, head, Scope));

        // Assert
        Assert.Equal("rootIdentifier", Assert.IsType<ArgumentException>(exception).ParamName);
    }

    /// <summary>Malformed ordinal text cannot alias another operation's marker or prepared identity.</summary>
    /// <param name="suffix">The invalid marker ordinal or terminator.</param>
    [Theory]
    [InlineData("__")]
    [InlineData("-1__")]
    [InlineData("00__")]
    [InlineData("01__")]
    [InlineData("+1__")]
    [InlineData("2147483648__")]
    [InlineData("0_")]
    [InlineData("0__payload")]
    public void InvalidOwnedMarkersAreRejected(string suffix)
    {
        // Arrange
        var marker = MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix + suffix;

        // Act
        var exception = Record.Exception(() =>
            new MySqlSafeMigrationIdentifierReferenceProof(marker, "id", Scope));

        // Assert
        Assert.Equal("marker", Assert.IsType<ArgumentException>(exception).ParamName);
    }

    /// <summary>Only a complete GUID scope can contribute to provider-owned SQL identities.</summary>
    /// <param name="scope">The invalid scope text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("1234567890abcdef1234567890abcde")]
    [InlineData("1234567890abcdef1234567890abcdef0")]
    [InlineData("1234567890abcdef1234567890abcdeg")]
    [InlineData("12345678-90ab-cdef-1234-567890abcdef")]
    [InlineData("1234567890abcdef1234567890abcde;")]
    public void InvalidPreparedScopesAreRejected(string scope)
    {
        // Arrange
        var marker = Marker;

        // Act
        var exception = Record.Exception(() =>
            new MySqlSafeMigrationIdentifierReferenceProof(marker, "id", scope));

        // Assert
        Assert.Equal("scopeId", Assert.IsType<ArgumentException>(exception).ParamName);
    }

    /// <summary>Separate ordinals in one invocation do not share result or preparation identities.</summary>
    [Fact]
    public void OperationOrdinalsRetainIndependentOwnedIdentities()
    {
        // Arrange
        var first = new MySqlSafeMigrationIdentifierReferenceProof(Marker, "id", Scope);
        var last = new MySqlSafeMigrationIdentifierReferenceProof(
            MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix + "2147483647__", "value", Scope);

        // Act
        var names = new[]
        {
            first.InnerStatementName, first.OuterStatementName, last.InnerStatementName, last.OuterStatementName,
        };

        // Assert
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotEqual(first.ResultVariable, last.ResultVariable);
        Assert.All(names, static name => Assert.InRange(name.Length, 1, 64));
    }

    /// <summary>Cleanup handles only absent owned statements without masking arbitrary execution errors.</summary>
    [Fact]
    public void CleanupHasNarrowIdempotentOwnership()
    {
        // Arrange
        var proof = new MySqlSafeMigrationIdentifierReferenceProof(Marker, "id", Scope);

        // Act
        var sql = MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof);
        var start = sql.IndexOf("CONVERT(0x", StringComparison.Ordinal) + "CONVERT(0x".Length;
        var end = sql.IndexOf(" USING utf8mb4)", start, StringComparison.Ordinal);
        var block = Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start)));

        // Assert
        Assert.Contains("DECLARE CONTINUE HANDLER FOR 1243", block, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLEXCEPTION", block, StringComparison.Ordinal);
        Assert.DoesNotContain("1064", block, StringComparison.Ordinal);
        Assert.Contains("DEALLOCATE PREPARE " + proof.InnerStatementName, block, StringComparison.Ordinal);
        Assert.Contains("DEALLOCATE PREPARE " + proof.OuterStatementName, block, StringComparison.Ordinal);
        Assert.Contains(proof.ResultVariable + " = NULL", block, StringComparison.Ordinal);
    }

    private static string DecodeSelectedBlock(string sql)
    {
        var start = sql.IndexOf("THEN CONVERT(0x", StringComparison.Ordinal) + "THEN CONVERT(0x".Length;
        var end = sql.IndexOf(" USING utf8mb4)", start, StringComparison.Ordinal);

        return Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start)));
    }
}
