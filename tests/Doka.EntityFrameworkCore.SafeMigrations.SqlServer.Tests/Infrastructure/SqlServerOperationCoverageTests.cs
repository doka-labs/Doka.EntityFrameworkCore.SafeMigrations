namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Prevents a new or existing Core operation family from falling through SQL Server generation.
/// </summary>
public sealed class SqlServerOperationCoverageTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=coverage;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Requires a SQL Server command path for every SafeMigrations operation kind.
    /// </summary>
    [Fact]
    public void EveryCoreOperationKind_HasAnExplicitSqlServerGenerationPath()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operations = CreateOperationMatrix(context.Database.ProviderName!);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var actualKinds = operations
            .Cast<SafeMigrationOperation>()
            .Select(operation => operation.Intent.Kind)
            .OrderBy(kind => kind)
            .ToArray();

        var generated = operations
            .Select(operation => generator.Generate([operation], context.Model))
            .ToArray();

        // Assert
        Assert.Equal(Enum.GetValues<SafeMigrationOperationKind>(), actualKinds);
        Assert.All(generated, commands => Assert.NotEmpty(commands));
        Assert.All(generated, commands => Assert.All(
            commands,
            command => Assert.False(string.IsNullOrWhiteSpace(command.CommandText))));
    }

    private static List<MigrationOperation> CreateOperationMatrix(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        builder.EnsureSchemaExists("matrix_schema");
        builder.DropSchemaIfExists("matrix_schema");
        builder.CreateTableIfNotExists(
            "matrix_table",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        builder.DropTableIfExists("matrix_table");
        builder.RenameTableIfExists("matrix_table", "renamed_table");
        builder.AddColumnIfNotExists<int>("Amount", "matrix_table", type: "int", nullable: true);
        builder.DropColumnIfExists("Amount", "matrix_table");
        builder.RenameColumnIfExists("Amount", "matrix_table", "NewAmount");
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<long>(
                "Amount",
                "matrix_table",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_matrix_table_Amount", "matrix_table", ["Amount"]);
        builder.DropIndexIfExists("IX_matrix_table_Amount", "matrix_table");
        builder.RenameIndexIfExists("IX_matrix_table_Amount", "matrix_table", "IX_matrix_table_NewAmount");
        builder.AddPrimaryKeyIfNotExists("PK_matrix_table", "matrix_table", ["Id"]);
        builder.DropPrimaryKeyIfExists("PK_matrix_table", "matrix_table");
        builder.AddUniqueConstraintIfNotExists("UQ_matrix_table_Amount", "matrix_table", ["Amount"]);
        builder.DropUniqueConstraintIfExists("UQ_matrix_table_Amount", "matrix_table");
        builder.AddCheckConstraintIfNotExists("CK_matrix_table_Amount", "matrix_table", "[Amount] >= 0");
        builder.DropCheckConstraintIfExists("CK_matrix_table_Amount", "matrix_table");
        builder.AddForeignKeyIfNotExists(
            "FK_matrix_table_parent",
            "matrix_table",
            ["ParentId"],
            "parent_table",
            ["Id"]);
        builder.DropForeignKeyIfExists("FK_matrix_table_parent", "matrix_table");
        builder.EnsureModelManagedDataFromModel(
            "matrix_table",
            ["Id"],
            ["int"],
            ["Id", "Amount"],
            ["int", "int"],
            new object?[,] { { 1, 10 } });
        builder.UpdateModelManagedDataFromModel(
            "matrix_table",
            ["Id"],
            ["int"],
            new object?[,] { { 1 } },
            ["Amount"],
            ["int"],
            new object?[,] { { 10 } },
            new object?[,] { { 11 } });
        builder.DeleteModelManagedDataFromModel(
            "matrix_table",
            ["Id"],
            ["int"],
            new object?[,] { { 1 } },
            ["Amount"],
            ["int"],
            new object?[,] { { 11 } });

        return builder.Operations;
    }
}
