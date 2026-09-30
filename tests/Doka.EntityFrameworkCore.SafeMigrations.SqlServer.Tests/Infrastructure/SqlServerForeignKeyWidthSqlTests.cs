namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies SQL Server FK arity and declared-byte bounds before any row or DDL probe.</summary>
public sealed class SqlServerForeignKeyWidthSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=fk_width;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

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
        var delayed = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(257, plan);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal(3, plan.PrerequisiteExpression.Split("SUM(CONVERT(bigint, c.max_length)) <= 900",
            StringSplitOptions.None).Length);
        Assert.Contains("N'[dbo].[width_child]'", plan.PrerequisiteExpression, StringComparison.Ordinal);
        Assert.Contains("N'[dbo].[width_parent]'", plan.PrerequisiteExpression, StringComparison.Ordinal);
        var prerequisite = delayed.IndexOf(plan.PrerequisiteExpression, StringComparison.Ordinal);
        var rowBinding = delayed.IndexOf("INSERT INTO @doka_analysis EXEC sys.sp_executesql", StringComparison.Ordinal);
        Assert.True(prerequisite >= 0);
        Assert.True(rowBinding > prerequisite);
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
