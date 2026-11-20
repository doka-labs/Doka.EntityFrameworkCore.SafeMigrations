namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>One generated seed lifecycle retains captured types, source values and dependency metadata.</summary>
    [Fact]
    public void ScaffoldingRendersEveryModelManagedTransitionWithFrozenMetadata()
    {
        // Arrange
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

        // Act
        generator.Generate("migrationBuilder", [ensure, update, delete], builder);
        var source = builder.ToString();

        // Assert
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

    /// <summary>Disabled safe scaffolding preserves ordinary EF insert, update and delete generation.</summary>
    [Fact]
    public void DisabledScaffoldingDelegatesOrdinaryDataOperationsToEfCore()
    {
        // Arrange
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

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".InsertData(", source, StringComparison.Ordinal);
        Assert.Contains(".UpdateData(", source, StringComparison.Ordinal);
        Assert.Contains(".DeleteData(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelManagedDataFromModel", source, StringComparison.Ordinal);
    }

    /// <summary>Each unpaired ordinary EF data operation fails before emitting partial migration source.</summary>
    /// <param name="kind">The independent ordinary EF data-operation family.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EnabledScaffoldingRejectsEveryUnpairedEfDataOperation(
        int kind
    )
    {
        // Arrange
        var generator = CreateModelDataOperationGenerator(isEnabled: true);
        MigrationOperation[] operations =
        [
            new InsertDataOperation(),
            new UpdateDataOperation(),
            new DeleteDataOperation(),
        ];

        var operation = operations[kind];
        var builder = new IndentedStringBuilder();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Contains("unpaired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(operation.GetType()
                .Name,
            exception.Message,
            StringComparison.Ordinal);

        Assert.Empty(builder.ToString());
    }

    /// <summary>Decoration retains the provider's registration identity and exact difference behavior.</summary>
    /// <param name="registrationValue">The independently registered instance, factory or implementation type.</param>
    [Theory]
    [InlineData((int)ModelDifferRegistration.Instance)]
    [InlineData((int)ModelDifferRegistration.Factory)]
    [InlineData((int)ModelDifferRegistration.ImplementationType)]
    public void ModelDifferDecoratorPreservesEveryProviderRegistrationShape(
        int registrationValue
    )
    {
        // Arrange
        var registration = (ModelDifferRegistration)registrationValue;
        var operations = new MigrationOperation[] { new SqlOperation { Sql = "SELECT 1;" } };
        var differ = new StubMigrationsModelDiffer(operations, hasDifferences: true);
        var services = new ServiceCollection
        {
            registration switch
            {
                ModelDifferRegistration.Instance => ServiceDescriptor.Singleton<IMigrationsModelDiffer>(differ),
                ModelDifferRegistration.Factory => ServiceDescriptor.Singleton<IMigrationsModelDiffer>(_ => differ),
                ModelDifferRegistration.ImplementationType => ServiceDescriptor.Singleton<
                    IMigrationsModelDiffer,
                    StubMigrationsModelDiffer>(),
                _ => throw new UnreachableException(),
            },
        };

        // Act
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(services);

        using var provider = services.BuildServiceProvider();

        var decorated = Assert.IsType<SafeMigrationMigrationsModelDiffer>(
            provider.GetRequiredService<IMigrationsModelDiffer>());

        var hasDifferences = decorated.HasDifferences(source: null, target: null);
        var differences = decorated.GetDifferences(source: null, target: null);

        // Assert
        Assert.Equal(
            registration != ModelDifferRegistration.ImplementationType,
            hasDifferences);

        Assert.Same(
            registration == ModelDifferRegistration.ImplementationType
                ? StubMigrationsModelDiffer.EmptyOperations
                : operations,
            differences);
    }

    /// <summary>An absent provider registration remains absent rather than receiving an unusable decorator.</summary>
    [Fact]
    public void ModelDifferDecoratorPreservesEmptyServices()
    {
        // Arrange
        var empty = new ServiceCollection();

        // Act
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(empty);

        // Assert
        Assert.Empty(empty);
    }

    /// <summary>The decorator rejects a missing service collection at its public boundary.</summary>
    [Fact]
    public void ModelDifferDecoratorRejectsNullServices()
    {
        // Arrange: A null collection cannot contain a provider registration.

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>An explicitly registered decorator is not replaced or nested again.</summary>
    [Fact]
    public void ModelDifferDecoratorPreservesExistingDecorator()
    {
        // Arrange
        var decorated = new ServiceCollection();
        decorated.AddSingleton<IMigrationsModelDiffer, SafeMigrationMigrationsModelDiffer>();

        var descriptor = Assert.Single(decorated);

        // Act
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(decorated);

        // Assert
        Assert.Same(descriptor, Assert.Single(decorated));
    }

    /// <summary>A second decoration reuses the original decorated provider-factory descriptor.</summary>
    [Fact]
    public void ModelDifferDecoratorIsIdempotentForProviderFactory()
    {
        // Arrange
        var providerFactory = new ServiceCollection();
        providerFactory.AddSingleton<IMigrationsModelDiffer>(_ => new StubMigrationsModelDiffer());
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(providerFactory);
        var factoryDescriptor = Assert.Single(providerFactory);

        // Act
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(providerFactory);

        // Assert
        Assert.Same(factoryDescriptor, Assert.Single(providerFactory));
    }

    /// <summary>A model differ cannot infer whether safe scaffolding is enabled from absent configuration.</summary>
    [Fact]
    public void ModelDifferRequiresExplicitScaffoldingConfiguration()
    {
        // Arrange
        var providerDiffer = new StubMigrationsModelDiffer();

        // Act
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SafeMigrationMigrationsModelDiffer(providerDiffer, null!));

        // Assert
        Assert.Equal("configuration", exception.ParamName);
    }

    /// <summary>Nested enabled decorators prepend exactly one guard while retaining the provider operation.</summary>
    [Fact]
    public void EnabledModelDifferPrependsOneDesignTimeServicesGuardAcrossNestedDecoration()
    {
        // Arrange
        var providerOperation = new SqlOperation { Sql = "SELECT 1;" };
        var providerDiffer = new StubMigrationsModelDiffer(
            [providerOperation],
            hasDifferences: true);

        var configuration = new SafeMigrationScaffoldingConfiguration(
            IsEnabled: true,
            Mode: SafeMigrationScaffoldingMode.Strict);

        var inner = new SafeMigrationMigrationsModelDiffer(providerDiffer, configuration);
        var outer = new SafeMigrationMigrationsModelDiffer(inner, configuration);

        // Act
        var operations = outer.GetDifferences(source: null, target: null);

        // Assert
        Assert.Collection(
            operations,
            operation => Assert.IsType<SafeMigrationDesignTimeServicesRequiredOperation>(operation),
            operation => Assert.Same(providerOperation, operation));
    }

    /// <summary>A disabled decorator preserves the provider's exact operation-list reference without a guard.</summary>
    [Fact]
    public void DisabledModelDifferPreservesProviderOperationsWithoutAGuard()
    {
        // Arrange
        var providerOperation = new SqlOperation { Sql = "SELECT 1;" };
        var providerOperations = new MigrationOperation[] { providerOperation };
        var providerDiffer = new StubMigrationsModelDiffer(
            providerOperations,
            hasDifferences: true);

        var configuration = new SafeMigrationScaffoldingConfiguration(
            IsEnabled: false,
            Mode: SafeMigrationScaffoldingMode.Strict);

        var differ = new SafeMigrationMigrationsModelDiffer(providerDiffer, configuration);

        // Act
        var operations = differ.GetDifferences(source: null, target: null);

        // Assert
        Assert.Same(providerOperations, operations);
        Assert.Same(providerOperation, Assert.Single(operations));
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
            createIndexProjectors: [],
            providerOperationAdapters: []);
    }

    private enum ModelDifferRegistration
    {
        Instance,
        Factory,
        ImplementationType,
    }

    /// <summary>Retains provider difference identity independently of the safe decorator.</summary>
    private sealed class StubMigrationsModelDiffer : IMigrationsModelDiffer
    {
        internal static readonly IReadOnlyList<MigrationOperation> EmptyOperations = [];
        private readonly bool _hasDifferences;

        private readonly IReadOnlyList<MigrationOperation> _operations;

        /// <summary>Creates the implementation-type registration with no provider differences.</summary>
        public StubMigrationsModelDiffer() : this(EmptyOperations, hasDifferences: false) { }

        /// <summary>Captures the exact provider list and independently advertised difference flag.</summary>
        /// <param name="operations">The operation list returned without copying.</param>
        /// <param name="hasDifferences">The provider's advertised difference result.</param>
        public StubMigrationsModelDiffer(
            IReadOnlyList<MigrationOperation> operations,
            bool hasDifferences
        )
        {
            _operations = operations;
            _hasDifferences = hasDifferences;
        }

        /// <inheritdoc />
        public bool HasDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => _hasDifferences;

        /// <inheritdoc />
        public IReadOnlyList<MigrationOperation> GetDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => _operations;
    }

    /// <summary>Leaves values unmapped so generated source exercises its ordinary CLR literal fallback.</summary>
    private sealed class ModelDataNullTypeMappingSource : ITypeMappingSource
    {
        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            IProperty property
        ) => null;

        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            IElementType elementType
        ) => null;

        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            MemberInfo member
        ) => null;

        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            MemberInfo member,
            IModel model,
            bool useAttributes
        ) => null;

        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            Type type
        ) => null;

        /// <inheritdoc />
        public CoreTypeMapping? FindMapping(
            Type type,
            IModel model,
            CoreTypeMapping? elementMapping = null
        ) => null;
    }
}
