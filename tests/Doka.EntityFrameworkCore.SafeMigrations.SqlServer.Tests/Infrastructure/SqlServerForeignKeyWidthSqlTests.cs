namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies SQL Server FK arity and declared-byte bounds before any row or DDL probe.</summary>
public sealed class SqlServerForeignKeyWidthSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=fk_width;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Authored storage cannot bypass the runtime FK contract's rejection of computed operands.</summary>
    /// <param name="sparse">Whether the definition is captured as one independently projected operand.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyAuthoredComputedOperand_RemainsUnqualified(
        bool sparse
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var table = new ExpectedTableDefinition("sparse_child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), true, "int", computedColumnSql: "1",
                isStored: true)]);

        var foreign = new ExpectedForeignKeyDefinition("FK_sparse", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var sql = sparse ? catalog.BuildSparseForeignKeyPrerequisite(table, foreign, 0)
            : catalog.BuildInlineForeignKeyPrerequisite(table, foreign);

        // Assert
        if (sparse)
        {
            Assert.EndsWith(" AND 1 = 0", sql, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("1 = 0", sql);
        }
    }

    /// <summary>Untouched dependent metadata retains the same computed-operand exclusion as runtime.</summary>
    [Fact]
    public void SparseLiveOperandCapture_RetainsNativeComputedGuard()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var foreign = new ExpectedForeignKeyDefinition("FK_sparse", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var sql = CreateCatalog(context).BuildSparseForeignKeyPrerequisite(null, foreign, 0);

        // Assert
        Assert.Contains("dc.is_computed = 0", sql, StringComparison.Ordinal);
    }

    /// <summary>An absent child is only a hypothesis; present owners retain their native physical guard.</summary>
    [Fact]
    public void SparseOperandCapture_ConditionsMissingOwnerWithoutReadingDependentRows()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var table = new ExpectedTableDefinition("sparse_child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")]);

        var foreign = new ExpectedForeignKeyDefinition("FK_sparse", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var sql = catalog.BuildSparseForeignKeyPrerequisite(table, foreign, 0);

        // Assert
        Assert.Contains("(NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains(" OR (CASE", sql, StringComparison.Ordinal);
        Assert.Contains("is_memory_optimized", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(CONVERT(bigint, c.max_length)) <= 900", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [dbo].[sparse_child]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.indexes", sql, StringComparison.Ordinal);
    }

    /// <summary>Captures mixed endpoints using native facets without referring to absent dependent data.</summary>
    [Fact]
    public void SparseForeignKey_ProvesMixedStorageAndWidthWithoutRowOrCandidateKeyQueries()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var table = new ExpectedTableDefinition("sparse_child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")]);

        var foreign = new ExpectedForeignKeyDefinition("FK_sparse", "sparse_child", ["TenantId", "ParentId"],
            "sparse_parent", ["TenantId", "Id"]);

        // Act
        var sql = catalog.BuildSparseForeignKeyPrerequisite(table, foreign);

        // Assert
        Assert.Contains("SUM(CONVERT(bigint, c.max_length)) <= 900", sql, StringComparison.Ordinal);
        Assert.Contains("dc.system_type_id = pc.system_type_id", sql, StringComparison.Ordinal);
        Assert.Contains("dc.user_type_id = pc.user_type_id", sql, StringComparison.Ordinal);
        Assert.Contains("dc.precision = pc.precision", sql, StringComparison.Ordinal);
        Assert.Contains("dc.scale = pc.scale", sql, StringComparison.Ordinal);
        Assert.Contains("ISNULL(dc.collation_name, N'') = ISNULL(pc.collation_name, N'')", sql,
            StringComparison.Ordinal);
        Assert.Contains("c.name = N'Id'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM [dbo].[sparse_child]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.indexes", sql, StringComparison.Ordinal);
    }

    /// <summary>A projected character operand resolves inherited or explicit collation.</summary>
    /// <param name="explicitCollation">Whether the authored dependent overrides the database default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseForeignKey_CapturesEffectiveCharacterCollation(
        bool explicitCollation
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var column = new ExpectedColumnDefinition("ParentId", typeof(string), true, "char(36)",
            collation: explicitCollation ? new SafeMigrationCollationIdentifier("Latin1_General_100_BIN2") : null);

        var table = new ExpectedTableDefinition("sparse_child", [column]);
        var foreign = new ExpectedForeignKeyDefinition("FK_sparse", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var sql = CreateCatalog(context).BuildSparseForeignKeyPrerequisite(table, foreign);

        // Assert
        Assert.Contains(explicitCollation ? "c.collation_name = N'Latin1_General_100_BIN2'"
            : "c.collation_name = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", sql,
            StringComparison.Ordinal);
    }

    /// <summary>A sparse self-reference uses native principal metadata rather than an invented inline key.</summary>
    [Fact]
    public void SparseSelfReference_UsesExistingPrincipalColumnMetadata()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var table = new ExpectedTableDefinition("sparse_self",
            [new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")]);

        var foreign = new ExpectedForeignKeyDefinition("FK_sparse_self", "sparse_self", ["ParentId"],
            "sparse_self", ["Id"]);

        // Act
        var sql = CreateCatalog(context).BuildSparseForeignKeyPrerequisite(table, foreign);

        // Assert
        Assert.NotEqual("1 = 0", sql);
        Assert.Contains("c.name = N'Id'", sql, StringComparison.Ordinal);
        Assert.Contains("ty.name = N'int'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.indexes", sql, StringComparison.Ordinal);
    }

    /// <summary>Checks both physical sides of standalone FKs before the orphan probe can bind.</summary>
    [Fact]
    public void StandaloneForeignKey_PrerequisiteChecksBothDeclaredWidths()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_width", "width_child", ["Value"], "width_parent", ["Value"])), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var delayed = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal(3, plan.PrerequisiteExpression.Split("SUM(CONVERT(bigint, c.max_length)) <= 900",
            StringSplitOptions.None).Length);
        Assert.Contains("N'[dbo].[width_child]'", plan.PrerequisiteExpression, StringComparison.Ordinal);
        Assert.Contains("N'[dbo].[width_parent]'", plan.PrerequisiteExpression, StringComparison.Ordinal);
        var prerequisite = delayed.IndexOf(plan.PrerequisiteExpression.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var prerequisiteGate = delayed.IndexOf("IF @doka_prerequisite = 1", StringComparison.Ordinal);
        var rowBinding = delayed.IndexOf("IF @doka_prerequisite = 1 EXEC sys.sp_executesql", StringComparison.Ordinal);
        Assert.True(prerequisite >= 0);
        Assert.True(prerequisiteGate > prerequisite);
        Assert.Equal(prerequisiteGate, rowBinding);
    }

    /// <summary>Rejects an immutable over-arity standalone contract before rendering any baseline.</summary>
    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void StandaloneForeignKey_ArityIsStaticallyBounded(
        int count,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var names = Names(count);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_arity", "width_child", names, "width_parent", names)), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        if (unsupported)
        {
            Assert.Equal("foreign_key_unproven_width", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(Record.Exception(() =>
                context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model)));
        }
    }

    /// <summary>Checks dependent authored widths for inline external and self-referencing FKs.</summary>
    [Theory]
    [InlineData(900, false)]
    [InlineData(901, true)]
    public void InlineForeignKey_DeclaredWidthIsBoundedBeforeNewTableDdl(
        int bytes,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var foreignKey = new ExpectedForeignKeyDefinition("FK_inline_width", "width_child", ["Value"],
            "width_parent", ["Value"]);

        var table = new ExpectedTableDefinition("width_child",
            [new ExpectedColumnDefinition("Value", typeof(string), false,
                "char(" + bytes.ToString(CultureInfo.InvariantCulture) + ")")],
            foreignKeys: [foreignKey]);

        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var shapeSupported = catalog.InlineForeignKeyPhysicalWidthIsSupported(table, foreignKey);

        // Assert
        Assert.Equal(!unsupported, shapeSupported);
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        if (unsupported)
        {
            Assert.Equal("foreign_key_unproven_width", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(Record.Exception(() =>
                context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model)));
        }
        else
        {
            Assert.Contains("SUM(CONVERT(bigint, c.max_length)) <= 900",
                catalog.BuildInlineForeignKeyPrerequisite(table, foreignKey), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Does not restore a projected inline proof when the referenced authored storage exceeds 900 bytes.
    /// </summary>
    [Theory]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public void SelfReference_PrincipalAuthoredWidthCannotBypassTheSharedGate(
        int principalBytes,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var foreignKey = new ExpectedForeignKeyDefinition(
            "FK_self_width", "width_child", ["Value"], "width_child", ["ParentValue"]);

        var table = new ExpectedTableDefinition("width_child",
            [
                new ExpectedColumnDefinition("Value", typeof(string), false, "char(900)"),
                new ExpectedColumnDefinition("ParentValue", typeof(string), false,
                    "char(" + principalBytes.ToString(CultureInfo.InvariantCulture) + ")"),
            ], uniqueConstraints: [new ExpectedUniqueConstraintDefinition(
                "UQ_parent_width", "width_child", ["ParentValue"])],
            foreignKeys: [foreignKey]);

        // Act
        var actual = CreateCatalog(context).InlineForeignKeyPhysicalWidthIsSupported(table, foreignKey);

        // Assert
        Assert.Equal(supported, actual);
    }

    /// <summary>
    /// Checks arity on authored new children independently of whether their parent has been projected.
    /// </summary>
    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void InlineForeignKey_AuthoredArityCannotBypassProjectedParentProof(
        int count,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var names = Names(count);
        var foreignKey = new ExpectedForeignKeyDefinition(
            "FK_inline_arity", "width_child", names, "width_parent", names);

        var table = new ExpectedTableDefinition("width_child",
            names.Select(static name => new ExpectedColumnDefinition(name, typeof(int), false, "int")),
            foreignKeys: [foreignKey]);

        // Act
        var catalog = CreateCatalog(context);
        var actual = catalog.InlineForeignKeyPhysicalWidthIsSupported(table, foreignKey);
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Equal(supported, actual);
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        if (!supported)
        {
            Assert.Equal("foreign_key_unproven_width", plan.UnsupportedCode);
        }
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static string[] Names(int count) => Enumerable.Range(0, count)
        .Select(static ordinal => "K" + ordinal.ToString(CultureInfo.InvariantCulture)).ToArray();
}
