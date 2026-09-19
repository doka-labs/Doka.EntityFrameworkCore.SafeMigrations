const string designTimeReferenceAttributeName =
    "Microsoft.EntityFrameworkCore.Design.DesignTimeServicesReferenceAttribute";

const string designTimeServicesTypeName = "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationDesignTimeServices, "
    + "Doka.EntityFrameworkCore.SafeMigrations";

const string providerName = "Microsoft.EntityFrameworkCore.Sqlite";

if (args is ["--verify-database", var databasePath, var migrationId])
{
    return VerifyDatabase(databasePath, migrationId);
}

var expectsDesignTimeReference = args switch
{
    [] => false,
    ["--expect-design-reference"] => true,
    _ => throw new ArgumentException("Usage: PackageConsumer [--expect-design-reference]"),
};

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
    string migrationId
)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

    using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True");
    connection.Open();

    var expectedState = ExecuteScalarInt(
        connection,
        "SELECT "
        + "(SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $migration) + "
        + "(SELECT COUNT(*) FROM \"tooling_entities\" WHERE \"Id\" = 1 AND \"Code\" = 'baseline') + "
        + "(SELECT COUNT(*) FROM pragma_table_info('tooling_entities') WHERE name IN ('Id', 'Code')) + "
        + "(SELECT COUNT(*) FROM pragma_index_list('tooling_entities') WHERE \"unique\" = 1);",
        migrationId);

    var foreignKeyViolations = ExecuteScalarInt(
        connection,
        "SELECT COUNT(*) FROM pragma_foreign_key_check;");

    if (expectedState != 5 || foreignKeyViolations != 0)
    {
        Console.Error.WriteLine("SQLite tooling database verification failed.");

        return 3;
    }

    Console.WriteLine("SQLite tooling database history, schema, data, and integrity verified.");

    return 0;
}

static int ExecuteScalarInt(
    SqliteConnection connection,
    string commandText,
    string? migrationId = null
)
{
    using var command = connection.CreateCommand();
    command.CommandText = commandText;
    if (migrationId is not null)
    {
        _ = command.Parameters.AddWithValue("$migration", migrationId);
    }

    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
