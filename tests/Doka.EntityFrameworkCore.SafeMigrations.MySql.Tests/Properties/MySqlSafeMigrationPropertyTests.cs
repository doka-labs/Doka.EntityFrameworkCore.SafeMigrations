namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Exercises MySQL and MariaDB rendering and catalog-normalization invariants
/// over generated identifiers and untrusted provider fragments.
/// </summary>
public sealed class MySqlSafeMigrationPropertyTests : IDisposable
{
    private readonly SafeMigrationDbContext _context;
    private readonly MySqlSafeMigrationSqlExpressionRenderer _renderer;

    public MySqlSafeMigrationPropertyTests()
    {
        _context = new SafeMigrationDbContext(
            "Server=localhost;Database=properties;User ID=test;Password=test;AllowUserVariables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        _renderer = new MySqlSafeMigrationSqlExpressionRenderer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());
    }

    [Property(MaxTest = 1000)]
    public bool Render_RoundTripsArbitraryIdentifierParts(
        string? rawSchema,
        string? rawName
    )
    {
        var schema = Identifier(rawSchema, "Schema");
        var name = Identifier(rawName, "Name");
        var rendered = _renderer.Render(SafeMigrationSql.Identifier(schema, name));

        return StringComparer.Ordinal.Equals($"{Quote(schema)}.{Quote(name)}", rendered);
    }

    [Property(MaxTest = 1000)]
    public bool CatalogCanonicalizer_PreservesQuotedIdentifierTokens(
        string? rawIdentifier
    )
    {
        var identifier = Identifier(rawIdentifier, "Value");
        var quoted = Quote(identifier);
        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            $"(({quoted} IS NULL))",
            includeMySqlEncodedDisplay: false);

        return candidates.Contains($"{quoted} is null", StringComparer.Ordinal);
    }

    [Property(MaxTest = 500)]
    public bool Render_RejectsEveryForeignProviderFragment(
        string? rawSql
    )
    {
        var sql = Identifier(rawSql, "sql");

        try
        {
            _ = _renderer.Render(SafeMigrationSql.ProviderFragment("foreign_provider", sql));
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

    private static string Quote(
        string identifier
    ) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
}
