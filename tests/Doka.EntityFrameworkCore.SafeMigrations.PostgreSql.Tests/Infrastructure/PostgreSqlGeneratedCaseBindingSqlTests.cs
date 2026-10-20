namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies CASE type eligibility without starting a server or trusting application model metadata.</summary>
public sealed class PostgreSqlGeneratedCaseBindingSqlTests
{
    /// <summary>Npgsql 10 treats omitted storage as virtual, not as the supported stored contract.</summary>
    /// <param name="stored">The authored provider storage setting.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputedStorageRequiresExplicitStoredContract(bool? stored)
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "case_nodes", type: "boolean", nullable: true,
            computedColumnSql: "value > 0", stored: stored));

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.Equal(stored != true, plan.IsStaticallyUnsupported);
        // WHY: Explicit storage admits the finite comparison, whose live
        // builtin operands now retain their separate dynamic type-refusal code.
        Assert.Equal(stored != true ? "virtual_generated_column" : "generated_case_operand_type",
            plan.UnsupportedCode);
    }

    /// <summary>New tables reject statically incompatible conditions and result families before native DDL.</summary>
    /// <param name="expression">The incompatible or unresolved generated CASE contract.</param>
    [Theory]
    [InlineData("CASE WHEN left_bound THEN TRUE ELSE FALSE END")]
    [InlineData("CASE WHEN left_bound > 0 THEN TRUE ELSE 1 END")]
    [InlineData("CASE WHEN missing > 0 THEN TRUE ELSE FALSE END")]
    [InlineData("CASE WHEN left_bound > 0 THEN custom_flag(left_bound) ELSE FALSE END")]
    [InlineData("CASE WHEN left_bound > 0 THEN -TRUE ELSE FALSE END")]
    [InlineData("CASE WHEN left_bound > 0 THEN COALESCE() ELSE FALSE END")]
    public void FreshTableRejectsInvalidCaseBindingAndKeepsPrerequisiteAgreement(string expression)
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("case_nodes", table => new
        {
            left_bound = table.Column<long>("bigint", nullable: false),
            has_descendants = table.Column<bool>("boolean", nullable: false,
                computedColumnSql: expression, stored: true),
        });

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(operation);
        var prerequisite = catalog.BuildPrerequisiteExpression(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("generated_case_operand_type", plan.UnsupportedCode);
        Assert.Equal("'unsupported'", plan.StateExpression);
        Assert.Equal(prerequisite, plan.PrerequisiteExpression);
        Assert.Equal("TRUE", prerequisite);
    }

    /// <summary>Qualifies live source operands by actual native OIDs instead of target model proposals.</summary>
    [Fact]
    public void StandaloneCaseAddRequiresRealSourceTypeEvidence()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "has_descendants", "case_nodes", type: "boolean", nullable: false,
            computedColumnSql: "right_bound > CASE WHEN left_bound = 9223372036854775807 "
                + "THEN left_bound ELSE left_bound + 1 END", stored: true));

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal("generated_case_operand_type", plan.UnsupportedCode);
        Assert.Contains("a.attname OPERATOR(pg_catalog.=) 'left_bound' "
            + "AND a.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[20, 21, 23]::pg_catalog.oid[])",
            plan.StateExpression, StringComparison.Ordinal);

        Assert.Contains("a.attname OPERATOR(pg_catalog.=) 'right_bound' "
            + "AND a.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[20, 21, 23, 700, 701, 1700]::pg_catalog.oid[])",
            plan.StateExpression, StringComparison.Ordinal);

        Assert.DoesNotContain("PREPARE", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Fully declared sources retain a catalog-independent proof and need no added live probes.</summary>
    /// <param name="sourceType">The explicit builtin alias, or null to use the CLR mapping.</param>
    /// <param name="targetType">The explicit generated type alias, or null to use the CLR mapping.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("bigint", "boolean")]
    [InlineData("int8", "bool")]
    public void FreshValidCaseDoesNotAddSourceCatalogProbes(string? sourceType, string? targetType)
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("case_nodes", table => new
        {
            left_bound = table.Column<long>(sourceType, nullable: false),
            has_descendants = table.Column<bool>(targetType, nullable: false,
                computedColumnSql: "CASE WHEN left_bound > 0 THEN TRUE ELSE FALSE END", stored: true),
        });

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Null(plan.UnsupportedCode);
        Assert.DoesNotContain("a.atttypid IN (", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Creates only provider services; no database connection is opened by these contracts.</summary>
    private static SafeMigrationDbContext CreateContext() => new(
        "Host=127.0.0.1;Port=1;Database=case_binding;Username=test;Password=test", registerSafeMigrations: false);

    /// <summary>Uses the same metadata renderer as preflight and runtime execution.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder CreateCatalog(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
