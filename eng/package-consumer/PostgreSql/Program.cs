const string designTimeReferenceAttributeName =
    "Microsoft.EntityFrameworkCore.Design.DesignTimeServicesReferenceAttribute";

const string designTimeServicesTypeName = "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationDesignTimeServices, "
    + "Doka.EntityFrameworkCore.SafeMigrations";

const string providerName = "Npgsql.EntityFrameworkCore.PostgreSQL";

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

_ = new DbContextOptionsBuilder().UsePostgreSqlSafeMigrations();

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

if (hasExpectedDesignTimeReference != expectsDesignTimeReference)
{
    return 2;
}

IServiceCollection services = new ServiceCollection();
services.AddPostgreSqlSafeMigrations();

Console.WriteLine(
    expectsDesignTimeReference
        ? "SafeMigrations PostgreSQL design-time package consumer verified."
        : "SafeMigrations PostgreSQL consumer without a design-service attribute verified.");

return 0;

internal sealed class PackageScaffoldingDbContext : DbContext
{
    public DbSet<PackageScaffoldingEntity> Entities => Set<PackageScaffoldingEntity>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseNpgsql("Host=127.0.0.1;Username=package;Password=package;Database=package");
        optionsBuilder.UsePostgreSqlSafeMigrations();
    }
}

internal sealed class PackageScaffoldingEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
