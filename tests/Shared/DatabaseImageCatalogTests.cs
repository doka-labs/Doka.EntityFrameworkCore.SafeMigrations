namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Checks embedded native image identities and strict parser behavior without starting Docker.</summary>
public sealed class DatabaseImageCatalogTests
{
    /// <summary>Every supported native target exists in the consuming assembly's canonical resource.</summary>
    /// <param name="target">The immutable qualification-stage name.</param>
    /// <param name="tag">The native repository and version preserved from qualification.</param>
    [Theory]
    [InlineData("mysql84", "mysql:8.4.13")]
    [InlineData("mysql97", "mysql:9.7.4")]
    [InlineData("mariadb1011", "mariadb:10.11.19")]
    [InlineData("mariadb114", "mariadb:11.4.13")]
    [InlineData("mariadb118", "mariadb:11.8.9")]
    [InlineData("mariadb123", "mariadb:12.3.3")]
    [InlineData("postgres14", "postgres:14.24")]
    [InlineData("postgres15", "postgres:15.19")]
    [InlineData("postgres16", "postgres:16.15")]
    [InlineData("postgres17", "postgres:17.11")]
    [InlineData("postgres18", "postgres:18.6")]
    [InlineData("sqlserver2019", "mcr.microsoft.com/mssql/server:2019-latest")]
    [InlineData("sqlserver2022", "mcr.microsoft.com/mssql/server:2022-latest")]
    [InlineData("sqlserver2025", "mcr.microsoft.com/mssql/server:2025-latest")]
    public void GetImage_DeclaredTargetResolvesExactTaggedDigest(
        string target,
        string tag
    )
    {
        // Arrange
        var prefix = tag + "@sha256:";

        // Act
        var image = DatabaseImageCatalog.GetImage(target);

        // Assert
        Assert.StartsWith(prefix, image, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + 64, image.Length);
        Assert.All(image[prefix.Length..],
            static character =>
                Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    /// <summary>An undeclared stage cannot silently select a mutable or unrelated image.</summary>
    [Fact]
    public void GetImage_UnknownTargetRejectsSelection()
    {
        // Arrange
        const string target = "missing";

        // Act
        var failure = Record.Exception(() => DatabaseImageCatalog.GetImage(target));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Explicit image overrides remain authoritative instead of being replaced by a default.</summary>
    /// <param name="configuredImage">The deliberately selected user image.</param>
    [Theory]
    [InlineData("private.example/custom:local")]
    [InlineData("  private.example/custom@sha256:0123456789  ")]
    public void Resolve_ExplicitImagePreservesExistingOverride(
        string configuredImage
    )
    {
        // Arrange
        var expected = configuredImage.Trim();

        // Act
        var image = DatabaseImageCatalog.Resolve("mariadb", "12.3.3", configuredImage);

        // Assert
        Assert.Equal(expected, image);
    }

    /// <summary>Supported version-only selections use the same immutable reference as the canonical target.</summary>
    /// <param name="configuredImage">An absent or whitespace-only override.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_SupportedVersionUsesCanonicalPin(
        string? configuredImage
    )
    {
        // Arrange
        var expected = DatabaseImageCatalog.GetImage("mariadb123");

        // Act
        var image = DatabaseImageCatalog.Resolve("mariadb", "12.3.3", configuredImage);

        // Assert
        Assert.Equal(expected, image);
    }

    /// <summary>Deliberately unlisted engine versions retain the preexisting explicit-version escape.</summary>
    [Fact]
    public void Resolve_ExplicitUnlistedVersionDoesNotSubstituteSupportedVersion()
    {
        // Arrange
        const string version = "99.0.1";

        // Act
        var image = DatabaseImageCatalog.Resolve("postgres", version, null);

        // Assert
        Assert.Equal("postgres:99.0.1", image);
    }

    /// <summary>Comments and line endings do not manufacture targets or change image identity.</summary>
    [Fact]
    public void Parse_CommentsAndWhitespacePreserveOnlyDeclaredTargets()
    {
        // Arrange
        var image = "mysql:8.4.13@sha256:" + new string('a', 64);
        using var reader = new StringReader($"# FROM hidden AS hidden\r\n\r\n FROM {image} AS mysql84\r\n");

        // Act
        var images = DatabaseImageCatalog.Parse(reader);

        // Assert
        var entry = Assert.Single(images);
        Assert.Equal("mysql84", entry.Key);
        Assert.Equal(image, entry.Value);
    }

    /// <summary>Every malformed suffix invalidates a catalog even after a valid selected prefix.</summary>
    /// <param name="invalid">An unsupported declaration or reference.</param>
    [Theory]
    [InlineData("RUN echo hidden")]
    [InlineData("FROM mysql:9.7.4 AS mysql97")]
    [InlineData("FROM mysql@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa AS mysql97")]
    [InlineData("FROM mysql:9.7.4@sha256:"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA AS mysql97")]
    [InlineData("FROM mysql:9.7.4@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa AS mysql97")]
    [InlineData("FROM mysql:9.7.4@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa AS invalid_name")]
    [InlineData("FROM mysql:9.7.4@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa AS mysql97 EXTRA")]
    [InlineData("FROM mysql:9.7.4@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa AS mysql97 # note")]
    public void Parse_InvalidEntryCannotHideBehindValidPrefix(
        string invalid
    )
    {
        // Arrange
        var valid = "FROM mysql:8.4.13@sha256:" + new string('b', 64) + " AS mysql84\n";
        using var reader = new StringReader(valid + invalid);

        // Act
        // WHY: Record.Exception invokes its synchronous callback before returning; the reader remains owned here.
        // ReSharper disable once AccessToDisposedClosure
        var failure = Record.Exception(() => DatabaseImageCatalog.Parse(reader));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Duplicate target identities are rejected even when their image references are equal.</summary>
    [Fact]
    public void Parse_DuplicateTargetRejectsCatalog()
    {
        // Arrange
        var declaration = "FROM mysql:8.4.13@sha256:" + new string('a', 64) + " AS mysql84\n";
        using var reader = new StringReader(declaration + declaration);

        // Act
        // WHY: Record.Exception finishes the parser callback before this method disposes its reader.
        // ReSharper disable once AccessToDisposedClosure
        var failure = Record.Exception(() => DatabaseImageCatalog.Parse(reader));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>An empty or comment-only manifest cannot establish an image inventory.</summary>
    /// <param name="manifest">The candidate empty catalog.</param>
    [Theory]
    [InlineData("")]
    [InlineData("# No targets\n\n")]
    public void Parse_EmptyCatalogRejectsInventory(
        string manifest
    )
    {
        // Arrange
        using var reader = new StringReader(manifest);

        // Act
        // WHY: The synchronous parser callback cannot survive Record.Exception or reach the reader after disposal.
        // ReSharper disable once AccessToDisposedClosure
        var failure = Record.Exception(() => DatabaseImageCatalog.Parse(reader));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }
}
