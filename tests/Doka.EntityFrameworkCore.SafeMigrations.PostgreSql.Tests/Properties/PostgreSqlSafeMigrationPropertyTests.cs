namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Exercises PostgreSQL rendering and catalog-query invariants over generated
/// identifiers and untrusted provider fragments.
/// </summary>
public sealed class PostgreSqlSafeMigrationPropertyTests : IDisposable
{
    private readonly SafeMigrationDbContext _context;
    private readonly PostgreSqlSafeMigrationSqlExpressionRenderer _renderer;

    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public PostgreSqlSafeMigrationPropertyTests()
    {
        _context = new SafeMigrationDbContext(
            "Host=localhost;Database=properties;Username=test;Password=test");

        _renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());
    }

    /// <summary>Dispose.</summary>
    public void Dispose() => _context.Dispose();

    /// <summary>Render Round Trips Arbitrary Identifier Parts.</summary>
    /// <param name="rawSchema">The generated payload retained inside the delimited schema identifier.</param>
    /// <param name="rawName">The generated payload retained inside the delimited object identifier.</param>
    /// <returns>Whether rendering preserves both independently delimited identifier parts.</returns>
    [Property(MaxTest = 1000)]
    public bool Render_RoundTripsArbitraryIdentifierParts(
        string? rawSchema,
        string? rawName
    )
    {
        // Arrange
        // An uppercase prefix forces PostgreSQL delimiting while retaining the
        // complete generated payload inside each identifier part.
        var schema = Identifier(rawSchema, "Schema");
        var name = Identifier(rawName, "Name");
        var expected = $"{Quote(schema)}.{Quote(name)}";

        // Act
        var rendered = _renderer.Render(SafeMigrationSql.Identifier(schema, name));

        // Assert
        return StringComparer.Ordinal.Equals(expected, rendered);
    }

    /// <summary>Catalog Candidate Quotes Generated Identifiers Through Pg Catalog.</summary>
    /// <param name="rawIdentifier">The generated payload retained inside the catalog candidate identifier.</param>
    /// <returns>Whether the candidate delegates identifier quoting to the native catalog function.</returns>
    [Property(MaxTest = 1000)]
    public bool CatalogCandidate_QuotesGeneratedIdentifiersThroughPgCatalog(
        string? rawIdentifier
    )
    {
        // Arrange
        var identifier = Identifier(rawIdentifier, "Value");
        var expected = $"pg_catalog.quote_ident({Literal(identifier)})";

        // Act
        var rendered = _renderer.RenderCatalogCandidateSql(
            SafeMigrationSql.Identifier(identifier),
            Literal);

        // Assert
        return StringComparer.Ordinal.Equals(expected, rendered);
    }

    /// <summary>Rendered Structured Expressions Parse To An Equivalent Stable Form.</summary>
    /// <param name="rawIdentifier">The generated identifier payload used by the bounded expression source.</param>
    /// <param name="rawLiteral">The generated literal value embedded in the expression.</param>
    /// <param name="shapeSelector">Selects the expression shape without reducing its generated input range.</param>
    /// <param name="depthSelector">Selects the bounded expression nesting depth.</param>
    /// <returns>Whether native rendering round-trips through the parser to the same stable expression.</returns>
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

    /// <summary>Rendered Structured Expressions Reject An Appended Statement.</summary>
    /// <param name="rawIdentifier">The generated identifier payload used by the bounded expression source.</param>
    /// <param name="rawLiteral">The generated literal value embedded in the expression.</param>
    /// <param name="shapeSelector">Selects the expression shape without reducing its generated input range.</param>
    /// <param name="depthSelector">Selects the bounded expression nesting depth.</param>
    /// <returns>Whether the parser rejects a second statement appended to the rendered expression.</returns>
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

    /// <summary>Render Rejects Every Foreign Provider Fragment.</summary>
    /// <param name="rawSql">The generated opaque SQL payload belonging to an unrelated provider.</param>
    /// <returns>Whether both capability classification and actual rendering reject the foreign fragment.</returns>
    [Property(MaxTest = 500)]
    public bool Render_RejectsEveryForeignProviderFragment(
        string? rawSql
    )
    {
        // Arrange
        var sql = Identifier(rawSql, "sql");
        var expression = SafeMigrationSql.ProviderFragment("foreign_provider", sql);
        NotSupportedException? failure = null;

        // Act
        var unsupportedFeature = _renderer.GetUnsupportedFeature(expression);
        try
        {
            _ = _renderer.Render(expression);
        }
        catch (NotSupportedException exception)
        {
            failure = exception;
        }

        // Assert
        return unsupportedFeature == "provider_fragment_mismatch" && failure is not null;
    }

    private static string Identifier(
        string? value,
        string prefix
    ) => $"{prefix}_{value ?? "null"}";

    private static string Literal(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Quote(
        string identifier
    ) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
