namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that the retained analysis preamble is reused only where its evidence still holds.
/// </summary>
public sealed class SqlServerCatalogPreambleTests
{
    /// <summary>The identical context and reference set reuse the proven identifier verdict.</summary>
    [Fact]
    public void SameContextAndReferences_ReuseTheProvenVerdict()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var references = References("items", "code");
        var preamble = Preamble(context, references);

        // Act
        var covers = preamble.Covers(context, References("items", "code"), preamble.Environment);

        // Assert
        Assert.True(covers);
    }

    /// <summary>A second context never inherits the first context's session evidence.</summary>
    [Fact]
    public void DifferentContext_RequiresItsOwnProbe()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var other = new SafeMigrationDbContext(ConnectionString);
        var preamble = Preamble(context, References("items", "code"));

        // Act
        var covers = preamble.Covers(other, References("items", "code"), preamble.Environment);

        // Assert
        Assert.False(covers);
    }

    /// <summary>An added reference is outside the proven set and forces a new probe.</summary>
    [Fact]
    public void AdditionalReference_RequiresANewProbe()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var preamble = Preamble(context, References("items", "code"));
        var requested = References("items", "code", "label");

        // Act
        var covers = preamble.Covers(context, requested, preamble.Environment);

        // Assert
        Assert.False(covers);
    }

    /// <summary>A renamed reference of the same size is not covered by the earlier verdict.</summary>
    [Fact]
    public void ChangedReferenceName_RequiresANewProbe()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var preamble = Preamble(context, References("items", "code"));

        // Act
        var covers = preamble.Covers(context, References("items", "caption"), preamble.Environment);

        // Assert
        Assert.False(covers);
    }

    /// <summary>Reordered references describe a different proof input and are not covered.</summary>
    [Fact]
    public void ReorderedReferences_RequireANewProbe()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var preamble = Preamble(context, References("items", "code", "label"));

        // Act
        var covers = preamble.Covers(context, References("items", "label", "code"), preamble.Environment);

        // Assert
        Assert.False(covers);
    }

    /// <summary>A missing reference collection is rejected instead of silently reusing a verdict.</summary>
    [Fact]
    public void MissingReferences_AreRejected()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var preamble = Preamble(context, References("items", "code"));

        // Act
        var failure = Assert.Throws<ArgumentNullException>(() => preamble.Covers(context, null!, preamble.Environment));

        // Assert
        Assert.Equal("references", failure.ParamName);
    }

    /// <summary>Changed or unproven environment identities cannot reuse an earlier identifier verdict.</summary>
    /// <param name="change">The fresh environment facet that differs or cannot be proven.</param>
    [Theory]
    [InlineData("schema")]
    [InlineData("visibility")]
    [InlineData("default_schema")]
    [InlineData("collation")]
    [InlineData("database_principal")]
    [InlineData("login")]
    [InlineData("missing_principal")]
    [InlineData("missing_login")]
    public void ChangedEnvironment_RequiresANewIdentifierProof(string change)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var references = References("items", "code");
        var preamble = Preamble(context, references);
        var environment = change switch
        {
            "schema" => preamble.Environment with { DefaultSchema = "application" },
            "visibility" => preamble.Environment with { CanSeeDatabaseMetadata = false },
            "default_schema" => preamble.Environment with { DefaultSchemaIsDbo = false },
            "collation" => preamble.Environment with { Collation = "Latin1_General_100_CS_AS" },
            "database_principal" => preamble.Environment with { DatabasePrincipalId = 2 },
            "login" => preamble.Environment with { LoginSid = "02" },
            "missing_principal" => preamble.Environment with { DatabasePrincipalId = null },
            "missing_login" => preamble.Environment with { LoginSid = null },
            _ => throw new UnreachableException(),
        };

        // Act
        var covers = preamble.Covers(context, references, environment);

        // Assert
        Assert.False(covers);
    }

    private const string ConnectionString = "Server=localhost;Database=doka_sm_preamble;Integrated Security=true;";

    private static SqlServerSafeMigrationProviderAnalyzer.SqlServerCatalogPreamble Preamble(
        DbContext context,
        IReadOnlyList<SqlServerIdentifierReference> references
    ) => new(
        context,
        new SqlServerSafeMigrationProviderAnalyzer.SqlServerCatalogEnvironment(
            "dbo", true, true, "Latin1_General_100_CI_AS", 1, "01"),
        references,
        IdentifierSafe: true);

    private static SqlServerIdentifierReference[] References(
        string table,
        params string[] columns
    ) => [.. columns.Select(column => new SqlServerIdentifierReference(
        SqlServerIdentifierScope.Column,
        "dbo",
        table,
        column))];
}
