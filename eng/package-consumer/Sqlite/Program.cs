const string designTimeReferenceAttributeName =
    "Microsoft.EntityFrameworkCore.Design.DesignTimeServicesReferenceAttribute";

const string designTimeServicesTypeName = "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationDesignTimeServices, "
    + "Doka.EntityFrameworkCore.SafeMigrations";

const string providerName = "Microsoft.EntityFrameworkCore.Sqlite";

if (args is ["--verify-database", var databasePath, var migrationId, var transitionMigrationId])
{
    return VerifyDatabase(databasePath, migrationId, transitionMigrationId);
}

var expectsDesignTimeReference = args switch
{
    [] => false,
    ["--expect-design-reference"] => true,
    _ => throw new ArgumentException("Usage: PackageConsumer [--expect-design-reference]"),
};

GeneratedExpressionConsumerContract.Verify();

var migrationBuilder = new MigrationBuilder(providerName);
migrationBuilder.CreateTableIfNotExists(
    "consumer_items",
    columns: table => new
    {
        Id = table.Column<int>(type: "INTEGER", nullable: false),
    },
    constraints: table => table.PrimaryKey("PK_consumer_items", item => item.Id));

if (migrationBuilder.Operations is not [SafeMigrationOperation])
{
    return 1;
}

_ = new DbContextOptionsBuilder().UseSqliteSafeMigrations();

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
    Console.Error.WriteLine(
        expectsDesignTimeReference
            ? "The SQLite package consumer is missing the expected design-time service reference."
            : "The runtime-only SQLite package consumer contains an unexpected design-time service reference."
    );

    return 2;
}

IServiceCollection services = new ServiceCollection();
services.AddSqliteSafeMigrations();

Console.WriteLine(
    expectsDesignTimeReference
        ? "SafeMigrations SQLite design-time package consumer verified."
        : "SafeMigrations SQLite consumer without a design-service attribute verified.");

return 0;

static int VerifyDatabase(
    string databasePath,
    string migrationId,
    string transitionMigrationId
)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

    // WHY: A missing target must fail verification rather than silently creating
    // an empty database. The builder also preserves paths containing delimiters.
    var settings = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
    };

    try
    {
        using var connection = new SqliteConnection(settings.ConnectionString);
        connection.Open();
        Doka.EntityFrameworkCore.SafeMigrations.Sqlite.PackageConsumer.ToolingDatabaseVerifier.Verify(
            connection, migrationId, transitionMigrationId);
    }
    catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
    {
        Console.Error.WriteLine("SQLite tooling database verification failed: " + exception.Message);

        return 3;
    }

    Console.WriteLine(
        "SQLite tooling database history, target schema, generated expressions, data, and integrity verified.");

    return 0;
}

internal sealed class PackageScaffoldingDbContext : DbContext
{
    public DbSet<PackageScaffoldingEntity> Entities => Set<PackageScaffoldingEntity>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite("Data Source=package-consumer.db");
        optionsBuilder.UseSqliteSafeMigrations();
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
