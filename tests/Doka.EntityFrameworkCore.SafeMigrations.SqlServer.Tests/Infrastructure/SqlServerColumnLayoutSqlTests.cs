namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies physical column layout proof without counting overflow-capable variable payload.</summary>
public sealed class SqlServerColumnLayoutSqlTests
{
    private const string ConnectionString = "Server=localhost;Database=layout;Integrated Security=true;";

    /// <summary>Uses the supported scalar type's actual storage width and packed-region classification.</summary>
    /// <param name="storeType">The authored built-in store type.</param>
    /// <param name="bytes">The fixed scalar byte contribution.</param>
    /// <param name="bit">Whether the column participates in packed bit storage.</param>
    /// <param name="variable">Whether the column requires a variable-offset entry.</param>
    [Theory]
    [InlineData("char(5000)", 5000, false, false)]
    [InlineData("nchar(2000)", 4000, false, false)]
    [InlineData("binary(300)", 300, false, false)]
    [InlineData("varchar(8000)", 0, false, true)]
    [InlineData("nvarchar(max)", 0, false, true)]
    [InlineData("varbinary(max)", 0, false, true)]
    [InlineData("bit", 0, true, false)]
    [InlineData("int", 4, false, false)]
    [InlineData("uniqueidentifier", 16, false, false)]
    [InlineData("decimal(9,0)", 5, false, false)]
    [InlineData("decimal(38,0)", 17, false, false)]
    [InlineData("time(2)", 3, false, false)]
    [InlineData("datetime2(4)", 7, false, false)]
    [InlineData("datetimeoffset(7)", 10, false, false)]
    public void ColumnStorage_UsesPhysicalScalarWidth(
        string storeType,
        int bytes,
        bool bit,
        bool variable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var clrType = storeType.StartsWith("binary", StringComparison.Ordinal)
            || storeType.StartsWith("varbinary", StringComparison.Ordinal) ? typeof(byte[])
            : storeType.StartsWith("char", StringComparison.Ordinal)
                || storeType.StartsWith("varchar", StringComparison.Ordinal)
                || storeType.StartsWith("nchar", StringComparison.Ordinal)
                || storeType.StartsWith("nvarchar", StringComparison.Ordinal) ? typeof(string)
            : storeType == "bit" ? typeof(bool)
            : storeType == "int" ? typeof(int)
            : storeType == "uniqueidentifier" ? typeof(Guid)
            : storeType.StartsWith("decimal", StringComparison.Ordinal) ? typeof(decimal)
            : storeType.StartsWith("time(", StringComparison.Ordinal) ? typeof(TimeSpan)
            : storeType.StartsWith("datetimeoffset", StringComparison.Ordinal) ? typeof(DateTimeOffset)
            : typeof(DateTime);

        var definition = new ExpectedColumnDefinition("Added", clrType, true, storeType);
        var catalog = CreateLayoutCatalog(context);

        // Act
        var supported = catalog.TryGetColumnStorageLayout(definition, out var layout);

        // Assert
        Assert.True(supported);
        Assert.Equal(bytes, layout.FixedBytes);
        Assert.Equal(bit, layout.IsBit);
        Assert.Equal(variable, layout.IsVariable);
    }

    /// <summary>Missing-column capacity is proved before required-column row binding.</summary>
    [Fact]
    public void RequiredColumn_LayoutGatePrecedesDelayedRows()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateLayoutCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Added", typeof(string), false, "char(5000)")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);
        var layoutGuard = selection.IndexOf("column_fixed_row_limit", StringComparison.Ordinal);
        var rowBinding = selection.IndexOf("EXEC sys.sp_executesql",
            StringComparison.Ordinal);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.NotNull(plan.ColumnLayoutFailureExpression);
        Assert.Contains("max_column_id_used", plan.ColumnLayoutFailureExpression, StringComparison.Ordinal);
        Assert.Contains("column_count >= 1024", plan.ColumnLayoutFailureExpression, StringComparison.Ordinal);
        Assert.Contains("THEN CONVERT(nvarchar(128), NULL) ELSE", plan.ColumnLayoutFailureExpression,
            StringComparison.Ordinal);
        Assert.True(layoutGuard >= 0);
        Assert.True(rowBinding > layoutGuard);
    }

    /// <summary>
    /// Scalar metadata proof rejects unknown physical facets rather than assuming ordinary fixed storage.
    /// </summary>
    [Fact]
    public void PhysicalLayout_RejectsUnrepresentedMetadata()
    {
        // Arrange
        const string objectId = "t.object_id";

        // Act
        var query = SqlServerSafeMigrationCatalogSqlBuilder.BuildColumnLayoutCatalogQuery(objectId);

        // Assert
        Assert.Contains("ty.is_user_defined=0", query, StringComparison.Ordinal);
        Assert.Contains("c.is_computed=0", query, StringComparison.Ordinal);
        Assert.Contains("c.is_sparse=0", query, StringComparison.Ordinal);
        Assert.Contains("c.is_hidden=0", query, StringComparison.Ordinal);
        Assert.Contains("c.generated_always_type=0", query, StringComparison.Ordinal);
        Assert.Contains("AS variable_columns", query, StringComparison.Ordinal);
        Assert.Contains("AS clustered_variable_extra", query, StringComparison.Ordinal);
        Assert.Contains("JOIN sys.indexes", query, StringComparison.Ordinal);
        Assert.Contains("AS clustered_key ", query, StringComparison.Ordinal);
        Assert.DoesNotContain("AS clustered ", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("system_internals", query, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.dm_db", query, StringComparison.Ordinal);
    }

    /// <summary>Uses an ordinary alias in every clustered-key reference rather than a reserved keyword.</summary>
    [Fact]
    public void PhysicalLayout_UsesConsistentNonReservedClusteredKeyAlias()
    {
        // Arrange
        const string objectId = "t.object_id";

        // Act
        var query = SqlServerSafeMigrationCatalogSqlBuilder.BuildColumnLayoutCatalogQuery(objectId);

        // Assert
        Assert.Contains("clustered_key.column_id IS NOT NULL", query, StringComparison.Ordinal);
        Assert.Contains("ON clustered_key.object_id=c.object_id", query, StringComparison.Ordinal);
        Assert.Contains("AND clustered_key.column_id=c.column_id", query, StringComparison.Ordinal);
        Assert.DoesNotContain("clustered.", query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Schema-only admission retains all scalar guards without unconsumed row-capacity work.</summary>
    [Fact]
    public void NullableVariableColumn_UsesOnlyConsumedMetadataProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateLayoutCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Added", typeof(string), true, "varchar(80)")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        var layout = Assert.IsType<string>(plan.ColumnLayoutFailureExpression);
        Assert.False(plan.RequiresDelayedBinding);
        Assert.Contains("column_count >= 1024", layout, StringComparison.Ordinal);
        Assert.Contains("max_column_id_used", layout, StringComparison.Ordinal);
        Assert.Contains("column_fixed_row_limit", layout, StringComparison.Ordinal);
        Assert.Contains("c.is_computed=0", layout, StringComparison.Ordinal);
        Assert.Contains("c.is_sparse=0", layout, StringComparison.Ordinal);
        Assert.Contains("c.generated_always_type=0", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("clustered", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("variable_columns", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT TOP (1)", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_PERMS_BY_NAME", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("c.default_object_id = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.default_constraints", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Argument slices retain exact scalar bounds and reject malformed or extra tokens.</summary>
    /// <param name="storeType">The authored physical scalar type.</param>
    /// <param name="clrType">The corresponding CLR scalar family.</param>
    /// <param name="expected">Whether the physical facets are supported.</param>
    [Theory]
    [InlineData("varchar(8000)", typeof(string), true)]
    [InlineData("nvarchar(4000)", typeof(string), true)]
    [InlineData("varchar(max)", typeof(string), true)]
    [InlineData("decimal(12, 3)", typeof(decimal), true)]
    [InlineData("numeric(38,0)", typeof(decimal), true)]
    [InlineData("datetime2", typeof(DateTime), true)]
    [InlineData("datetime2(0)", typeof(DateTime), true)]
    [InlineData("time(7)", typeof(TimeSpan), true)]
    [InlineData("varchar(8001)", typeof(string), false)]
    [InlineData("nvarchar(4001)", typeof(string), false)]
    [InlineData("varchar(32768)", typeof(string), false)]
    [InlineData("char(max)", typeof(string), false)]
    [InlineData("varchar()", typeof(string), false)]
    [InlineData("varchar(1,2)", typeof(string), false)]
    [InlineData("varchar(1", typeof(string), false)]
    [InlineData("decimal", typeof(decimal), false)]
    [InlineData("decimal()", typeof(decimal), false)]
    [InlineData("decimal(12)", typeof(decimal), false)]
    [InlineData("decimal(,3)", typeof(decimal), false)]
    [InlineData("decimal(12,)", typeof(decimal), false)]
    [InlineData("decimal(12,3,0)", typeof(decimal), false)]
    [InlineData("decimal(0,0)", typeof(decimal), false)]
    [InlineData("decimal(39,0)", typeof(decimal), false)]
    [InlineData("decimal(12,13)", typeof(decimal), false)]
    [InlineData("decimal(256,0)", typeof(decimal), false)]
    [InlineData("decimal(+12,3)", typeof(decimal), false)]
    [InlineData("datetime2()", typeof(DateTime), false)]
    [InlineData("datetime2(8)", typeof(DateTime), false)]
    [InlineData("time(1,2)", typeof(TimeSpan), false)]
    [InlineData("int(4)", typeof(int), false)]
    public void StoreTypeArgumentsPreserveExactFacetValidation(string storeType, Type clrType, bool expected)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateLayoutCatalog(context);
        var definition = new ExpectedColumnDefinition("Value", clrType, true, storeType);

        // Act
        var actual = catalog.TryGetColumnStorageLayout(definition, out _);

        // Assert
        Assert.Equal(expected, actual);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateLayoutCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
