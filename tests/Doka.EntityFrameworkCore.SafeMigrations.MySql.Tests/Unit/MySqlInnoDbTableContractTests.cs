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

    /// <summary>Existing typed callbacks preserve the operation prefix and an independent engine snapshot.</summary>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedCallbackCapturesAndReconstructsAnIndependentEngineSnapshot(
        bool convergence
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        var prefix = new SqlOperation { Sql = "SELECT 42" };
        builder.Operations.Add(prefix);
        CreateTableOperation? captured = null;

        // Act
        AppendTable(builder,
            convergence,
            SafeMigrationPolicy.ThrowIfDifferent,
            configure: nativeTable => captured = nativeTable);

        var operation = builder
            .Operations
            .OfType<SafeMigrationOperation>()
            .Single(candidate => candidate.Intent is EnsureTableIntent);

        captured!["Doka:MySql:StorageEngine"] = "MyISAM";
        var reconstructed = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(operation.Intent);

        // Assert
        Assert.Same(prefix, builder.Operations[0]);
        Assert.Equal(convergence
                ? 3
                : 2,
            builder.Operations.Count);

        Assert.All(builder.Operations.Skip(1), static added => Assert.IsType<SafeMigrationOperation>(added));
        Assert.Equal("InnoDB", reconstructed["Doka:MySql:StorageEngine"]);
        Assert.Empty(operation.GetAnnotations());
    }

    /// <summary>
    /// A throwing typed callback removes its own appended operations and retains the original failure.
    /// </summary>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    /// <param name="callback">The callback that appends an operation before throwing.</param>
    [Theory]
    [InlineData(false, "columns")]
    [InlineData(false, "constraints")]
    [InlineData(true, "columns")]
    [InlineData(true, "constraints")]
    public void ThrowingTypedCallbackRemovesOwnedOperationsAndPreservesPrefix(
        bool convergence,
        string callback
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        var prefix = new SqlOperation { Sql = "SELECT 42" };
        builder.Operations.Add(prefix);
        var failure = new InvalidOperationException("Expected callback failure.");

        // Act
        var exception = Record.Exception(() => AppendTable(builder,
            convergence,
            SafeMigrationPolicy.ThrowIfDifferent,
            beforeColumns: callback == "columns"
                ? AppendAndThrow
                : null,
            configure: callback == "constraints"
                ? _ => AppendAndThrow()
                : null));

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(prefix, Assert.Single(builder.Operations));

        void AppendAndThrow()
        {
            builder.Sql("SELECT 1");

            throw failure;
        }
    }

    /// <summary>Unexpected callback additions cannot escape rejection as ordinary unguarded operations.</summary>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    /// <param name="callback">The callback that appends the unexpected operation.</param>
    [Theory]
    [InlineData(false, "columns")]
    [InlineData(false, "constraints")]
    [InlineData(true, "columns")]
    [InlineData(true, "constraints")]
    public void ExtraTypedCallbackOperationsAreRejectedWithoutRemovingPrefix(
        bool convergence,
        string callback
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        var prefix = new SqlOperation { Sql = "SELECT 42" };
        builder.Operations.Add(prefix);

        // Act
        var exception = Record.Exception(() => AppendTable(builder,
            convergence,
            SafeMigrationPolicy.ThrowIfDifferent,
            beforeColumns: callback == "columns"
                ? AppendUnexpectedOperation
                : null,
            configure: callback == "constraints"
                ? _ => AppendUnexpectedOperation()
                : null));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Same(prefix, Assert.Single(builder.Operations));

        void AppendUnexpectedOperation() => builder.Sql("SELECT 1");
    }

    /// <summary>Immutable-capture rejection never leaves a native table operation behind the existing prefix.</summary>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedTypedSnapshotPreservesOnlyExistingOperations(
        bool convergence
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        var prefix = new SqlOperation { Sql = "SELECT 42" };
        builder.Operations.Add(prefix);

        // Act
        var exception = Record.Exception(() => AppendTable(builder,
            convergence,
            SafeMigrationPolicy.ThrowIfDifferent,
            configure: operation => operation.AddAnnotation("Unknown:Mutable", new List<int> { 1 })));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Same(prefix, Assert.Single(builder.Operations));
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
        var exception = Record.Exception(() => AppendTable(builder,
            convergence,
            SafeMigrationPolicy.ThrowIfDifferent,
            configure: ConfigureConstraint));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(builder.Operations);

        void ConfigureConstraint(
            CreateTableOperation operation
        )
        {
            MigrationOperation constraint;
            switch (kind)
            {
                case "primary":
                    var primary = new AddPrimaryKeyOperation
                    {
                        Name = "PK_items",
                        Table = operation.Name,
                        Columns = ["Id"],
                    };

                    operation.PrimaryKey = primary;
                    constraint = primary;
                    break;
                case "unique":
                    var unique = new AddUniqueConstraintOperation
                    {
                        Name = "UQ_items",
                        Table = operation.Name,
                        Columns = ["Id"],
                    };

                    operation.UniqueConstraints.Add(unique);
                    constraint = unique;
                    break;
                case "check":
                    var check = new AddCheckConstraintOperation
                    {
                        Name = "CK_items",
                        Table = operation.Name,
                        Sql = "Id >= 0",
                    };

                    operation.CheckConstraints.Add(check);
                    constraint = check;
                    break;
                case "foreign":
                    var foreign = new AddForeignKeyOperation
                    {
                        Name = "FK_items",
                        Table = operation.Name,
                        Columns = ["Id"],
                        PrincipalTable = "parents",
                        PrincipalColumns = ["Id"],
                    };

                    operation.ForeignKeys.Add(foreign);
                    constraint = foreign;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            constraint.AddAnnotation("Unknown:ConstraintOption", true);
        }
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
        Assert.Equal(plainHash,
            SafeMigrationContractFingerprint.Create(CreateBuilder(annotated: false)
                .Operations));
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
        AppendTable(builder,
            convergence,
            convergence
                ? SafeMigrationPolicy.RepairIfSafe
                : SafeMigrationPolicy.ExistenceOnly);

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model, options);

        var sql = string.Join("\n", commands.Select(static command => command.CommandText));

        // Assert
        Assert.Contains("engine_table.ENGINE", sql, StringComparison.Ordinal);
        Assert.Contains("THEN 'unsupported'", sql, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("ENGINE = InnoDB")), sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE `items` ENGINE", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A late safe-wrapper annotation cannot masquerade as captured table metadata or emit native creation.
    /// </summary>
    /// <param name="convergence">Whether the existing typed API emits granular convergence operations.</param>
    /// <param name="options">The runtime command or generated-script render path.</param>
    [Theory]
    [InlineData(false, MigrationsSqlGenerationOptions.Default)]
    [InlineData(true, MigrationsSqlGenerationOptions.Default)]
    [InlineData(false, MigrationsSqlGenerationOptions.Script)]
    [InlineData(true, MigrationsSqlGenerationOptions.Script)]
    public void LateEngineAnnotationRemainsUnsupportedWithoutNativeCreate(
        bool convergence,
        MigrationsSqlGenerationOptions options
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var returned = convergence
            ? builder.ConvergeTableFromModel("items",
                table => new { Id = table.Column<int>(type: "int") },
                policy: SafeMigrationPolicy.ThrowIfDifferent)
            : builder.CreateTableIfNotExists("items", table => new { Id = table.Column<int>(type: "int") });

        returned.Annotation("Doka:MySql:StorageEngine", "InnoDB");
        var tableOperation = builder
            .Operations
            .OfType<SafeMigrationOperation>()
            .Single(candidate => candidate.Intent is EnsureTableIntent);

        var definition = ((EnsureTableIntent)tableOperation.Intent).Definition;

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([tableOperation], context.Model, options);

        var sql = string.Join("\n", commands.Select(static command => command.CommandText));

        // Assert
        Assert.Equal("InnoDB", tableOperation["Doka:MySql:StorageEngine"]);
        Assert.Empty(definition.ProviderAnnotations);
        Assert.Contains("unsupported", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENGINE = InnoDB", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("CREATE TABLE ")),
            sql,
            StringComparison.Ordinal);

        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("ENGINE = InnoDB")),
            sql,
            StringComparison.Ordinal);
    }

    /// <summary>Unknown provider metadata remains unsupported through the existing typed table callback.</summary>
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
        builder.CreateTableIfNotExists("items",
            table => new { Id = table.Column<int>(type: "int") },
            constraints: table =>
            {
                if (includeEngine)
                {
                    table.Annotation("Doka:MySql:StorageEngine", "InnoDB");
                }

                table.Annotation("Unknown:TableOption", "value");
            });

        // Act
        var sql = string.Join("\n",
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate(builder.Operations, context.Model)
                .Select(static command => command.CommandText));

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
            AppendTable(builder, convergence: false, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.CreateTableIfNotExists("items", table => new { Id = table.Column<int>(type: "int") });
        }

        return builder;
    }

    /// <summary>Exercises only the existing typed public APIs while placing metadata before snapshot capture.</summary>
    /// <param name="builder">The migration builder whose existing operation prefix remains owned by the caller.</param>
    /// <param name="convergence">Whether to use the granular convergence API.</param>
    /// <param name="policy">The unchanged authored conflict policy.</param>
    /// <param name="beforeColumns">Optional work executed by the existing typed columns callback.</param>
    /// <param name="configure">Optional native-table metadata applied before immutable capture.</param>
    private static void AppendTable(
        MigrationBuilder builder,
        bool convergence,
        SafeMigrationPolicy policy,
        Action? beforeColumns = null,
        Action<CreateTableOperation>? configure = null
    )
    {
        if (convergence)
        {
            builder.ConvergeTableFromModel("items",
                table =>
                {
                    beforeColumns?.Invoke();

                    return new { Id = table.Column<int>(type: "int") };
                },
                constraints: table =>
                {
                    table.Annotation("Doka:MySql:StorageEngine", "InnoDB");
                    configure?.Invoke(table.GetInfrastructure());
                },
                policy: policy);

            return;
        }

        builder.CreateTableIfNotExists("items",
            table =>
            {
                beforeColumns?.Invoke();

                return new { Id = table.Column<int>(type: "int") };
            },
            constraints: table =>
            {
                table.Annotation("Doka:MySql:StorageEngine", "InnoDB");
                configure?.Invoke(table.GetInfrastructure());
            },
            policy: policy);
    }

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
