namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Exercises MySQL and MariaDB rendering and catalog-normalization invariants
/// over generated identifiers and untrusted provider fragments.
/// </summary>
public sealed class MySqlSafeMigrationPropertyTests : IDisposable
{
    private readonly SafeMigrationDbContext _context;
    private readonly MySqlSafeMigrationSqlExpressionRenderer _renderer;

    /// <summary>Binds this test owner to its explicitly owned provider resources.</summary>
    public MySqlSafeMigrationPropertyTests()
    {
        _context = new SafeMigrationDbContext(
            "Server=localhost;Database=properties;User ID=test;Password=test;AllowUserVariables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        _renderer = new MySqlSafeMigrationSqlExpressionRenderer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());
    }

    /// <inheritdoc />
    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Requires arbitrary identifier parts to survive provider quoting without changing their logical identity.
    /// </summary>
    /// <param name="rawSchema">The arbitrary generated schema component.</param>
    /// <param name="rawName">The arbitrary generated identifier component.</param>
    [Property(MaxTest = 1000)]
    public bool Render_RoundTripsArbitraryIdentifierParts(
        string? rawSchema,
        string? rawName
    )
    {
        // Arrange
        var schema = Identifier(rawSchema, "Schema");
        var name = Identifier(rawName, "Name");

        // Act
        var rendered = _renderer.Render(SafeMigrationSql.Identifier(schema, name));

        // Assert
        return StringComparer.Ordinal.Equals($"{Quote(schema)}.{Quote(name)}", rendered);
    }

    /// <summary>
    /// Preserves quoted identifier tokens while canonicalizing catalog formatting outside quoted regions.
    /// </summary>
    /// <param name="rawIdentifier">The arbitrary generated identifier before provider quoting.</param>
    [Property(MaxTest = 1000)]
    public bool CatalogCanonicalizer_PreservesQuotedIdentifierTokens(
        string? rawIdentifier
    )
    {
        // Arrange
        var identifier = Identifier(rawIdentifier, "Value");
        var quoted = Quote(identifier);

        // Act
        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            $"(({quoted} IS NULL))",
            includeMySqlEncodedDisplay: false);

        // Assert
        return candidates.Contains($"{quoted} is null", StringComparer.Ordinal);
    }

    /// <summary>Requires structured rendering and parsing to agree on one stable expression identity.</summary>
    /// <param name="rawIdentifier">The arbitrary generated identifier before provider quoting.</param>
    /// <param name="rawLiteral">The arbitrary generated value before structured literal rendering.</param>
    /// <param name="shapeSelector">The generated selector choosing a bounded structured-expression shape.</param>
    /// <param name="depthSelector">The generated selector bounding structured expression depth.</param>
    [Property(MaxTest = 1000)]
    public bool RenderedStructuredExpressions_ParseToAnEquivalentStableForm(
        string? rawIdentifier,
        int rawLiteral,
        int shapeSelector,
        byte depthSelector
    )
    {
        // Arrange
        var expression = SafeMigrationSqlExpressionPropertyCases.Create(
            rawIdentifier,
            rawLiteral,
            shapeSelector,
            depthSelector);

        // Act
        var preservesRoundTrip = SafeMigrationSqlExpressionPropertyCases.PreservesStableRoundTrip(
            expression,
            _renderer.Render);

        // Assert
        return preservesRoundTrip;
    }

    /// <summary>Rejects a second statement appended to an otherwise valid structured expression.</summary>
    /// <param name="rawIdentifier">The arbitrary generated identifier before provider quoting.</param>
    /// <param name="rawLiteral">The arbitrary generated value before structured literal rendering.</param>
    /// <param name="shapeSelector">The generated selector choosing a bounded structured-expression shape.</param>
    /// <param name="depthSelector">The generated selector bounding structured expression depth.</param>
    [Property(MaxTest = 1000)]
    public bool RenderedStructuredExpressions_RejectAnAppendedStatement(
        string? rawIdentifier,
        int rawLiteral,
        int shapeSelector,
        byte depthSelector
    )
    {
        // Arrange
        var expression = SafeMigrationSqlExpressionPropertyCases.Create(
            rawIdentifier,
            rawLiteral,
            shapeSelector,
            depthSelector);

        // Act
        var rejectsStatement = SafeMigrationSqlExpressionPropertyCases.RejectsAppendedStatement(
            expression,
            _renderer.Render);

        // Assert
        return rejectsStatement;
    }

    /// <summary>Rejects foreign-provider fragments regardless of the generated fragment payload.</summary>
    /// <param name="rawSql">The arbitrary generated foreign-provider fragment that must remain rejected.</param>
    [Property(MaxTest = 500)]
    public bool Render_RejectsEveryForeignProviderFragment(
        string? rawSql
    )
    {
        // Arrange
        var sql = Identifier(rawSql, "sql");
        var expression = SafeMigrationSql.ProviderFragment("foreign_provider", sql);
        var rejected = false;

        // Act
        var unsupportedFeature = _renderer.GetUnsupportedFeature(expression);
        if (unsupportedFeature == "provider_fragment_mismatch")
        {
            try
            {
                _ = _renderer.Render(expression);
            }
            catch (NotSupportedException)
            {
                rejected = true;
            }
        }

        // Assert
        return rejected;
    }

    private static string Identifier(
        string? value,
        string prefix
    ) => $"{prefix}_{value ?? "null"}";

    private static string Quote(
        string identifier
    ) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
}
