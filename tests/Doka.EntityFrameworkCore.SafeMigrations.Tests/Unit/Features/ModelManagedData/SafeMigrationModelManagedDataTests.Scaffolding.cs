namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    [Fact]
    public void ScaffoldingRendersEveryModelManagedTransitionWithFrozenMetadata()
    {
        var generator = CreateModelDataOperationGenerator(isEnabled: true);
        var builder = new IndentedStringBuilder();
        var ensure = new EnsureModelManagedDataScaffoldingOperation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "code", "description"],
                ["int", "varchar(64)", "varchar(256)"],
                new object?[,] { { 1, "administrator", null }, { 2, "member", "Standard role" } },
                "identity",
                [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]));

        var update = new UpdateModelManagedDataScaffoldingOperation(
            new UpdateModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["code", "description"],
                ["varchar(64)", "varchar(256)"],
                new object?[,] { { "administrator", null } },
                new object?[,] { { "owner", "Built-in owner" } },
                "identity",
                [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]));

        var delete = new DeleteModelManagedDataScaffoldingOperation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 2 } },
                ["id", "code"],
                ["int", "varchar(64)"],
                new object?[,] { { 2, "member" } },
                "identity",
                [
                    new ExpectedModelManagedDataForeignKeyDefinition(
                        "user_roles",
                        ["role_id"],
                        ["id"],
                        "identity"),
                ]));

        generator.Generate("migrationBuilder", [ensure, update, delete], builder);
        var source = builder.ToString();

        Assert.Contains(".EnsureModelManagedDataFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(".UpdateModelManagedDataFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(".DeleteModelManagedDataFromModel(", source, StringComparison.Ordinal);
        Assert.Contains("schema: \"identity\"", source, StringComparison.Ordinal);
        Assert.Contains("uniqueKeys:", source, StringComparison.Ordinal);
        Assert.Contains(
            "new ExpectedModelManagedDataUniqueKeyDefinition([\"code\"])",
            source,
            StringComparison.Ordinal);
        Assert.Contains("foreignKeys:", source, StringComparison.Ordinal);
        Assert.Contains("new ExpectedModelManagedDataForeignKeyDefinition(", source, StringComparison.Ordinal);
        Assert.Contains("table: \"user_roles\"", source, StringComparison.Ordinal);
        Assert.Contains("principalColumns: [\"id\"]", source, StringComparison.Ordinal);
        Assert.Contains("keyValues: new object[,]", source, StringComparison.Ordinal);
        Assert.Contains("{ 1, \"administrator\", null }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("object?[,]", source, StringComparison.Ordinal);
        Assert.DoesNotContain(";;", source, StringComparison.Ordinal);
        Assert.Equal(3, source.Count(static character => character == ';'));
    }

    [Fact]
    public void DisabledScaffoldingDelegatesOrdinaryDataOperationsToEfCore()
    {
        var generator = CreateModelDataOperationGenerator(isEnabled: false);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new InsertDataOperation
            {
                Table = "roles",
                Columns = ["id", "name"],
                Values = new object?[,] { { 1, "administrator" } },
            },
            new UpdateDataOperation
            {
                Table = "roles",
                KeyColumns = ["id"],
                KeyValues = new object?[,] { { 1 } },
                Columns = ["name"],
                Values = new object?[,] { { "owner" } },
            },
            new DeleteDataOperation
            {
                Table = "roles",
                KeyColumns = ["id"],
                KeyValues = new object?[,] { { 1 } },
            },
        ];

        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        Assert.Contains(".InsertData(", source, StringComparison.Ordinal);
        Assert.Contains(".UpdateData(", source, StringComparison.Ordinal);
        Assert.Contains(".DeleteData(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelManagedDataFromModel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledScaffoldingRejectsEveryUnpairedEfDataOperation()
    {
        var generator = CreateModelDataOperationGenerator(isEnabled: true);
        MigrationOperation[] operations =
        [
            new InsertDataOperation(),
            new UpdateDataOperation(),
            new DeleteDataOperation(),
        ];

        foreach (var operation in operations)
        {
            var builder = new IndentedStringBuilder();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                generator.Generate("migrationBuilder", [operation], builder));

            Assert.Contains("unpaired", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(operation.GetType().Name, exception.Message, StringComparison.Ordinal);
            Assert.Equal("migrationBuilder", builder.ToString());
        }
    }

    [Theory]
    [InlineData((int)ModelDifferRegistration.Instance)]
    [InlineData((int)ModelDifferRegistration.Factory)]
    [InlineData((int)ModelDifferRegistration.ImplementationType)]
    public void ModelDifferDecoratorPreservesEveryProviderRegistrationShape(
        int registrationValue
    )
    {
        var registration = (ModelDifferRegistration)registrationValue;
        var operations = new MigrationOperation[] { new SqlOperation { Sql = "SELECT 1;" }, };
        var differ = new StubMigrationsModelDiffer(operations, hasDifferences: true);
        var services = new ServiceCollection();

        services.Add(registration switch
        {
            ModelDifferRegistration.Instance => ServiceDescriptor.Singleton<IMigrationsModelDiffer>(differ),
            ModelDifferRegistration.Factory => ServiceDescriptor.Singleton<IMigrationsModelDiffer>(_ => differ),
            ModelDifferRegistration.ImplementationType => ServiceDescriptor.Singleton<
                IMigrationsModelDiffer,
                StubMigrationsModelDiffer>(),
            _ => throw new UnreachableException(),
        });

        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(services);

        using var provider = services.BuildServiceProvider();

        var decorated = Assert.IsType<SafeMigrationMigrationsModelDiffer>(
            provider.GetRequiredService<IMigrationsModelDiffer>());

        Assert.Equal(registration != ModelDifferRegistration.ImplementationType,
            decorated.HasDifferences(source: null, target: null));
        Assert.Same(
            registration == ModelDifferRegistration.ImplementationType
                ? StubMigrationsModelDiffer.EmptyOperations
                : operations,
            decorated.GetDifferences(source: null, target: null));
    }

    [Fact]
    public void ModelDifferDecoratorIsIdempotentAndRejectsNullServices()
    {
        var empty = new ServiceCollection();

        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(empty);

        Assert.Empty(empty);
        Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(null!));

        var decorated = new ServiceCollection();
        decorated.AddSingleton<IMigrationsModelDiffer, SafeMigrationMigrationsModelDiffer>();

        var descriptor = Assert.Single(decorated);

        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(decorated);

        Assert.Same(descriptor, Assert.Single(decorated));
    }

    private static SafeMigrationCSharpMigrationOperationGenerator CreateModelDataOperationGenerator(
        bool isEnabled
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, ModelDataNullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var dependencies = provider.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>();

        return new SafeMigrationCSharpMigrationOperationGenerator(
            dependencies,
            new SafeMigrationScaffoldingConfiguration(
                isEnabled,
                SafeMigrationScaffoldingMode.Strict),
            createIndexProjectors: []);
    }

    private enum ModelDifferRegistration
    {
        Instance,
        Factory,
        ImplementationType,
    }

    private sealed class StubMigrationsModelDiffer : IMigrationsModelDiffer
    {
        internal static readonly IReadOnlyList<MigrationOperation> EmptyOperations = [];

        private readonly IReadOnlyList<MigrationOperation> _operations;
        private readonly bool _hasDifferences;

        public StubMigrationsModelDiffer() : this(EmptyOperations, hasDifferences: false)
        {
        }

        public StubMigrationsModelDiffer(
            IReadOnlyList<MigrationOperation> operations,
            bool hasDifferences
        )
        {
            _operations = operations;
            _hasDifferences = hasDifferences;
        }

        public bool HasDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => _hasDifferences;

        public IReadOnlyList<MigrationOperation> GetDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => _operations;
    }

    private sealed class ModelDataNullTypeMappingSource : ITypeMappingSource
    {
        public CoreTypeMapping? FindMapping(
            IProperty property
        ) => null;

        public CoreTypeMapping? FindMapping(
            IElementType elementType
        ) => null;

        public CoreTypeMapping? FindMapping(
            System.Reflection.MemberInfo member
        ) => null;

        public CoreTypeMapping? FindMapping(
            System.Reflection.MemberInfo member,
            IModel model,
            bool useAttributes
        ) => null;

        public CoreTypeMapping? FindMapping(
            Type type
        ) => null;

        public CoreTypeMapping? FindMapping(
            Type type,
            IModel model,
            CoreTypeMapping? elementMapping = null
        ) => null;
    }
}
