namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies computed-column catalog and invocation-local comparison contracts without a server.</summary>
public sealed class SqlServerGeneratedColumnSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=generated;User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Accepts an overflow-guarded generated flag and compares its native physical expression.</summary>
    /// <param name="stored">Whether the generated value is persisted.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedFlagUsesNativeDefinitionAndStorageComparison(bool stored)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var definition = Flag(stored);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("generated_nodes", definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("sys.computed_columns", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("is_persisted = " + (stored ? "1" : "0"), plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("Latin1_General_100_BIN2", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("STRING_AGG", plan.CatalogPreambleSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT TOP (0)", plan.CatalogPreambleSql, StringComparison.Ordinal);
        Assert.DoesNotContain("sp_addextendedproperty", plan.PostApplySql ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Does not turn an opaque, volatile, or potentially NULL result into an accepted required flag.</summary>
    /// <param name="sql">The authored computed SQL.</param>
    /// <param name="code">The stable fail-closed rejection.</param>
    [Theory]
    [InlineData("[R]", "computed_column_nullability_unproven")]
    [InlineData("NULL", "computed_column_nullability_unproven")]
    [InlineData("GETDATE()", "computed_expression_unproven")]
    [InlineData("SELECT 1", "opaque_sql_expression")]
    [InlineData("CAST(256 AS tinyint)", "computed_column_nullability_unproven")]
    [InlineData("CAST(9223372036854775807 AS int)", "computed_column_nullability_unproven")]
    [InlineData("CASE WHEN [L]>0 THEN CAST(256 AS tinyint) ELSE 0 END", "computed_column_nullability_unproven")]
    [InlineData("COALESCE([L],1)", "computed_column_nullability_unproven")]
    [InlineData("TRUE", "computed_expression_unproven")]
    [InlineData("FALSE", "computed_expression_unproven")]
    public void UnprovenRequiredGeneratedExpressionsFailClosed(string sql, string code)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var definition = new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit", computedColumnSql: sql);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("generated_nodes", definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal(code, plan.UnsupportedCode);
    }

    /// <summary>Requires computed-index determinism, precision and the current native SET contract.</summary>
    [Fact]
    public void ComputedIndexEligibilityDoesNotRequireOrdinaryColumns()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var definition = new ExpectedIndexDefinition("IX_generated_nodes", "generated_nodes",
            [new ExpectedIndexKeyDefinition("HasChildren"), new ExpectedIndexKeyDefinition("R")]);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureIndexIntent(definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Contains("IsDeterministic", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("IsPrecise", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("IsIndexable", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("MAX(CONVERT(int, c.is_computed)) = 0", plan.StateExpression,
            StringComparison.Ordinal);
    }

    /// <summary>Defers unresolved operand spellings to native identifier binding without throwing.</summary>
    [Fact]
    public void UnresolvedDeclaredComputedOperandUsesNativeProofInsteadOfThrowing()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var table = new ExpectedTableDefinition("generated_nodes",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int"), Flag()]);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("Computed operand metadata unavailable", plan.CatalogPreambleSql, StringComparison.Ordinal);
    }

    /// <summary>A virtual generated value consumes a column id but no stored scalar field or packed bit slot.</summary>
    [Fact]
    public void VirtualComputedStorageDoesNotInventRowMaterialization()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var column = Flag();

        // Act
        var supported = catalog.TryGetColumnStorageLayout(column, out var storage);
        var materializes = SqlServerSafeMigrationCatalogSqlBuilder.ColumnAdditionMaterializesRows(column, storage);

        // Assert
        Assert.True(supported);
        Assert.Equal(0, storage.FixedBytes);
        Assert.False(storage.IsBit);
        Assert.False(storage.IsVariable);
        Assert.False(materializes);
    }

    /// <summary>Keeps captured numeric spelling rather than implicitly retyping its CLR literals.</summary>
    [Fact]
    public void CapturedComputedSourceRetainsOriginalNumericLexemes()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        const string source = "2147483648 / 2147483647";
        var expression = ParseRequired(source);
        var definition = new ExpectedColumnDefinition("Ratio", typeof(decimal), true, "decimal(38,0)",
            computedExpression: expression) { CapturedComputedSql = source, };

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("generated_nodes", definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains(source, plan.CatalogPreambleSql, StringComparison.Ordinal);
        Assert.DoesNotContain("AS bigint", plan.CatalogPreambleSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Never promotes authored T-SQL Boolean keywords to literal safety proofs.</summary>
    /// <param name="captured">Whether the validated source accompanies its parsed AST.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooleanKeywordCheckSourcesRemainUnsupported(bool captured)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var definition = captured
            ? ExpectedCheckConstraintDefinition.FromCapturedExpression("CK_true", "generated_nodes",
                SafeMigrationSql.Literal(true), "TRUE", null)
            : new ExpectedCheckConstraintDefinition("CK_true", "generated_nodes", "TRUE");

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("check_expression_unproven", plan.UnsupportedCode);
    }

    /// <summary>Distinguishes native structured Boolean defaults from ambiguous authored keywords.</summary>
    /// <param name="structured">Whether the caller supplied a typed expression instead of provider SQL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooleanDefaultProofRespectsSourceProvenance(bool structured)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var value = structured ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(true))
            : SafeMigrationDefaultValue.Sql("TRUE");

        var definition = new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit", defaultValue: value);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("generated_nodes", definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Equal(!structured, plan.IsStaticallyUnsupported);
        Assert.Equal(structured ? null : "default_expression_unproven", plan.UnsupportedCode);
    }

    /// <summary>Native floating-point tokens cannot acquire an exact integer projection certificate.</summary>
    /// <param name="source">The authored comparison, retaining its numeric token.</param>
    /// <param name="supported">Whether exact integer comparison is proven.</param>
    [Theory]
    [InlineData("[Value]>=1", true)]
    [InlineData("[Value]>=1.0", true)]
    [InlineData("[Value]>=1e0", false)]
    [InlineData("[Value]>=1E0", false)]
    [InlineData("2147483648/2147483647>1", false)]
    public void IntegerCheckProjectionDoesNotEraseNativeFloatingTokens(string source, bool supported)
    {
        // Arrange
        var definition = new ExpectedCheckConstraintDefinition("CK_value", "generated_nodes", source);
        var expression = SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredCheckExpression(definition)
            ?? throw new InvalidOperationException("The test fixture expression must parse.");

        // Act
        var actual = SqlServerSafeMigrationCatalogSqlBuilder.IsSafeIntegerCheckPredicate(expression);

        // Assert
        Assert.Equal(supported, actual);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationSqlExpression ParseRequired(string source)
        => SafeMigrationSqlExpressionParser.TryParse(source, out var expression)
            ? expression : throw new InvalidOperationException("The test fixture expression must parse.");

    /// <summary>Creates the isolated overflow-guarded finite Boolean flag contract.</summary>
    internal static ExpectedColumnDefinition Flag(bool stored = false)
        => new("HasChildren", typeof(bool), false, "bit", computedColumnSql:
            "CONVERT(bit, CASE WHEN [R] > CASE WHEN [L] = 9223372036854775807 "
            + "THEN [L] ELSE [L] + 1 END THEN 1 ELSE 0 END)", isStored: stored);
}
