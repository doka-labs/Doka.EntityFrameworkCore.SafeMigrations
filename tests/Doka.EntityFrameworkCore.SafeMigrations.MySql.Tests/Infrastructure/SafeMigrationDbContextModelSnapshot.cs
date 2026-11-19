namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves the canonical baseline model used by pending-migration analysis.</summary>
[DbContext(typeof(SafeMigrationDbContext))]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
