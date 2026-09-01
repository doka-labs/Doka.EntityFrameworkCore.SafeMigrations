namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationScaffoldingTests
{
    [Fact]
    public void OptionsBuilderDefaultsToStrictFailClosedConfiguration()
    {
        var builder = new SafeMigrationOptionsBuilder();

        Assert.Equal(SafeMigrationScaffoldingMode.Strict, builder.Mode);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, builder.LegacyConvergencePolicy);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseScaffoldingMode((SafeMigrationScaffoldingMode)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseLegacyConvergencePolicy(SafeMigrationPolicy.ExistenceOnly));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseLegacyConvergencePolicy((SafeMigrationPolicy)int.MaxValue));
    }

    [Fact]
    public void RepairPolicyRequiresLegacyConvergenceRegardlessOfCallOrder()
    {
        var invalid = new SafeMigrationOptionsBuilder()
            .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);

        var valid = new SafeMigrationOptionsBuilder()
            .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)
            .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);

        var exception = Assert.Throws<InvalidOperationException>(invalid.Validate);
        valid.Validate();

        Assert.Contains("requires LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, valid.LegacyConvergencePolicy);
    }

    [Theory]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, "SafeMigrationPolicy.ThrowIfDifferent")]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, "SafeMigrationPolicy.RepairIfSafe")]
    public void LegacyConvergenceGenerationFreezesSelectedPolicyInSource(
        SafeMigrationPolicy policy,
        string expectedPolicy
    )
    {
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.LegacyConvergence,
            legacyConvergencePolicy: policy);

        var builder = new IndentedStringBuilder();

        generator.Generate("migrationBuilder", [CreateTable()], builder);
        var source = builder.ToString();

        Assert.Contains(".ConvergeTableFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(
            string.Concat(
                ",",
                Environment.NewLine,
                "    policy: global::Doka.EntityFrameworkCore.SafeMigrations.",
                expectedPolicy),
            source,
            StringComparison.Ordinal);
        Assert.EndsWith(");", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, ".CreateTableIfNotExists(")]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, ".ConvergeTableFromModel(")]
    public void CreateTableGenerationFreezesSelectedModeInSource(
        SafeMigrationScaffoldingMode mode,
        string expectedMethod
    )
    {
        var generator = CreateOperationGenerator(mode);
        var builder = new IndentedStringBuilder();

        generator.Generate("migrationBuilder", [CreateTable()], builder);

        Assert.Contains(expectedMethod, builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.CreateTable(", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledScaffoldingDelegatesToEfCoreUnchanged()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();

        generator.Generate("migrationBuilder", [CreateTable()], builder);

        Assert.Contains("migrationBuilder.CreateTable(", builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTableIfNotExists", builder.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, ".DropIndexIfExists(")]
    [InlineData(false, ".DropIndex(")]
    public void DropIndexGenerationFollowsTheSelectedScaffoldingContract(
        bool isEnabled,
        string expectedMethod
    )
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: isEnabled);
        var builder = new IndentedStringBuilder();
        var operation = new DropIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
        };

        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        Assert.Contains(expectedMethod, source, StringComparison.Ordinal);
        Assert.Equal(isEnabled, source.Contains(".DropIndexIfExists(", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateTableGenerationAcceptsStructurallyComparableCheckConstraints()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.CheckConstraints.Add(
            new AddCheckConstraintOperation
            {
                Name = "ck_users_id",
                Table = "users",
                Sql = "`id` >= 0",
            });

        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        Assert.Contains(".CreateTableIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("table.CheckConstraint(\"ck_users_id\", \"`id` >= 0\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTableGenerationRejectsOpaqueCheckConstraintBeforeEmittingSource()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.CheckConstraints.Add(
            new AddCheckConstraintOperation
            {
                Name = "ck_users_reference",
                Table = "users",
                Sql = "reference LIKE 'USR-%'",
            });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        Assert.DoesNotContain("CreateTable", builder.ToString(), StringComparison.Ordinal);
        Assert.Contains("ck_users_reference", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trailing_token", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FromExpression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexGenerationSelectsSingleAndCompositeSafeEntryPoints()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var single = new CreateIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
            Columns = ["email"],
        };

        var composite = new CreateIndexOperation
        {
            Name = "ix_users_tenant_email",
            Table = "users",
            Columns = ["tenant_id", "email"],
            IsDescending = [false, true],
        };

        generator.Generate("migrationBuilder", [single, composite], builder);
        var source = builder.ToString();

        Assert.Contains(".CreateIndexIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(".CreateCompositeIndexIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains("columns: [\"tenant_id\", \"email\"]", source, StringComparison.Ordinal);
        Assert.Contains("descending: [false, true]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.CreateIndex(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectedIndexPrefixesAreRenderedAndCapturedInKeyOrder()
    {
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            createIndexProjectors: [new TestIndexProjector([0, 64])]);
        var sourceBuilder = new IndentedStringBuilder();
        var operation = new CreateIndexOperation
        {
            Name = "ix_users_tenant_email",
            Table = "users",
            Columns = ["tenant_id", "email"],
        };

        generator.Generate("migrationBuilder", [operation], sourceBuilder);
        var source = sourceBuilder.ToString();

        Assert.Contains(".CreateCompositeIndexWithPrefixesIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(
            string.Concat(",", Environment.NewLine, "    prefixLengths: [0, 64]"),
            source,
            StringComparison.Ordinal);

        var migrationBuilder = new MigrationBuilder("test");
        _ = migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
            operation.Name,
            operation.Table,
            operation.Columns,
            [0, 64]);

        var safeOperation = Assert.IsType<SafeMigrationOperation>(Assert.Single(migrationBuilder.Operations));
        var intent = Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);

        Assert.Null(intent.Definition.Keys[0].PrefixLength);
        Assert.Equal(64, intent.Definition.Keys[1].PrefixLength);
        Assert.Empty(safeOperation.GetAnnotations());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0, 64)]
    public void SingleIndexPrefixProjectionRejectsInvalidShapes(
        params int[] prefixLengths
    )
    {
        var migrationBuilder = new MigrationBuilder("test");

        Assert.Throws<ArgumentException>(() =>
            migrationBuilder.CreateIndexWithPrefixesIfNotExistsFromModel(
                "ix_users_email",
                "users",
                "email",
                prefixLengths));
        Assert.Empty(migrationBuilder.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexPrefixProjectionRejectsMissingRequiredMetadata(
        bool composite
    )
    {
        var migrationBuilder = new MigrationBuilder("test");

        var exception = composite
            ? Assert.Throws<ArgumentNullException>(() =>
                migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
                    "ix_users_tenant_email",
                    "users",
                    ["tenant_id", "email"],
                    prefixLengths: null!))
            : Assert.Throws<ArgumentNullException>(() =>
                migrationBuilder.CreateIndexWithPrefixesIfNotExistsFromModel(
                    "ix_users_email",
                    "users",
                    "email",
                    prefixLengths: null!));

        Assert.Equal("prefixLengths", exception.ParamName);
        Assert.Empty(migrationBuilder.Operations);
    }

    [Fact]
    public void CompositeForeignKeyGenerationUsesAnalyzerCompatiblePrincipalColumns()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "tenant_id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            });
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "role_id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            });
        operation.ForeignKeys.Add(
            new AddForeignKeyOperation
            {
                Name = "fk_users_roles",
                Table = "users",
                Columns = ["tenant_id", "role_id"],
                PrincipalTable = "roles",
                PrincipalColumns = ["tenant_id", "id"],
            });

        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        Assert.Contains("principalColumns: [\"tenant_id\", \"id\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MigrationGenerationUsesAnalyzerCompatibleFileScopedNamespace(
        string newline
    )
    {
        var blockScopedSource = string.Concat(
            "#nullable disable",
            newline,
            newline,
            "namespace Company.Product.Migrations",
            newline,
            "{",
            newline,
            "    public partial class CreateUsers : Migration",
            newline,
            "    {",
            newline,
            "    }",
            newline,
            "}",
            newline);

        var source = SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(
            blockScopedSource,
            "Company.Product.Migrations");

        Assert.Contains("namespace Company.Product.Migrations;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"namespace Company.Product.Migrations{newline}{{",
            source,
            StringComparison.Ordinal);
        Assert.Contains("public partial class CreateUsers : Migration", source, StringComparison.Ordinal);
        Assert.Equal(newline, SafeMigrationGeneratedSource.GetConsistentNewLine(source));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MigrationGenerationAddsMissingSafeMigrationsNamespaceImportExactlyOnce(
        string newline
    )
    {
        var source = string.Concat(
            "using Doka.EntityFrameworkCore.MySql;",
            newline,
            "using Microsoft.EntityFrameworkCore.Migrations;",
            newline,
            newline,
            "#nullable disable",
            newline);

        var withImport = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(source);
        var repeated = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(withImport);

        Assert.Contains(
            string.Concat(
                "using Doka.EntityFrameworkCore.MySql;",
                newline,
                "using Doka.EntityFrameworkCore.SafeMigrations;",
                newline,
                "using Microsoft.EntityFrameworkCore.Migrations;"),
            withImport,
            StringComparison.Ordinal);
        Assert.Equal(withImport, repeated);

        const string directive = "using Doka.EntityFrameworkCore.SafeMigrations;";
        var directiveIndex = repeated.IndexOf(directive, StringComparison.Ordinal);

        Assert.True(directiveIndex >= 0);
        Assert.DoesNotContain(
            directive,
            repeated[(directiveIndex + directive.Length)..],
            StringComparison.Ordinal);
        Assert.Equal(newline, SafeMigrationGeneratedSource.GetConsistentNewLine(repeated));
    }

    [Theory]
    [InlineData("first\r\nsecond\n")]
    [InlineData("first\rsecond")]
    public void MigrationGenerationRejectsInconsistentLineEndings(
        string source
    )
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationGeneratedSource.GetConsistentNewLine(source));

        Assert.Contains("inconsistent line endings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGenerationRejectsSourceWithoutEfCoreNamespaceAnchor()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.EnsureSafeMigrationsUsingDirective(
                "#nullable disable"));

        Assert.Contains("namespace directive", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unresolved extension method", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGenerationRejectsDuplicateSafeMigrationsNamespaceDirective()
    {
        var newline = Environment.NewLine;
        var source = string.Join(
            newline,
            "using Doka.EntityFrameworkCore.SafeMigrations;",
            "using Doka.EntityFrameworkCore.SafeMigrations;",
            "using Microsoft.EntityFrameworkCore.Migrations;",
            string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.EnsureSafeMigrationsUsingDirective(source));

        Assert.Contains(
            "more than one SafeMigrations namespace directive",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGenerationDoesNotMistakeLiteralTextForUsingDirective()
    {
        var newline = Environment.NewLine;
        var source = string.Join(
            newline,
            "using Microsoft.EntityFrameworkCore.Migrations;",
            string.Empty,
            "var text = \"using Doka.EntityFrameworkCore.SafeMigrations;\";",
            string.Empty);

        var result = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(source);

        Assert.StartsWith(
            string.Concat(
                "using Doka.EntityFrameworkCore.SafeMigrations;",
                newline,
                "using Microsoft.EntityFrameworkCore.Migrations;"),
            result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesignTimeServicesDecorateProviderGeneratorWithoutRewritingProviderArtifacts()
    {
        var providerGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(ServiceDescriptor.Singleton<IMigrationsCodeGenerator>(providerGenerator));
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");
        var metadata = generator.GenerateMetadata(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "CreateUsers",
            "202608310001_CreateUsers",
            targetModel: null!);
        var snapshot = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        Assert.NotSame(providerGenerator, generator);
        Assert.Equal(providerGenerator.Language, generator.Language);
        Assert.Equal(providerGenerator.FileExtension, generator.FileExtension);
        Assert.Equal(TestMigrationsCodeGenerator.MetadataSource, metadata);
        Assert.Equal(TestMigrationsCodeGenerator.SnapshotSource, snapshot);
        Assert.Contains("using System;", metadata, StringComparison.Ordinal);
        Assert.Contains("new Guid(", metadata, StringComparison.Ordinal);
        Assert.Contains("using System;", snapshot, StringComparison.Ordinal);
        Assert.Contains("new Guid(", snapshot, StringComparison.Ordinal);
        Assert.Equal(1, providerGenerator.MetadataCallCount);
        Assert.Equal(1, providerGenerator.SnapshotCallCount);
    }

    [Fact]
    public void DesignTimeServicesDeferGeneratorDecorationUntilProviderServicesExist()
    {
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(
            ServiceDescriptor.Singleton<IMigrationsCodeGenerator, TestMigrationsCodeGenerator>());
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select(language: null);

        Assert.IsType<SafeMigrationCSharpMigrationsGenerator>(generator);
    }

    [Fact]
    public void DesignTimeServicesRejectMissingProviderGeneratorAtSelectionTime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);

        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IMigrationsCodeGeneratorSelector>();

        var exception = Assert.Throws<OperationException>(() => selector.Select("C#"));

        Assert.Contains("No SafeMigrations code generator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("C#", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignTimeServicesPreserveProviderLanguageWhenScaffoldingIsDisabled()
    {
        var csharpGenerator = new TestMigrationsCodeGenerator();
        var fsharpGenerator = new TestMigrationsCodeGenerator(language: "F#");
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(csharpGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(fsharpGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IMigrationsCodeGeneratorSelector>();
        var generator = selector.Select("F#");

        var source = generator.GenerateMigration(
            "Company.Migrations",
            "CreateUsers",
            upOperations: [],
            downOperations: []);

        Assert.Equal("F#", generator.Language);
        Assert.Equal(TestMigrationsCodeGenerator.MigrationSource, source);
        Assert.Equal(0, csharpGenerator.MigrationCallCount);
        Assert.Equal(1, fsharpGenerator.MigrationCallCount);
    }

    [Fact]
    public void EnabledCodeGeneratorSelectorRejectsUnsupportedLanguage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var selector = new SafeMigrationMigrationsCodeGeneratorSelector(
            [
                new TestMigrationsCodeGenerator(),
                new TestMigrationsCodeGenerator(language: "F#"),
            ],
            provider.GetRequiredService<ICSharpHelper>(),
            new SafeMigrationScaffoldingConfiguration(
                IsEnabled: true,
                Mode: SafeMigrationScaffoldingMode.Strict));

        var exception = Assert.Throws<OperationException>(() => selector.Select("F#"));

        Assert.Contains("No SafeMigrations code generator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("F#", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignTimeServicesActivateProviderImplementationTypeRegistration()
    {
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(
            ServiceDescriptor.Singleton<IMigrationsCodeGenerator, TestMigrationsCodeGenerator>());
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");
        var snapshot = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        Assert.IsType<SafeMigrationCSharpMigrationsGenerator>(generator);
        Assert.Equal(TestMigrationsCodeGenerator.SnapshotSource, snapshot);
    }

    [Fact]
    public void CodeGeneratorSelectorPreservesLastLanguageMatch()
    {
        var firstGenerator = new TestMigrationsCodeGenerator();
        var secondGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(firstGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(secondGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("c#");

        _ = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        Assert.Equal(0, firstGenerator.SnapshotCallCount);
        Assert.Equal(1, secondGenerator.SnapshotCallCount);
    }

    [Fact]
    public void CodeGeneratorSelectorPreservesLegacyGeneratorPrecedence()
    {
        var legacyGenerator = new TestMigrationsCodeGenerator(language: null);
        var csharpGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(legacyGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(csharpGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");

        _ = generator.GenerateMetadata(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "CreateUsers",
            "202608310001_CreateUsers",
            targetModel: null!);

        Assert.Equal(1, legacyGenerator.MetadataCallCount);
        Assert.Equal(0, csharpGenerator.MetadataCallCount);
    }

    [Fact]
    public void DesignTimeServicesAreIdempotentBeforeProviderRegistration()
    {
        var services = new ServiceCollection();
        var designTimeServices = new SafeMigrationDesignTimeServices();

        designTimeServices.ConfigureDesignTimeServices(services);
        designTimeServices.ConfigureDesignTimeServices(services);

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IMigrationsCodeGeneratorSelector));
    }

    [Fact]
    public void UnexpectedMigrationNamespaceShapeIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(
                "namespace Unexpected;",
                "Company.Product.Migrations"));

        Assert.Contains("unexpected namespace shape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRollbackGenerationFailsClosedInGeneratedSource()
    {
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.LegacyConvergence);
        var builder = new IndentedStringBuilder();

        generator.Generate("migrationBuilder", [new SafeMigrationLegacyRollbackOperation()], builder);

        Assert.StartsWith(
            "throw new global::System.NotSupportedException(",
            builder.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("cannot be rolled back safely", builder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TypedConvergenceExpandsIntoContainerColumnsAndConstraints()
    {
        var builder = new MigrationBuilder("test");

        builder.ConvergeTableFromModel(
            "users",
            table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", value => value.Id);
                table.UniqueConstraint("uq_users_email", value => value.Email);
            });

        Assert.Collection(
            builder.Operations,
            operation =>
            {
                var safe = Assert.IsType<SafeMigrationOperation>(operation);
                var intent = Assert.IsType<EnsureTableIntent>(safe.Intent);

                Assert.Equal(SafeMigrationTableMode.ConvergenceContainer, intent.Mode);
                Assert.Equal(SafeMigrationPolicy.ExistenceOnly, safe.Policy);
            },
            operation => Assert.IsType<EnsureColumnIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsureColumnIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsurePrimaryKeyIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsureUniqueConstraintIntent>(
                Assert.IsType<SafeMigrationOperation>(operation).Intent));
    }

    [Fact]
    public void ProviderColumnAnnotationsAreSnapshottedAndRestoredOnStandardOperations()
    {
        var prefixLengths = new[] { new[] { 12, 24 } };
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:ValueGenerationStrategy"] = TestValueGenerationStrategy.Identity;
        operation["Test:PrefixLengths"] = prefixLengths;

        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);
        prefixLengths[0][0] = 99;

        var standard = Assert.IsType<AddColumnOperation>(SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null));

        var restoredPrefixLengths = Assert.IsType<int[][]>(standard["Test:PrefixLengths"]);
        restoredPrefixLengths[0][0] = 48;

        var secondStandard = Assert.IsType<AddColumnOperation>(SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null));

        Assert.Equal(TestValueGenerationStrategy.Identity, standard["Test:ValueGenerationStrategy"]);
        Assert.Equal([48, 24], restoredPrefixLengths[0]);
        Assert.Equal([12, 24], Assert.IsType<int[][]>(secondStandard["Test:PrefixLengths"])[0]);
    }

    [Fact]
    public void MutableProviderAnnotationValue_IsRejectedDuringCapture()
    {
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Mutable"] = new List<int> { 1 };

        var exception = Assert.Throws<NotSupportedException>(() =>
            SafeMigrationExpectedDefinitionFactory.From(operation));

        Assert.Contains("cannot be captured immutably", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedProviderAnnotationValueKindsHaveDeterministicFingerprints()
    {
        object[] values =
        [
            "value",
            'v',
            true,
            (byte)1,
            (sbyte)-1,
            (short)-2,
            (ushort)2,
            -3,
            (uint)3,
            -4L,
            4UL,
            1.25m,
            1.5f,
            1.75d,
            new DateOnly(2026, 8, 27),
            new TimeOnly(12, 34, 56),
            new DateTime(2026, 8, 27, 12, 34, 56, DateTimeKind.Utc),
            new DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.FromHours(2)),
            TimeSpan.FromMinutes(5),
            Guid.Parse("1ea905b8-9114-46f1-811d-9db2ba394f31"),
            new byte[] { 1, 2, 3 },
            typeof(string),
            TestValueGenerationStrategy.Identity,
            new[] { 1, 2, 3 },
        ];

        foreach (var value in values)
        {
            var operation = new AddColumnOperation
            {
                Name = "id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            };

            operation["Test:Value"] = value;

            var first = Assert.Single(SafeMigrationProviderAnnotation.Capture(operation));
            var second = Assert.Single(SafeMigrationProviderAnnotation.Capture(operation));

            Assert.Equal(64, first.Fingerprint.Length);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
        }
    }

    [Fact]
    public void InvalidProviderAnnotationArrayShapesAreRejected()
    {
        var multidimensional = new int[1, 1];
        var nonZeroBased = Array.CreateInstance(typeof(int), [1], [1]);

        foreach (var value in new Array[] { multidimensional, nonZeroBased })
        {
            var operation = new AddColumnOperation
            {
                Name = "id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            };

            operation["Test:Array"] = value;

            var exception = Assert.Throws<NotSupportedException>(() =>
                SafeMigrationProviderAnnotation.Capture(operation));

            Assert.Contains("one-dimensional and zero-based", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NullProviderAnnotationSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationProviderAnnotation.Capture(null!));
    }

    private static SafeMigrationCSharpMigrationOperationGenerator CreateOperationGenerator(
        SafeMigrationScaffoldingMode mode,
        bool isEnabled = true,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent,
        IEnumerable<ISafeMigrationCreateIndexScaffoldingProjector>? createIndexProjectors = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var dependencies = provider.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>();

        return new SafeMigrationCSharpMigrationOperationGenerator(
            dependencies,
            new SafeMigrationScaffoldingConfiguration(isEnabled, mode, legacyConvergencePolicy),
            createIndexProjectors ?? []);
    }

    private static CreateTableOperation CreateTable()
    {
        var operation = new CreateTableOperation { Name = "users" };
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            });

        return operation;
    }

    private sealed class NullTypeMappingSource : ITypeMappingSource
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

    private sealed class TestMigrationsCodeGenerator(
        string? language = "C#"
    ) : IMigrationsCodeGenerator
    {
        public const string MigrationSource = "module Company.Migrations.CreateUsers";
        public const string MetadataSource = "using System;\npartial class CreateUsers "
            + "{ private readonly Guid _id = new Guid(\"1714e708-5197-44c4-b355-ad0f2bc6cc80\"); }";
        public const string SnapshotSource = "using System;\npartial class ReviewContextModelSnapshot "
            + "{ private readonly Guid _id = new Guid(\"1714e708-5197-44c4-b355-ad0f2bc6cc80\"); }";

        public string FileExtension => ".cs";

        public string? Language { get; } = language;

        public int MigrationCallCount { get; private set; }

        public int MetadataCallCount { get; private set; }

        public int SnapshotCallCount { get; private set; }

        public string GenerateMigration(
            string? migrationNamespace,
            string migrationName,
            IReadOnlyList<MigrationOperation> upOperations,
            IReadOnlyList<MigrationOperation> downOperations
        )
        {
            MigrationCallCount++;

            return MigrationSource;
        }

        public string GenerateMetadata(
            string? migrationNamespace,
            Type contextType,
            string migrationName,
            string migrationId,
            IModel targetModel
        )
        {
            MetadataCallCount++;

            return MetadataSource;
        }

        public string GenerateSnapshot(
            string? modelSnapshotNamespace,
            Type contextType,
            string modelSnapshotName,
            IModel model
        )
        {
            SnapshotCallCount++;

            return SnapshotSource;
        }
    }

    private enum TestValueGenerationStrategy
    {
        Identity = 1,
    }

    private sealed class TestIndexProjector(
        IReadOnlyList<int> prefixLengths
    ) : ISafeMigrationCreateIndexScaffoldingProjector
    {
        public SafeMigrationCreateIndexScaffoldingProjection Project(
            CreateIndexOperation operation
        ) => new(operation, prefixLengths);
    }
}
