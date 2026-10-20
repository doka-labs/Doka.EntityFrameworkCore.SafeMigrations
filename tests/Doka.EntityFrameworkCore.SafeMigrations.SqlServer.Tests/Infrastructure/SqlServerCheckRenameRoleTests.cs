namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Prevents SQL Server CHECK rename proofs from comparing non-column token roles.</summary>
public sealed class SqlServerCheckRenameRoleTests
{
    /// <summary>Only parsed member roles participate in native catalog equality.</summary>
    /// <param name="sql">The captured native CHECK text.</param>
    /// <param name="expected">The only referenced column, or null when there are no members.</param>
    [Theory]
    [InlineData("[Upper]>0", "Upper")]
    [InlineData("[Payload]>0 AND 'Upper'='Upper'", "Payload")]
    [InlineData("[Payload]>ABS(0)", "Payload")]
    [InlineData("CAST([Payload] AS int)>0", "Payload")]
    [InlineData("1=1", null)]
    public void CapturedCheckCollectsOnlyColumnRoles(string sql, string? expected)
    {
        // Arrange
        var definition = Captured(sql);

        // Act
        var names = SqlServerSafeMigrationCatalogSqlBuilder.GetCheckRenamePrerequisiteColumns(definition);

        // Assert
        Assert.NotNull(names);
        Assert.Equal(expected is null ? [] : [expected], names);
    }

    /// <summary>Unknown native type/function roles stay unproven instead of inventing column dependencies.</summary>
    /// <param name="sql">The captured CHECK with roles outside the admitted proof grammar.</param>
    [Theory]
    [InlineData("TRY_CONVERT(int,[Payload])>0")]
    [InlineData("GETDATE()>[Payload]")]
    [InlineData("[Payload]")]
    public void UnprovenNativeRolesNeverBecomeColumnComparisons(string sql)
    {
        // Arrange
        var definition = Captured(sql);

        // Act
        var names = SqlServerSafeMigrationCatalogSqlBuilder.GetCheckRenamePrerequisiteColumns(definition);

        // Assert
        Assert.Null(names);
    }

    private static ExpectedCheckConstraintDefinition Captured(string sql)
    {
        var expression = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var parsed)
            ? parsed : throw new InvalidOperationException("The role fixture must parse.");

        return ExpectedCheckConstraintDefinition.FromCapturedExpression("CK_roles", "nodes", expression, sql, null);
    }
}
