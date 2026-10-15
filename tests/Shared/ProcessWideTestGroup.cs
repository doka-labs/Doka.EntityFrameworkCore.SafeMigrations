namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Serializes only tests whose measured or mutable contracts are process-wide.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideTestGroup
{
    /// <summary>The common collection name linked once into each test assembly.</summary>
    public const string Name = "Process-wide test contracts";
}
