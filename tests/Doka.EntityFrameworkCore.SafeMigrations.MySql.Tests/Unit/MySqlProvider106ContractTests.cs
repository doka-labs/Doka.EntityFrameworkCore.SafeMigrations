namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves published Doka 10.6 physical facets through SafeMigrations' authoring contracts.</summary>
public sealed class MySqlProvider106ContractTests
{
    /// <summary>Provider-derived lengths survive EF operation capture without invented index prefixes.</summary>
    /// <param name="table">The independently configured physical owner.</param>
    /// <param name="column">The convention-sized or explicitly configured column.</param>
    /// <param name="storeType">The expected physical mapping from the published provider.</param>
    /// <param name="maximumLength">The expected column-length facet.</param>
    [Theory]
    [InlineData("budget_strings", "left_value", "varchar(254)", 254)]
    [InlineData("budget_strings", "right_value", "varchar(254)", 254)]
    [InlineData("budget_binary", "payload", "varbinary(254)", 254)]
    [InlineData("budget_fixed", "code", "char(64)", 64)]
    [InlineData("budget_fixed", "payload", "varbinary(216)", 216)]
    [InlineData("budget_explicit", "code", "varchar(700)", 700)]
    [InlineData("budget_explicit", "value", "varchar(68)", 68)]
    [InlineData("budget_guid", "value", "varchar(164)", 164)]
    [InlineData("budget_overlap", "shared", "varchar(68)", 68)]
    [InlineData("budget_principals", "code", "varchar(168)", 168)]
    [InlineData("budget_dependents", "code", "varchar(168)", 168)]
    public void CompletePhysicalBudgetsSurviveSafeDefinitionCapture(
        string table,
        string column,
        string storeType,
        int maximumLength
    )
    {
        // Arrange
        using var context = CreateContext();
        var operations = CreateOperations(context);
        var create = Assert.Single(operations.OfType<CreateTableOperation>(), operation => operation.Name == table);
        var original = Assert.Single(create.Columns, operation => operation.Name == column);

        // Act
        var captured = SafeMigrationExpectedDefinitionFactory.From(create);
        var definition = Assert.Single(captured.Columns, value => value.Name == column);
        var indexes = operations.OfType<CreateIndexOperation>().Where(operation => operation.Table == table)
            .Select(operation => MySqlSafeMigrationProviderOperationAdapter.Instance.TryNormalize(operation,
                out var normalized) ? normalized : null).ToArray();

        // Assert
        Assert.Equal(storeType, original.ColumnType);
        Assert.Equal(storeType, definition.StoreType);
        Assert.Equal(maximumLength, original.MaxLength);
        Assert.Equal(maximumLength, definition.MaxLength);
        Assert.Equal(original.IsFixedLength, definition.IsFixedLength);
        Assert.Equal(8, operations.OfType<CreateIndexOperation>().Count());
        Assert.All(indexes, operation => Assert.All(
            Assert.IsType<EnsureIndexIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent).Definition.Keys,
            static key => Assert.Null(key.PrefixLength)));
    }

    /// <summary>Principal-derived lengths remain convention-owned on both endpoints after safe capture.</summary>
    [Fact]
    public void CompatibleForeignKeyLengthKeepsPrincipalIdentityAndConventionOwnership()
    {
        // Arrange
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var principal = model.FindEntityType("principal")!.FindProperty("code")!;
        var dependent = model.FindEntityType("dependent")!.FindProperty("code")!;

        // Act
        var operations = CreateOperations(context);
        var owner = Assert.Single(operations.OfType<CreateTableOperation>(),
            operation => operation.Name == "budget_dependents");

        var captured = SafeMigrationExpectedDefinitionFactory.From(owner);
        var foreignKey = Assert.Single(captured.ForeignKeys);

        // Assert
        Assert.Equal(168, principal.GetMaxLength());
        Assert.Equal(principal.GetMaxLength(), dependent.GetMaxLength());
        Assert.Equal(ConfigurationSource.Convention,
            ((IConventionProperty)principal).GetMaxLengthConfigurationSource());
        Assert.NotEqual(ConfigurationSource.Explicit,
            ((IConventionProperty)dependent).GetMaxLengthConfigurationSource());
        Assert.Equal("budget_principals", foreignKey.PrincipalTable);
        Assert.Equal(["code", "scope"], foreignKey.Columns);
        Assert.Equal(["code", "scope"], foreignKey.PrincipalColumns);
        Assert.Equal(168, Assert.Single(captured.Columns, column => column.Name == "code").MaxLength);
    }

    /// <summary>The budget convention must not narrow unrelated unindexed text or binary payloads.</summary>
    [Fact]
    public void UnindexedPayloadsRemainUnboundedAfterSafeCapture()
    {
        // Arrange
        using var context = CreateContext();
        var original = Assert.Single(CreateOperations(context).OfType<CreateTableOperation>(),
            operation => operation.Name == "budget_unindexed");

        // Act
        var captured = SafeMigrationExpectedDefinitionFactory.From(original);

        // Assert
        var text = Assert.Single(captured.Columns, column => column.Name == "text");
        var payload = Assert.Single(captured.Columns, column => column.Name == "payload");
        Assert.Equal("longtext", text.StoreType);
        Assert.Equal("longblob", payload.StoreType);
        Assert.Null(text.MaxLength);
        Assert.Null(payload.MaxLength);
    }

    /// <summary>Actual provider and SafeMigrations design services publish the same bounded model facets.</summary>
    [Fact]
    public void MigrationAndSnapshotGeneratorsRetainCompleteBudgetFacets()
    {
        // Arrange
        using var context = CreateContext();
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(context);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);
        new MySqlSafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        // WHY: EF's standalone design-time provider resolves the captured context's scoped registrations
        // through its root, matching the actual tooling builder rather than an application-host scope.
        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<
            Microsoft.EntityFrameworkCore.Migrations.Design.IMigrationsCodeGeneratorSelector>();

        var generator = selector.Select("C#");
        var operations = CreateOperations(context);

        // Act
        var migration = generator.GenerateMigration("Contracts", "Provider106", operations, []);
        var snapshot = generator.GenerateSnapshot("Contracts", typeof(BudgetContext), "Provider106Snapshot",
            context.GetService<IDesignTimeModel>().Model);

        // Assert
        Assert.Contains("CreateTableIfNotExists(", migration, StringComparison.Ordinal);
        Assert.Contains("CreateCompositeIndexIfNotExistsFromModel(", migration, StringComparison.Ordinal);
        Assert.Contains("type: \"varbinary(254)\"", migration, StringComparison.Ordinal);
        Assert.Contains("type: \"varchar(68)\"", migration, StringComparison.Ordinal);
        Assert.Contains("fixedLength: true", migration, StringComparison.Ordinal);
        Assert.Contains(".HasColumnType(\"varbinary(254)\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(".HasColumnType(\"varchar(68)\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(".HasMaxLength(168)", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("prefixLengths:", migration, StringComparison.Ordinal);
    }

    /// <summary>An impossible explicit physical key cannot be silently narrowed before safe scaffolding.</summary>
    [Fact]
    public void ExplicitOversizedKeyFailsBeforeSafeOperationPublication()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ImpossibleBudgetContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Database=provider_106",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .EnableServiceProviderCaching(false).Options;

        using var context = new ImpossibleBudgetContext(options);

        // Act
        var failure = Record.Exception(() => CreateOperations(context));

        // Assert
        Assert.Contains("3072", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Seven-digit decimal remainders consume four bytes in projected and live physical budgets.</summary>
    /// <param name="precision">The declared decimal precision.</param>
    /// <param name="scale">The declared decimal scale.</param>
    /// <param name="width">The independently calculated complete decimal width.</param>
    /// <param name="kind">Zero for index, one for primary key, or two for unique constraint.</param>
    /// <param name="overLimit">Whether the complete key exceeds its physical limit by exactly one byte.</param>
    /// <param name="projected">Whether widths originate in accepted definitions instead of native facets.</param>
    [Theory]
    [MemberData(nameof(DecimalBoundaryCases))]
    public void DecimalRemaindersRespectExactAndOneByteOverPhysicalBoundaries(
        int precision,
        int scale,
        int width,
        int kind,
        bool overLimit,
        bool projected
    )
    {
        // Arrange
        using var context = CreateContext();
        const int limit = 3072;
        var binaryLength = limit - width + (overLimit ? 1 : 0);
        var decimalColumn = new ExpectedColumnDefinition("amount", typeof(decimal), false,
            $"decimal({precision},{scale})", precision: precision, scale: scale);

        var binaryColumn = new ExpectedColumnDefinition("payload", typeof(byte[]), false,
            $"varbinary({binaryLength})", maxLength: binaryLength);

        var columns = new ProjectedColumns(projected ? [decimalColumn, binaryColumn] : []);
        var environment = new SafeMigrationIndexPhysicalEnvironment(limit,
            Columns: new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal)
            {
                ["amount"] = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
                    "decimal", null, null, precision, scale, null),
                ["payload"] = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
                    "varbinary", binaryLength, binaryLength, null, null, null),
            });

        var mappings = context.GetService<IRelationalTypeMappingSource>();

        // Act
        var result = kind switch
        {
            0 => MySqlProjectedIndexPhysicalShape.Validate(new ExpectedIndexDefinition("ix_budget", "budget",
                [new ExpectedIndexKeyDefinition("amount"), new ExpectedIndexKeyDefinition("payload")]),
                columns, environment, mappings),
            1 => MySqlProjectedIndexPhysicalShape.Validate(
                new ExpectedPrimaryKeyDefinition("pk_budget", "budget", ["amount", "payload"]),
                columns, environment, mappings),
            2 => MySqlProjectedIndexPhysicalShape.Validate(
                new ExpectedUniqueConstraintDefinition("uq_budget", "budget", ["amount", "payload"]),
                columns, environment, mappings),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Assert
        Assert.Equal((long)width, environment.Columns!["amount"].FullWidthBytes);
        Assert.Equal(overLimit
            ? kind == 0 ? MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix
                : MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit
            : MySqlProjectedIndexPhysicalShapeResult.Feasible, result);
    }

    /// <summary>Enumerates both remainder positions, every key kind and both metadata origins.</summary>
    public static IEnumerable<object[]> DecimalBoundaryCases()
    {
        foreach (var (precision, scale, width) in new[] { (18, 2, 9), (9, 7, 5) })
        {
            foreach (var kind in new[] { 0, 1, 2 })
            {
                foreach (var overLimit in new[] { false, true })
                {
                    foreach (var projected in new[] { false, true })
                    {
                        yield return [precision, scale, width, kind, overLimit, projected];
                    }
                }
            }
        }
    }

    /// <summary>Builds published-provider options without opening a database or retaining variant caches.</summary>
    internal static DbContext CreateContext(string? connectionString = null, MySqlServerVersion? version = null)
    {
        var options = new DbContextOptionsBuilder<BudgetContext>()
            .UseMySql(connectionString ?? "Server=127.0.0.1;Port=1;User ID=test;Database=provider_106",
                version ?? MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .EnableServiceProviderCaching(false);

        options.UseMySqlSafeMigrations();

        return new BudgetContext(options.Options);
    }

    /// <summary>Captures exactly the finalized model that SafeMigrations' design-time pipeline consumes.</summary>
    internal static IReadOnlyList<MigrationOperation> CreateOperations(DbContext context) =>
        context.GetService<IMigrationsModelDiffer>().GetDifferences(null,
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

    /// <summary>Combines implicit, explicit, fixed, overlapping and principal-derived facets.</summary>
    private sealed class BudgetContext(
        DbContextOptions<BudgetContext> options
    ) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasCharSet("utf8mb4").UseCollation("utf8mb4_unicode_ci");
            var strings = Owner(modelBuilder, "strings");
            strings.Property<string>("left_value").IsRequired();
            strings.Property<string>("right_value").IsRequired();
            strings.Property<string>("third_value").IsRequired();
            strings.Property<byte[]>("reserved").HasMaxLength(17).IsRequired();
            strings.HasIndex("left_value", "right_value", "third_value", "reserved");

            var binary = Owner(modelBuilder, "binary");
            binary.Property<byte[]>("payload").IsRequired();
            binary.Property<byte[]>("reserved").HasMaxLength(2818).IsRequired();
            binary.HasIndex("payload", "reserved");

            var fixedOwner = Owner(modelBuilder, "fixed");
            fixedOwner.Property<string>("code").HasMaxLength(64).IsFixedLength().IsRequired();
            fixedOwner.Property<byte[]>("payload").IsRequired();
            fixedOwner.Property<byte[]>("reserved").HasMaxLength(2600).IsRequired();
            fixedOwner.HasIndex("code", "payload", "reserved");

            var explicitOwner = Owner(modelBuilder, "explicit");
            explicitOwner.Property<string>("code").HasMaxLength(700).IsRequired();
            explicitOwner.Property<string>("value").IsRequired();
            explicitOwner.HasIndex("code", "value");

            var guid = Owner(modelBuilder, "guid");
            guid.Property<Guid>("guid").HasMySqlGuidFormat(DokaMySqlGuidFormat.Binary16);
            guid.Property<string>("value").IsRequired();
            guid.Property<string>("reserved").HasMaxLength(600).IsRequired();
            guid.HasIndex("guid", "value", "reserved");

            var overlap = Owner(modelBuilder, "overlap");
            overlap.Property<string>("left_value").IsRequired();
            overlap.Property<string>("shared").IsRequired();
            overlap.Property<string>("explicit_value").HasMaxLength(700).IsRequired();
            overlap.HasIndex("left_value", "shared");
            overlap.HasIndex("shared", "explicit_value");

            var principal = modelBuilder.Entity("principal");
            principal.ToTable("budget_principals");
            principal.Property<string>("code").IsRequired();
            principal.Property<string>("scope").HasMaxLength(600).IsRequired();
            principal.HasKey("code", "scope");
            var dependent = Owner(modelBuilder, "dependents", "dependent");
            dependent.Property<string>("code").IsRequired();
            dependent.Property<string>("scope").IsRequired();
            dependent.HasOne("principal", null).WithMany().HasForeignKey("code", "scope");

            var unindexed = Owner(modelBuilder, "unindexed");
            unindexed.Property<string>("text").IsRequired();
            unindexed.Property<byte[]>("payload").IsRequired();
        }
    }

    /// <summary>Provides an independent explicit overflow that no convention may repair.</summary>
    private sealed class ImpossibleBudgetContext(DbContextOptions<ImpossibleBudgetContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasCharSet("utf8mb4");
            var owner = Owner(modelBuilder, "impossible");
            owner.Property<string>("value").HasMaxLength(769).IsRequired();
            owner.HasIndex("value");
        }
    }

    /// <summary>Creates one physical owner with a deterministic non-generated integer identity.</summary>
    private static EntityTypeBuilder Owner(ModelBuilder modelBuilder, string suffix, string? name = null)
    {
        var owner = modelBuilder.Entity(name ?? suffix);
        owner.ToTable("budget_" + suffix);
        owner.Property<int>("id").ValueGeneratedNever();
        owner.HasKey("id");

        return owner;
    }

    /// <summary>Separates accepted projected definitions from independently captured native metadata.</summary>
    private sealed class ProjectedColumns(ExpectedColumnDefinition[] definitions) : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = definitions.FirstOrDefault(value => value.Name == column);

            return definition is not null;
        }
    }
}
