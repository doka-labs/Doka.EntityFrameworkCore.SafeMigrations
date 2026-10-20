namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects native captured-source roles independently of a database or SQL mode.</summary>
public sealed class MySqlCapturedExpressionContractTests
{
    /// <summary>Captured CAST targets cannot reuse portable column-type aliases that execute differently.</summary>
    /// <param name="target">The exact captured CAST target spelling.</param>
    /// <param name="accepted">Whether the spelling belongs to the common native CAST grammar.</param>
    [Theory]
    [InlineData("bigint", false)]
    [InlineData("varchar(8)", false)]
    [InlineData("text", false)]
    [InlineData("SIGNED", true)]
    [InlineData("SIGNED INTEGER", true)]
    [InlineData("unsigned integer", true)]
    [InlineData("CHAR(8)", true)]
    [InlineData("decimal(12, 2)", true)]
    [InlineData("DATETIME(6)", true)]
    public void CapturedCastQualificationRetainsNativeTargetGrammar(string target, bool accepted)
    {
        // Arrange
        var sql = $"CAST(id AS {target})";
        var captured = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression);

        // Act
        var failure = MySqlCapturedExpressionContract.GetUnsupportedFeature(sql, expression, isMariaDb: true);

        // Assert
        Assert.True(captured);
        Assert.Equal(accepted ? null : "captured_expression_cast_target_unverified", failure);
    }

    /// <summary>Foreign quote delimiters are rejected in identifier roles, not inside actual quoted tokens.</summary>
    /// <param name="sql">The successfully captured generic expression.</param>
    /// <param name="accepted">Whether MySQL's native backtick grammar proves its source roles.</param>
    [Theory]
    [InlineData("[id] > 0", false)]
    [InlineData("\"id\" > 0", false)]
    [InlineData("`id` > 0", true)]
    [InlineData("'[id]' = '[id]'", true)]
    [InlineData("'\"' = '\"'", true)]
    [InlineData("`a\"[b]` > 0", true)]
    [InlineData("'A''[B]' = 'A''[B]'", true)]
    public void CapturedQuoteQualificationUsesTokenRoles(string sql, bool accepted)
    {
        // Arrange
        var captured = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression);

        // Act
        var failure = MySqlCapturedExpressionContract.GetUnsupportedFeature(sql, expression, isMariaDb: true);

        // Assert
        Assert.True(captured);
        Assert.Equal(accepted ? null : "captured_expression_dialect_unverified", failure);
    }

    /// <summary>Supplementary captured text is rejected only for the engine with a proven lossy catalog.</summary>
    /// <param name="isMariaDb">Whether native I_S loses supplementary source bytes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedSupplementaryBoundaryIsEngineSpecific(bool isMariaDb)
    {
        // Arrange
        const string sql = "'\U0001F642'";
        var captured = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression);

        // Act
        var failure = MySqlCapturedExpressionContract.GetUnsupportedFeature(sql, expression, isMariaDb);

        // Assert
        Assert.True(captured);
        Assert.Equal(isMariaDb ? "captured_expression_literal_catalog_unverifiable" : null, failure);
    }
}
