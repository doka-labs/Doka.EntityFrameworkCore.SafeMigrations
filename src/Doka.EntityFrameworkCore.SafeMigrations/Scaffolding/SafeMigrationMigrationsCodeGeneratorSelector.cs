namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Selects the provider-owned migrations generator and decorates its migration
/// source without taking ownership of provider metadata or snapshots.
/// </summary>
/// <remarks>
/// EF Core loads referenced design-time services before provider and default
/// design-time services. Decorating at selection time preserves that ordering:
/// the complete provider-generator set is available only after the service
/// provider has been built.
/// </remarks>
internal sealed class SafeMigrationMigrationsCodeGeneratorSelector : IMigrationsCodeGeneratorSelector
{
    private readonly IMigrationsCodeGenerator[] _decoratedGenerators;
    private readonly SafeMigrationScaffoldingConfiguration _configuration;
    private readonly IMigrationsCodeGenerator[] _providerGenerators;

    /// <summary>Initializes the deferred provider-generator selector.</summary>
    /// <param name="providerGenerators">The complete provider-generator set.</param>
    /// <param name="csharpHelper">The EF Core C# identifier formatter.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    public SafeMigrationMigrationsCodeGeneratorSelector(
        IEnumerable<IMigrationsCodeGenerator> providerGenerators,
        ICSharpHelper csharpHelper,
        SafeMigrationScaffoldingConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(providerGenerators);
        ArgumentNullException.ThrowIfNull(csharpHelper);
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
        _providerGenerators = providerGenerators.ToArray();
        _decoratedGenerators = new IMigrationsCodeGenerator[_providerGenerators.Length];

        for (var index = 0; index < _providerGenerators.Length; index++)
        {
            _decoratedGenerators[index] = new SafeMigrationCSharpMigrationsGenerator(
                _providerGenerators[index],
                csharpHelper,
                configuration);
        }
    }

    /// <inheritdoc />
    public IMigrationsCodeGenerator Select(
        string? language
    )
    {
        var requestedLanguage = string.IsNullOrEmpty(language)
            ? "C#"
            : language;

        if (_configuration.IsEnabled
            && !StringComparer.OrdinalIgnoreCase.Equals(requestedLanguage, "C#"))
        {
            throw UnsupportedLanguage(requestedLanguage);
        }

        // Preserve EF Core's legacy-generator precedence before applying its
        // case-insensitive last-match rule for C# generators. SafeMigrations
        // rewrites C# source and must not decorate an explicitly different
        // language even when that provider generator is registered.
        for (var index = _providerGenerators.Length - 1; index >= 0; index--)
        {
            if (_providerGenerators[index].Language is null)
            {
                return _decoratedGenerators[index];
            }
        }

        for (var index = _providerGenerators.Length - 1; index >= 0; index--)
        {
            if (string.Equals(
                    _providerGenerators[index].Language,
                    requestedLanguage,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _decoratedGenerators[index];
            }
        }

        throw UnsupportedLanguage(requestedLanguage);
    }

    private static OperationException UnsupportedLanguage(
        string language
    ) => new($"No SafeMigrations code generator supports language '{language}'.");
}
