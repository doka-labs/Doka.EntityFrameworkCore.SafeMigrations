namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Captures the retained generated result after the unrelated column is removed.</summary>
[DbContext(typeof(GeneratedFacetPendingDropDbContext))]
public sealed class GeneratedFacetPendingDropModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
        => GeneratedFacetPendingDropDbContext.ConfigureModel(modelBuilder);
}
