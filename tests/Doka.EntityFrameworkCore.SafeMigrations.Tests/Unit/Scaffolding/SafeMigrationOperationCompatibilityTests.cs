namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationOperationCompatibilityTests
{
    public static TheoryData<MigrationOperation, Type> SupportedOperations => new()
    {
        { new EnsureSchemaOperation { Name = "application" }, typeof(EnsureSchemaIntent) },
        { new DropSchemaOperation { Name = "archive" }, typeof(DropSchemaIntent) },
        { CreateTable(), typeof(EnsureTableIntent) },
        { new DropTableOperation { Name = "items" }, typeof(DropTableIntent) },
        { new RenameTableOperation { Name = "items", NewName = "products" }, typeof(RenameTableIntent) },
        { CreateColumn<AddColumnOperation>(), typeof(EnsureColumnIntent) },
        { CreateAlterColumn(), typeof(AlterColumnIntent) },
        { new DropColumnOperation { Name = "caption", Table = "items" }, typeof(DropColumnIntent) },
        {
            new RenameColumnOperation
            {
                Name = "caption",
                Table = "items",
                NewName = "name",
            },
            typeof(RenameColumnIntent)
        },
        {
            new CreateIndexOperation
            {
                Name = "ix_items_caption",
                Table = "items",
                Columns = ["caption"],
            },
            typeof(EnsureIndexIntent)
        },
        {
            new DropIndexOperation
            {
                Name = "ix_items_caption",
                Table = "items",
            },
            typeof(DropIndexIntent)
        },
        {
            new RenameIndexOperation
            {
                Name = "ix_items_caption",
                Table = "items",
                NewName = "ix_items_name",
            },
            typeof(RenameIndexIntent)
        },
        {
            new AddPrimaryKeyOperation
            {
                Name = "pk_items",
                Table = "items",
                Columns = ["id"],
            },
            typeof(EnsurePrimaryKeyIntent)
        },
        {
            new DropPrimaryKeyOperation { Name = "pk_items", Table = "items" },
            typeof(DropPrimaryKeyIntent)
        },
        {
            new AddUniqueConstraintOperation
            {
                Name = "uq_items_caption",
                Table = "items",
                Columns = ["caption"],
            },
            typeof(EnsureUniqueConstraintIntent)
        },
        {
            new DropUniqueConstraintOperation { Name = "uq_items_caption", Table = "items" },
            typeof(DropUniqueConstraintIntent)
        },
        {
            new AddCheckConstraintOperation
            {
                Name = "ck_items_id",
                Table = "items",
                Sql = "id > 0",
            },
            typeof(EnsureCheckConstraintIntent)
        },
        {
            new DropCheckConstraintOperation { Name = "ck_items_id", Table = "items" },
            typeof(DropCheckConstraintIntent)
        },
        {
            new AddForeignKeyOperation
            {
                Name = "fk_items_groups",
                Table = "items",
                Columns = ["group_id"],
                PrincipalTable = "groups",
                PrincipalColumns = ["id"],
            },
            typeof(EnsureForeignKeyIntent)
        },
        {
            new DropForeignKeyOperation { Name = "fk_items_groups", Table = "items" },
            typeof(DropForeignKeyIntent)
        },
    };

    public static TheoryData<MigrationOperation> UnsupportedOperations => new()
    {
        new AlterDatabaseOperation(),
        new AlterTableOperation { Name = "items" },
        new CreateSequenceOperation { Name = "item_ids" },
        new AlterSequenceOperation { Name = "item_ids" },
        new DropSequenceOperation { Name = "item_ids" },
        new RenameSequenceOperation { Name = "item_ids", NewName = "product_ids" },
        new RestartSequenceOperation { Name = "item_ids", StartValue = 42 },
        new SqlOperation { Sql = "SELECT 1;" },
        new InsertDataOperation
        {
            Table = "items",
            Columns = ["id"],
            Values = new object[,] { { 1 } },
        },
        new UpdateDataOperation
        {
            Table = "items",
            KeyColumns = ["id"],
            KeyValues = new object[,] { { 1 } },
            Columns = ["caption"],
            Values = new object[,] { { "updated" } },
        },
        new DeleteDataOperation
        {
            Table = "items",
            KeyColumns = ["id"],
            KeyValues = new object[,] { { 1 } },
        },
    };

    [Theory]
    [MemberData(nameof(SupportedOperations))]
    public void Normalize_ConvertsEverySupportedStandardOperation(
        MigrationOperation operation,
        Type expectedIntentType
    )
    {
        // Arrange
        ISafeMigrationProviderOperationAdapter? providerAdapter = null;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(operation, providerAdapter);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);

        Assert.Equal(SafeMigrationOperationCompatibilityKind.Safe, result.Kind);
        Assert.IsType(expectedIntentType, safeOperation.Intent);
    }

    [Fact]
    public void Normalize_AlterColumnMatchesGeneratedRepairPolicy()
    {
        // Arrange
        var operation = CreateAlterColumn();

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(operation, providerAdapter: null);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);

        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, safeOperation.Policy);
        var intent = Assert.IsType<AlterColumnIntent>(safeOperation.Intent);
        Assert.NotNull(intent.OldDefinition);
        Assert.Equal("varchar(100)", intent.OldDefinition.StoreType);
        Assert.Equal("varchar(200)", intent.Definition.StoreType);
    }

    [Theory]
    [MemberData(nameof(UnsupportedOperations))]
    public void Normalize_RejectsOperationsWithoutCompleteContracts(
        MigrationOperation operation
    )
    {
        // Arrange
        ISafeMigrationProviderOperationAdapter? providerAdapter = null;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(operation, providerAdapter);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
        Assert.Same(operation, result.Operation);
    }

    [Fact]
    public void Normalize_RejectsLossyProviderAnnotations()
    {
        // Arrange
        var operation = new DropColumnOperation
        {
            Name = "caption",
            Table = "items",
        };

        operation["Provider:Unknown"] = "unsafe";

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(operation, providerAdapter: null);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    private static CreateTableOperation CreateTable()
    {
        var operation = new CreateTableOperation { Name = "items" };
        operation.Columns.Add(CreateColumn<AddColumnOperation>());

        return operation;
    }

    private static AlterColumnOperation CreateAlterColumn()
    {
        var operation = CreateColumn<AlterColumnOperation>();
        operation.ColumnType = "varchar(200)";
        operation.MaxLength = 200;
        operation.OldColumn = CreateColumn<AddColumnOperation>();

        return operation;
    }

    private static TOperation CreateColumn<TOperation>()
        where TOperation : ColumnOperation, new()
    {
        var operation = new TOperation
        {
            Name = "caption",
            Table = "items",
            ClrType = typeof(string),
            ColumnType = "varchar(100)",
            MaxLength = 100,
            IsNullable = false,
        };

        return operation;
    }
}
