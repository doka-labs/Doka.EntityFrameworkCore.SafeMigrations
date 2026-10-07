namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies metadata-only lossless integer transition contracts.</summary>
public sealed class SqlServerIntegerWideningSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=integer_widening_sql;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Admits every built-in integer widening without reading values for unchanged nullability.</summary>
    [Theory]
    [InlineData(typeof(byte), "tinyint", typeof(short), "smallint")]
    [InlineData(typeof(byte), "tinyint", typeof(int), "int")]
    [InlineData(typeof(byte), "tinyint", typeof(long), "bigint")]
    [InlineData(typeof(short), "smallint", typeof(int), "int")]
    [InlineData(typeof(short), "smallint", typeof(long), "bigint")]
    [InlineData(typeof(int), "int", typeof(long), "bigint")]
    public void LosslessWidening_UsesExactSourceAndCatalogOnlyProof(
        Type sourceClr,
        string sourceType,
        Type targetClr,
        string targetType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var source = new ExpectedColumnDefinition("Value", sourceClr, false, sourceType);
        var target = new ExpectedColumnDefinition("Value", targetClr, false, targetType);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.False(plan.RequiresLiveDataProof);
        Assert.True(plan.RequiresExpressionDependencyRead);
        Assert.DoesNotContain("FROM [dbo].[items]", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("DATALENGTH", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("sys.partition_schemes", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("sys.stats_columns", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("s.auto_created = 0", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("ty.name = N'" + sourceType + "'", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("8060", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("MS_Description", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("MS_Description", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Narrowing and incompatible CLR/store declarations never acquire a safe widening proof.</summary>
    [Theory]
    [InlineData(typeof(long), "bigint", typeof(int), "int")]
    [InlineData(typeof(int), "int", typeof(short), "smallint")]
    [InlineData(typeof(int), "int", typeof(decimal), "decimal(20,0)")]
    [InlineData(typeof(bool), "bit", typeof(int), "int")]
    [InlineData(typeof(int), "int", typeof(int), "bigint")]
    public void UnsupportedNumericChange_HasNoRepair(
        Type sourceClr,
        string sourceType,
        Type targetClr,
        string targetType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("Value", targetClr, false, targetType),
            new ExpectedColumnDefinition("Value", sourceClr, false, sourceType)), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, plan.RepairCapability);
    }

    /// <summary>A nullable-to-required widening requires a delayed NULL proof, not a range scan.</summary>
    [Fact]
    public void WideningAndNotNull_ProbesNullsOnly()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), true, "int")), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("[Value] IS NULL", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("DATALENGTH", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Equivalent typed literal defaults retain contract stamps; changed values are not safe.</summary>
    [Theory]
    [InlineData(7L, SafeMigrationRepairCapability.Safe)]
    [InlineData(8L, SafeMigrationRepairCapability.None)]
    public void IntegerDefault_RequiresUnchangedSemanticValue(
        long value,
        SafeMigrationRepairCapability expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var source = new ExpectedColumnDefinition("Value", typeof(int), false, "int",
            defaultValue: SafeMigrationDefaultValue.Literal(7));

        var target = new ExpectedColumnDefinition("Value", typeof(long), false, "bigint",
            defaultValue: SafeMigrationDefaultValue.Literal(value));

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe));

        // Assert
        Assert.Equal(expected, plan.RepairCapability);
        if (expected == SafeMigrationRepairCapability.Safe)
        {
            Assert.Contains("sys.extended_properties", plan.RepairPrecondition, StringComparison.Ordinal);
            Assert.NotNull(plan.PostApplySql);
            Assert.Contains("sp_addextendedproperty", plan.PostApplySql, StringComparison.Ordinal);
        }
    }

    /// <summary>Identity widening preserves seed and increment, never adding identity or reseeding.</summary>
    [Theory]
    [InlineData("3, 7", SafeMigrationRepairCapability.Safe)]
    [InlineData("4, 7", SafeMigrationRepairCapability.None)]
    [InlineData("3, 8", SafeMigrationRepairCapability.None)]
    [InlineData(null, SafeMigrationRepairCapability.None)]
    public void IntegerIdentity_RequiresUnchangedGenerationContract(
        string? targetIdentity,
        SafeMigrationRepairCapability expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var oldColumn = new AddColumnOperation
        {
            Name = "Value", Table = "items", ClrType = typeof(int), ColumnType = "int",
        };

        oldColumn.AddAnnotation("SqlServer:Identity", "3, 7");
        var newColumn = new AddColumnOperation
        {
            Name = "Value", Table = "items", ClrType = typeof(long), ColumnType = "bigint",
        };

        if (targetIdentity is not null)
        {
            newColumn.AddAnnotation("SqlServer:Identity", targetIdentity);
        }

        var source = SafeMigrationExpectedDefinitionFactory.From(oldColumn);
        var target = SafeMigrationExpectedDefinitionFactory.From(newColumn);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe));

        // Assert
        Assert.Equal(expected, plan.RepairCapability);
        if (expected == SafeMigrationRepairCapability.Safe)
        {
            Assert.Contains("identity_column.seed_value) = 3", plan.RepairPrecondition, StringComparison.Ordinal);
            Assert.Contains("identity_column.increment_value) = 7", plan.RepairPrecondition, StringComparison.Ordinal);
        }
    }
}
