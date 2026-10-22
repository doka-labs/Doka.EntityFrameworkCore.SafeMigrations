namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Gets the ordinary EF operations delegated by disabled scaffolding.</summary>
    public static TheoryData<MigrationOperation> DisabledUnsupportedOperations => new()
    {
        new AlterTableOperation { Name = "items" },
        new CreateSequenceOperation { Name = "item_ids", ClrType = typeof(long) },
        new AlterSequenceOperation { Name = "item_ids" },
        new DropSequenceOperation { Name = "item_ids" },
        new RenameSequenceOperation { Name = "item_ids", NewName = "product_ids" },
        new RestartSequenceOperation { Name = "item_ids", StartValue = 42 },
        new SqlOperation { Sql = "SELECT 1;" },
    };

    /// <summary>Gets the standard operations that enabled safe scaffolding must reject.</summary>
    public static TheoryData<MigrationOperation> UnsupportedScaffoldingOperations => new()
    {
        new AlterDatabaseOperation(),
        new AlterTableOperation { Name = "items" },
        new CreateSequenceOperation { Name = "item_ids" },
        new AlterSequenceOperation { Name = "item_ids" },
        new DropSequenceOperation { Name = "item_ids" },
        new RenameSequenceOperation { Name = "item_ids", NewName = "product_ids" },
        new RestartSequenceOperation { Name = "item_ids", StartValue = 42 },
        new SqlOperation { Sql = "SELECT 1;" },
    };

    /// <summary>Creates the operation generator with explicit mode, adapter and index-projector ownership.</summary>
    private static SafeMigrationCSharpMigrationOperationGenerator CreateOperationGenerator(
        SafeMigrationScaffoldingMode mode,
        bool isEnabled = true,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent,
        IEnumerable<ISafeMigrationCreateIndexScaffoldingProjector>? createIndexProjectors = null,
        IEnumerable<ISafeMigrationProviderOperationAdapter>? providerOperationAdapters = null
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
            createIndexProjectors ?? [],
            providerOperationAdapters ?? []);
    }

    private sealed class TestProviderOperationAdapter : ISafeMigrationProviderOperationAdapter
    {
        public bool TryNormalize(
            MigrationOperation operation,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out SafeMigrationOperation? safeOperation
        )
        {
            safeOperation = operation switch
            {
                CreateIndexOperation index when index["Test:ProviderMetadata"] is not null =>
                    new SafeMigrationOperation(
                        new EnsureIndexIntent(SafeMigrationExpectedDefinitionFactory.From(index)),
                        SafeMigrationPolicy.ThrowIfDifferent),
                DropColumnOperation column when column["Test:ProviderMetadata"] is not null =>
                    new SafeMigrationOperation(
                        new DropColumnIntent(column.Name, column.Table, column.Schema),
                        SafeMigrationPolicy.ThrowIfDifferent),
                _ => null,
            };

            return safeOperation is not null;
        }

        public bool IsCertifiedPassthrough(
            MigrationOperation operation
        ) => operation is AlterDatabaseOperation;
    }

    private sealed class UnknownMigrationOperation : MigrationOperation { }

    /// <summary>Wraps the provider generator with the selected safe-scaffolding configuration.</summary>
    private static SafeMigrationCSharpMigrationsGenerator CreateMigrationsGenerator(
        IMigrationsCodeGenerator providerGenerator,
        bool isEnabled
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var csharpHelper = provider.GetRequiredService<ICSharpHelper>();

        return new SafeMigrationCSharpMigrationsGenerator(
            providerGenerator,
            csharpHelper,
            new SafeMigrationScaffoldingConfiguration(
                isEnabled,
                SafeMigrationScaffoldingMode.Strict));
    }

    /// <summary>Creates the stable single-column table fixture shared by generation contracts.</summary>
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

    /// <summary>Counts nonoverlapping source fragments for exact generated-contract assertions.</summary>
    private static int CountOccurrences(
        string source,
        string value
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
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
        string? language = "C#",
        string? migrationSource = null
    ) : IMigrationsCodeGenerator
    {
        public const string MigrationSource = "module Company.Migrations.CreateUsers";
        public const string CSharpMigrationSource = "using Microsoft.EntityFrameworkCore.Migrations;\n\n"
            + "#nullable disable\n\n"
            + "namespace Company.Migrations\n"
            + "{\n"
            + "    public partial class CreateUsers : Migration\n"
            + "    {\n"
            + "    }\n"
            + "}\n";

        public const string MetadataSource = "using System;\npartial class CreateUsers "
            + "{ private readonly Guid _id = new Guid(\"1714e708-5197-44c4-b355-ad0f2bc6cc80\"); }";
        public const string SnapshotSource = "using System;\npartial class ReviewContextModelSnapshot "
            + "{ private readonly Guid _id = new Guid(\"1714e708-5197-44c4-b355-ad0f2bc6cc80\"); }";

        private readonly string _migrationSource = migrationSource ?? MigrationSource;

        public string FileExtension => ".cs";

        public string? Language { get; } = language;

        public int MigrationCallCount { get; private set; }

        public int MetadataCallCount { get; private set; }

        public int SnapshotCallCount { get; private set; }

        public IReadOnlyList<MigrationOperation>? LastUpOperations { get; private set; }

        public IReadOnlyList<MigrationOperation>? LastDownOperations { get; private set; }

        public string GenerateMigration(
            string? migrationNamespace,
            string migrationName,
            IReadOnlyList<MigrationOperation> upOperations,
            IReadOnlyList<MigrationOperation> downOperations
        )
        {
            MigrationCallCount++;
            LastUpOperations = upOperations;
            LastDownOperations = downOperations;

            return _migrationSource;
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

    private sealed class TestIndexProjector(
        IReadOnlyList<int> prefixLengths,
        IReadOnlyList<string>? includedColumns = null
    ) : ISafeMigrationCreateIndexScaffoldingProjector
    {
        public SafeMigrationCreateIndexScaffoldingProjection Project(
            CreateIndexOperation operation
        ) => new(operation, prefixLengths, includedColumns);
    }

    private sealed class TestIncludedIndexProjector(
        IReadOnlyList<string> includedColumns
    ) : ISafeMigrationCreateIndexScaffoldingProjector
    {
        public SafeMigrationCreateIndexScaffoldingProjection Project(
            CreateIndexOperation operation
        ) => new(operation, PrefixLengths: null, includedColumns);
    }
}
