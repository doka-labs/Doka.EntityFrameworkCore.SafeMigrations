namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies SafeMigrations Db Context Model Snapshot contracts in independently owned test state.</summary>
[DbContext(typeof(SafeMigrationDbContext))]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
