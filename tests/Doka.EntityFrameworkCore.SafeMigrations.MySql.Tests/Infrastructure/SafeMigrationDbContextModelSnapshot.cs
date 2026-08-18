namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

[DbContext(typeof(SafeMigrationDbContext))]
public sealed class SafeMigrationDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
