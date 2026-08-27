namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationScaffoldingTests
{
    [Fact]
    public void OptionsBuilderDefaultsToStrictAndRejectsUndefinedModes()
    {
        var builder = new SafeMigrationOptionsBuilder();

        Assert.Equal(SafeMigrationScaffoldingMode.Strict, builder.Mode);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseScaffoldingMode((SafeMigrationScaffoldingMode)int.MaxValue));
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

    [Fact]
    public void MigrationGenerationUsesAnalyzerCompatibleFileScopedNamespace()
    {
        var newline = Environment.NewLine;
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
        bool isEnabled = true
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var dependencies = provider.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>();

        return new SafeMigrationCSharpMigrationOperationGenerator(
            dependencies,
            new SafeMigrationScaffoldingConfiguration(isEnabled, mode));
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

    private enum TestValueGenerationStrategy
    {
        Identity = 1,
    }
}
