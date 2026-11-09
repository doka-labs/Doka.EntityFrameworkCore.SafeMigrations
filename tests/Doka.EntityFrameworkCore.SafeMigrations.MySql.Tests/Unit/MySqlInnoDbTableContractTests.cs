namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects immutable, complete engine contracts independently of any database server.</summary>
public sealed class MySqlInnoDbTableContractTests : IDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Owns runtime services without adding a profile to EF's process-wide automatic cache.</summary>
    public MySqlInnoDbTableContractTests()
    {
        _services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();
    }

    /// <summary>Disposes owned runtime services after the test's contexts have completed.</summary>
    public void Dispose() => _services.Dispose();

    /// <summary>Capture happens after annotations and does not observe later EF-operation mutation.</summary>
    [Fact]
    public void FactoryCapturesAndReconstructsAnIndependentEngineSnapshot()
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        CreateTableOperation? captured = null;

        // Act
        builder.CreateTableIfNotExistsFromModel(operationFactory: operationBuilder =>
        {
            operationBuilder.CreateTable("items", table => new { Id = table.Column<int>(type: "int") })
                .Annotation("Doka:MySql:StorageEngine", "InnoDB");
            captured = (CreateTableOperation)operationBuilder.Operations.Single();
        });

        var operation = (SafeMigrationOperation)builder.Operations.Single();
        captured!["Doka:MySql:StorageEngine"] = "MyISAM";
        var reconstructed = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(operation.Intent);

        // Assert
        Assert.Equal("InnoDB", reconstructed["Doka:MySql:StorageEngine"]);
        Assert.Empty(operation.GetAnnotations());
    }

    /// <summary>A rejected multi-operation factory cannot leave ordinary unguarded operations behind.</summary>
    [Fact]
    public void InvalidFactoryRollsBackEveryAppendedOperation()
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");

        // Act
        var exception = Record.Exception(() => builder.CreateTableIfNotExistsFromModel(operationBuilder =>
        {
            operationBuilder.CreateTable("first", table => new { Id = table.Column<int>(type: "int") });
            operationBuilder.Sql("SELECT 1");
        }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Nested provider annotations cannot disappear while the outer engine metadata is captured.</summary>
    /// <param name="kind">The nested constraint family.</param>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    [Theory]
    [InlineData("primary", false)]
    [InlineData("unique", false)]
    [InlineData("check", false)]
    [InlineData("foreign", false)]
    [InlineData("primary", true)]
    [InlineData("unique", true)]
    [InlineData("check", true)]
    [InlineData("foreign", true)]
    public void AnnotatedConstraintCannotBeSilentlyDiscarded(
        string kind,
        bool convergence
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");

        // Act
        var exception = Record.Exception(() =>
        {
            if (convergence)
            {
                builder.ConvergeTableFromModel(AppendAnnotatedTable, SafeMigrationPolicy.ThrowIfDifferent);
            }
            else
            {
                builder.CreateTableIfNotExistsFromModel(AppendAnnotatedTable);
            }
        });

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(builder.Operations);

        void AppendAnnotatedTable(
            MigrationBuilder operationBuilder
        ) => operationBuilder.CreateTable("items", table => new { Id = table.Column<int>(type: "int") },
                constraints: table =>
                {
                    switch (kind)
                    {
                        case "primary":
                            table.PrimaryKey("PK_items", value => value.Id)
                                .Annotation("Unknown:ConstraintOption", true);
                            break;
                        case "unique":
                            table.UniqueConstraint("UQ_items", value => value.Id)
                                .Annotation("Unknown:ConstraintOption", true);
                            break;
                        case "check":
                            table.CheckConstraint("CK_items", "Id >= 0")
                                .Annotation("Unknown:ConstraintOption", true);
                            break;
                        case "foreign":
                            table.ForeignKey("FK_items", value => value.Id, "parents", "Id")
                                .Annotation("Unknown:ConstraintOption", true);
                            break;
                    }
                })
            .Annotation("Doka:MySql:StorageEngine", "InnoDB");
    }

    /// <summary>Provider metadata changes fingerprints and complete table equivalence, not empty contracts.</summary>
    [Fact]
    public void ExplicitEngineParticipatesInTheCompleteContract()
    {
        // Arrange
        var plain = CreateBuilder(annotated: false);
        var explicitEngine = CreateBuilder(annotated: true);
        var plainTable = ((EnsureTableIntent)((SafeMigrationOperation)plain.Operations[0]).Intent).Definition;
        var engineTable = ((EnsureTableIntent)((SafeMigrationOperation)explicitEngine.Operations[0]).Intent).Definition;

        // Act
        var plainHash = SafeMigrationContractFingerprint.Create(plain.Operations);
        var engineHash = SafeMigrationContractFingerprint.Create(explicitEngine.Operations);
        var equivalent = SafeMigrationDefinitionEquivalence.Table(plainTable, engineTable);

        // Assert
        Assert.NotEqual(plainHash, engineHash);
        Assert.False(equivalent);
        Assert.Equal(plainHash, SafeMigrationContractFingerprint.Create(CreateBuilder(annotated: false).Operations));
    }

    /// <summary>Both strict and convergence DDL retain engine creation and a mandatory catalog guard.</summary>
    /// <param name="convergence">Whether object-granular convergence is requested.</param>
    /// <param name="options">The runtime command or generated-script render path.</param>
    [Theory]
    [InlineData(false, MigrationsSqlGenerationOptions.Default)]
    [InlineData(true, MigrationsSqlGenerationOptions.Default)]
    [InlineData(false, MigrationsSqlGenerationOptions.Script)]
    [InlineData(true, MigrationsSqlGenerationOptions.Script)]
    public void RuntimeSqlPreservesEngineAndMandatoryCatalogCheck(
        bool convergence,
        MigrationsSqlGenerationOptions options
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (convergence)
        {
            builder.ConvergeTableFromModel(AppendTable, SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.CreateTableIfNotExistsFromModel(AppendTable, SafeMigrationPolicy.ExistenceOnly);
        }

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model, options);
        var sql = string.Join("\n", commands.Select(static command => command.CommandText));

        // Assert
        Assert.Contains("engine_table.ENGINE", sql, StringComparison.Ordinal);
        Assert.Contains("THEN 'unsupported'", sql, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("ENGINE = InnoDB")), sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE `items` ENGINE", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unknown provider metadata remains unsupported even when authored through the public factory.</summary>
    /// <param name="includeEngine">Whether a valid engine annotation accompanies the unknown metadata.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCapturedAnnotationCannotReachNativeCreate(
        bool includeEngine
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExistsFromModel(operationBuilder =>
        {
            var operation = operationBuilder.CreateTable("items", table => new { Id = table.Column<int>(type: "int") });
            if (includeEngine)
            {
                operation.Annotation("Doka:MySql:StorageEngine", "InnoDB");
            }

            operation.Annotation("Unknown:TableOption", "value");
        });

        // Act
        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model).Select(static command => command.CommandText));

        // Assert
        Assert.Contains("unsupported", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE `items`", sql, StringComparison.Ordinal);
    }

    private static MigrationBuilder CreateBuilder(
        bool annotated
    )
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        if (annotated)
        {
            builder.CreateTableIfNotExistsFromModel(AppendTable);
        }
        else
        {
            builder.CreateTableIfNotExists("items", table => new { Id = table.Column<int>(type: "int") });
        }

        return builder;
    }

    private static void AppendTable(
        MigrationBuilder builder
    ) => builder.CreateTable("items", table => new { Id = table.Column<int>(type: "int") })
        .Annotation("Doka:MySql:StorageEngine", "InnoDB");

    private DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseInternalServiceProvider(_services);
        options.UseMySql("Server=127.0.0.1;Port=1;User ID=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 13)));
        options.UseMySqlSafeMigrations();

        return new DbContext(options.Options);
    }
}
