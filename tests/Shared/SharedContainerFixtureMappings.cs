namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Builds non-owning injection mappings without altering teardown-owned fixture dictionaries.</summary>
internal static class SharedContainerFixtureMappings
{
    /// <summary>Copies ordinary collection fixtures and adds the assembly-owned instance for injection.</summary>
    /// <typeparam name="TFixture">The shared fixture type.</typeparam>
    /// <param name="collectionFixtures">The collection's teardown-owned mappings.</param>
    /// <param name="sharedFixture">The assembly-owned fixture, when required by the selection.</param>
    /// <returns>A separate mapping consumed only by the test class runner.</returns>
    public static IDictionary<Type, object> Merge<TFixture>(
        IDictionary<Type, object> collectionFixtures,
        TFixture? sharedFixture
    ) where TFixture : class
    {
        var mappings = new Dictionary<Type, object>(collectionFixtures);

        if (sharedFixture is not null)
        {
            mappings[typeof(TFixture)] = sharedFixture;
        }

        return mappings;
    }
}
