using var context = new SqlServerGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var catalogBuilder = new SqlServerSafeMigrationCatalogSqlBuilder(
    context.GetService<IRelationalTypeMappingSource>(),
    context.GetService<ISqlGenerationHelper>());

var baselineCapture = args is ["--capture-baseline", "--output", var path]
    ? new SqlServerBaselineCapture(Path.GetFullPath(path))
    : null;

var runner = baselineCapture is null
    ? BenchmarkRunner.Create(args, "sqlserver-results.json", "sqlserver")
    : null;

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    Measure(
        $"sqlserver_generation_{size}",
        () => generator.Generate(operations, context.Model).Count);

    if (size is 1 or 1000)
    {
        Measure($"sqlserver_analyzer_build_{size}", () => BuildPlans(catalogBuilder, operations));
    }
}

Measure(
    "sqlserver_canonical_model_validation",
    () => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            context,
            "Microsoft.EntityFrameworkCore.SqlServer")
        .Length);

var analyzerOperations = ColumnBenchmarkWorkload.CreateOperations(512);

Measure(
    "sqlserver_analyzer_build_512",
    () => BuildPlans(catalogBuilder, analyzerOperations));

var repairOperations = SqlServerColumnBenchmarkWorkload.CreateRepairOperations(1000);

foreach (var repairOperation in repairOperations.Cast<SafeMigrationOperation>())
{
    var plan = catalogBuilder.Build(repairOperation);

    if (repairOperation is not { Policy: SafeMigrationPolicy.RepairIfSafe, Intent: AlterColumnIntent }
        || plan.IsStaticallyUnsupported
        || plan.RepairCapability != SafeMigrationRepairCapability.Safe)
    {
        throw new InvalidOperationException("The SQL Server repair benchmark requires supported column repair plans.");
    }
}

Measure(
    "sqlserver_repair_generation_1000",
    () => generator.Generate(repairOperations, context.Model).Count);

Measure(
    "sqlserver_repair_analyzer_build_1000",
    () => BuildPlans(catalogBuilder, repairOperations));

var modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
    context.Database.ProviderName!,
    "int",
    "nvarchar(64)");

Measure(
    "sqlserver_model_data_generation_384",
    () => generator.Generate(modelManagedOperations, context.Model).Count);

Measure(
    "sqlserver_model_data_analyzer_build_384",
    () => BuildPlans(catalogBuilder, modelManagedOperations));

return baselineCapture?.Complete() ?? runner!.Complete();

void Measure(
    string name,
    Func<int> action
)
{
    if (baselineCapture is not null)
    {
        baselineCapture.Measure(name, action);

        return;
    }

    runner!.Measure(name, action);
}

static int BuildPlans(
    SqlServerSafeMigrationCatalogSqlBuilder catalogBuilder,
    IReadOnlyList<MigrationOperation> operations
)
{
    var count = 0;

    foreach (var operation in operations.Cast<SafeMigrationOperation>())
    {
        _ = catalogBuilder.Build(operation);
        count++;
    }

    return count;
}

internal sealed class SqlServerGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(
            "Server=127.0.0.1,1;Database=benchmark;User ID=sa;Password=BenchmarkOnly123!;TrustServerCertificate=True");
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerGenerationContext>();
    }
}

[DbContext(typeof(SqlServerGenerationContext))]
internal sealed class SqlServerGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    )
        => ArgumentNullException.ThrowIfNull(modelBuilder);
}
