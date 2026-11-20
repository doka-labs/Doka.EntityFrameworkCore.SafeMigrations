namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Builds final expected inventory through granular operations while retaining owner-qualified physical dependencies.
/// </summary>
public sealed partial class SafeMigrationExpectedCatalogTests
{
    private static SafeMigrationOperation Envelope(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);
}
