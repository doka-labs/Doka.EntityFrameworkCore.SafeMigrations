namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies delayed catalog permissions independently of row and server metadata permissions.</summary>
public sealed class SqlServerDependencyCatalogPermissionTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=dependency_permissions;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Every expression-dependency reader proves effective access before dynamic classification.</summary>
    /// <param name="kind">The protected operation family.</param>
    [Theory]
    [InlineData("integer")]
    [InlineData("text")]
    [InlineData("drop-column")]
    [InlineData("rename-column")]
    [InlineData("drop-table")]
    [InlineData("rename-table")]
    public void ProtectedClassifier_IsDelayedBehindCatalogPermission(
        string kind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent intent = kind switch
        {
            "integer" => new AlterColumnIntent("items",
                new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
            "text" => new AlterColumnIntent("items",
                new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(64)"),
                new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(32)")),
            "drop-column" => new DropColumnIntent("Value", "items"),
            "rename-column" => new RenameColumnIntent("Value", "items", "Renamed"),
            "drop-table" => new DropTableIntent("items"),
            "rename-table" => new RenameTableIntent("items", newName: "renamed"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.True(plan.RequiresExpressionDependencyRead);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains(SqlServerSafeMigrationCatalogSqlBuilder.ExpressionDependencyReadPermission,
            plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Equal("N'unsupported'", plan.StateEvaluationGuardFailureExpression);
        Assert.Contains("dependency_catalog_permission", plan.PrerequisiteFailureCodeExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FROM sys.sql_expression_dependencies", plan.PrerequisiteFailureCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("FROM sys.sql_expression_dependencies", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Unrelated column creation and bounded CHECK plans do not acquire dependency-view access.</summary>
    /// <param name="check">Whether the operation creates an integer CHECK instead of a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedClassifier_DoesNotRequireProtectedCatalog(
        bool check
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent intent = check
            ? new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("CK_value", "items", "[Value]>=0"))
            : new EnsureColumnIntent("items", new ExpectedColumnDefinition("Value", typeof(int), true, "int"));

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe));

        // Assert
        Assert.False(plan.RequiresExpressionDependencyRead);
        Assert.DoesNotContain("dependency_catalog_permission", plan.StateEvaluationGuardExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sys.sql_expression_dependencies", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Integer and text rewrites require UPDATE without requiring it for an exact target replay.</summary>
    /// <param name="widening">Whether the transition widens an integer instead of contracting text.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportedAlter_WritePermissionKeepsMatchingException(
        bool widening
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var source = widening ? new ExpectedColumnDefinition("Value", typeof(int), false, "int")
            : new ExpectedColumnDefinition("Value", typeof(string), true, "nvarchar(80)", maxLength: 80);

        var target = widening ? new ExpectedColumnDefinition("Value", typeof(long), false, "bigint")
            : new ExpectedColumnDefinition("Value", typeof(string), true, "nvarchar(20)", maxLength: 20);

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Contains("N'UPDATE'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains(plan.Postcondition, plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("column_alter_write_permission", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Equal("N'unsupported'", plan.StateEvaluationGuardFailureExpression);
    }

    /// <summary>Text ALTER proves SELECT outside its row scope without inferring a grant from target matching.</summary>
    [Fact]
    public void TextAlter_ReadPermissionPrecedesRowBinding()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("Value", typeof(string), true, "nvarchar(20)", maxLength: 20),
            new ExpectedColumnDefinition("Value", typeof(string), true, "nvarchar(80)", maxLength: 80)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("N'OBJECT', N'SELECT'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("column_alter_read_permission", plan.PrerequisiteFailureCodeExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [dbo].[items]", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [dbo].[items]", plan.PrerequisiteFailureCodeExpression, StringComparison.Ordinal);
        Assert.Contains("FROM [dbo].[items]", plan.StateExpression, StringComparison.Ordinal);
        Assert.Equal("N'unsupported'", plan.StateEvaluationGuardFailureExpression);
    }

    /// <summary>Referenced key drops certify only exact key-kind conflicts for graph reconciliation.</summary>
    /// <param name="primary">Whether the drop targets a primary key instead of a UNIQUE key.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencedKeyDrop_RetainsKindBoundClassification(
        bool primary
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent intent = primary ? new DropPrimaryKeyIntent("key", "items")
            : new DropUniqueConstraintIntent("key", "items");

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Contains("incoming_foreign_key_key_dependency", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("kc.type = N'" + (primary ? "PK" : "UQ") + "'", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("fk.key_index_id = kc.unique_index_id", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
    }
}
