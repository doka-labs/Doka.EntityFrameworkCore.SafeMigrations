namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Exercises PostgreSQL rendering and catalog-query invariants over generated
/// identifiers and untrusted provider fragments.
/// </summary>
public sealed class PostgreSqlSafeMigrationPropertyTests : IDisposable
{
    private readonly SafeMigrationDbContext _context;
    private readonly PostgreSqlSafeMigrationSqlExpressionRenderer _renderer;

    public PostgreSqlSafeMigrationPropertyTests()
    {
        _context = new SafeMigrationDbContext(
            "Host=localhost;Database=properties;Username=test;Password=test");

        _renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());
    }

    [Property(MaxTest = 1000)]
    public bool Render_RoundTripsArbitraryIdentifierParts(
        string? rawSchema,
        string? rawName
    )
    {
        // An uppercase prefix forces PostgreSQL delimiting while retaining the
        // complete generated payload inside each identifier part.
        var schema = Identifier(rawSchema, "Schema");
        var name = Identifier(rawName, "Name");
        var rendered = _renderer.Render(SafeMigrationSql.Identifier(schema, name));

        return StringComparer.Ordinal.Equals($"{Quote(schema)}.{Quote(name)}", rendered);
    }

    [Property(MaxTest = 1000)]
    public bool CatalogCandidate_QuotesGeneratedIdentifiersThroughPgCatalog(
        string? rawIdentifier
    )
    {
        var identifier = Identifier(rawIdentifier, "Value");
        var rendered = _renderer.RenderCatalogCandidateSql(
            SafeMigrationSql.Identifier(identifier),
            Literal);

        return StringComparer.Ordinal.Equals(
            $"pg_catalog.quote_ident({Literal(identifier)})",
            rendered);
    }

    [Property(MaxTest = 1000)]
    public bool RenderedStructuredExpressions_ParseToAnEquivalentStableForm(
        string? rawIdentifier,
        int rawLiteral,
        int shapeSelector,
        byte depthSelector
    )
    {
        var expression = SafeMigrationSqlExpressionPropertyCases.Create(
            rawIdentifier,
            rawLiteral,
            shapeSelector,
            depthSelector);

        return SafeMigrationSqlExpressionPropertyCases.PreservesStableRoundTrip(expression, _renderer.Render);
    }

    [Property(MaxTest = 1000)]
    public bool RenderedStructuredExpressions_RejectAnAppendedStatement(
        string? rawIdentifier,
        int rawLiteral,
        int shapeSelector,
        byte depthSelector
    )
    {
        var expression = SafeMigrationSqlExpressionPropertyCases.Create(
            rawIdentifier,
            rawLiteral,
            shapeSelector,
            depthSelector);

        return SafeMigrationSqlExpressionPropertyCases.RejectsAppendedStatement(expression, _renderer.Render);
    }

    [Property(MaxTest = 500)]
    public bool Render_RejectsEveryForeignProviderFragment(
        string? rawSql
    )
    {
        var sql = Identifier(rawSql, "sql");
        var expression = SafeMigrationSql.ProviderFragment("foreign_provider", sql);
        if (_renderer.GetUnsupportedFeature(expression) != "provider_fragment_mismatch")
        {
            return false;
        }

        try
        {
            _ = _renderer.Render(expression);
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    public void Dispose() => _context.Dispose();

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
