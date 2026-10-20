namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Keeps typed constraint candidates separate from default tuple targets and unrelated removals.</summary>
public sealed class SqlServerConstraintDropCandidateTests
{
    /// <summary>Only complete matching candidate identities contribute native constraint exclusions.</summary>
    /// <param name="kind">The typed predecessor operation.</param>
    /// <param name="key">Whether native key exclusions must be present.</param>
    /// <param name="check">Whether native CHECK exclusions must be present.</param>
    /// <param name="foreignKey">Whether native foreign-key exclusions must be present.</param>
    [Theory]
    [InlineData("primary", true, false, false)]
    [InlineData("unique", true, false, false)]
    [InlineData("check", false, true, false)]
    [InlineData("foreign", false, false, true)]
    [InlineData("table", false, false, true)]
    [InlineData("column", false, false, false)]
    [InlineData("index", false, false, false)]
    public void CandidatesRetainOnlyTheirNativeConstraintKind(string kind, bool key, bool check, bool foreignKey)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1;Database=constraint_candidates;User ID=sa;Password=unused;TrustServerCertificate=True");

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent candidate = kind switch
        {
            "primary" => new DropPrimaryKeyIntent("candidate", "nodes"),
            "unique" => new DropUniqueConstraintIntent("candidate", "nodes"),
            "check" => new DropCheckConstraintIntent("candidate", "nodes"),
            "foreign" => new DropForeignKeyIntent("candidate", "nodes"),
            "table" => new DropTableIntent("nodes"),
            "column" => new DropColumnIntent("other", "nodes"),
            "index" => new DropIndexIntent("candidate", "nodes"),
            _ => throw new UnreachableException(),
        };

        var drop = new SafeMigrationOperation(new DropColumnIntent("source", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var predecessor = new SafeMigrationOperation(candidate, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(drop, projectedConstraintDropCandidates: [predecessor]);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var classification = Assert.IsType<string>(plan.ClassificationCodeExpression);
        Assert.Equal(key, classification.Contains("removed_key.", StringComparison.Ordinal));
        Assert.Equal(check, classification.Contains("removed_check.", StringComparison.Ordinal));
        Assert.Equal(foreignKey, classification.Contains("removed_fk.", StringComparison.Ordinal));
        Assert.Equal(kind == "primary", classification.Contains("removed_key.type=N'PK'", StringComparison.Ordinal));
        Assert.Equal(kind == "unique", classification.Contains("removed_key.type=N'UQ'", StringComparison.Ordinal));
        Assert.Equal(key || check || kind == "foreign",
            classification.Contains(".name=N'candidate'", StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
