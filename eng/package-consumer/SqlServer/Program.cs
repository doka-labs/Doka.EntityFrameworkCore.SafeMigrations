const string designTimeReferenceAttributeName =
    "Microsoft.EntityFrameworkCore.Design.DesignTimeServicesReferenceAttribute";

const string designTimeServicesTypeName =
    "Doka.EntityFrameworkCore.SafeMigrations.SqlServer.SqlServerSafeMigrationDesignTimeServices, "
    + "Doka.EntityFrameworkCore.SafeMigrations.SqlServer";

const string providerName = "Microsoft.EntityFrameworkCore.SqlServer";

var expectsDesignTimeReference = args switch
{
    [] => false,
    ["--expect-design-reference"] => true,
    _ => throw new ArgumentException("Usage: PackageConsumer [--expect-design-reference]"),
};

var migrationBuilder = new MigrationBuilder("PackageConsumer");
migrationBuilder.EnsureSchemaExists("consumer_schema");

if (migrationBuilder.Operations is not [SafeMigrationOperation])
{
    return 1;
}

_ = new DbContextOptionsBuilder().UseSqlServerSafeMigrations();

var designTimeReferences = Assembly
    .GetExecutingAssembly()
    .GetCustomAttributesData()
    .Where(attribute => attribute.AttributeType.FullName == designTimeReferenceAttributeName)
    .ToArray();

var hasExpectedDesignTimeReference = designTimeReferences is [{ ConstructorArguments.Count: 2 }]
    && designTimeReferences[0].ConstructorArguments[0].Value is string typeName
    && designTimeReferences[0].ConstructorArguments[1].Value is string referencedProvider
    && typeName == designTimeServicesTypeName
    && referencedProvider == providerName;

if (expectsDesignTimeReference ? !hasExpectedDesignTimeReference : designTimeReferences.Length != 0)
{
    Console.Error.WriteLine(
        expectsDesignTimeReference
            ? "The SQL Server package consumer is missing the expected design-time service reference."
            : "The runtime-only SQL Server package consumer contains an unexpected design-time service reference."
    );

    return 2;
}

IServiceCollection services = new ServiceCollection();
services.AddSqlServerSafeMigrations();

Console.WriteLine(
    expectsDesignTimeReference
        ? "SafeMigrations SQL Server design-time package consumer verified."
        : "SafeMigrations SQL Server consumer without a design-service attribute verified.");

return 0;

internal sealed class PackageScaffoldingDbContext : DbContext
{
    public DbSet<PackageScaffoldingEntity> Entities => Set<PackageScaffoldingEntity>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(
            "Server=127.0.0.1,1;Database=package;User ID=sa;Password=PackageConsumer123!;TrustServerCertificate=True");
        optionsBuilder.UseSqlServerSafeMigrations();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<PackageScaffoldingEntity>().HasData(
            new PackageScaffoldingEntity
            {
                Id = 1,
                Name = "package-consumer",
            });
    }
}

internal sealed class PackageScaffoldingEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
