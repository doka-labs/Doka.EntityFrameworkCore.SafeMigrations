namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Supplies the canonical empty application model for hand-authored migration tests.
/// </summary>
[DbContext(typeof(SafeMigrationDbContext))]
public sealed class SafeMigrationDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
