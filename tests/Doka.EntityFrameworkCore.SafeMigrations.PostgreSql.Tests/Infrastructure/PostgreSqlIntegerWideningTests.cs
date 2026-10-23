namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Bounds the provider's signed-integer transition lattice independently of any database.</summary>
public sealed class PostgreSqlIntegerWideningTests
{
    /// <summary>Keeps optional role introspection out of ordinary runtime DROP generation.</summary>
    [Fact]
    public void IndexDropProofIsCapturedOnlyInTheExplicitAnalysisPath()
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var operation = new SafeMigrationOperation(new DropIndexIntent("ix_rows", "rows"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var runtime = builder.Build(operation);
        var analysis = builder.Build(operation, includeIndexDropColumnProof: true);

        // Assert
        Assert.Null(runtime.ClassificationCodeExpression);
        Assert.Contains(PostgreSqlSafeMigrationCatalogSqlBuilder.OrdinaryIndexDropProofCode,
            analysis.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.pg_opclass", analysis.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Equal(runtime.StateExpression, analysis.StateExpression);
        Assert.Equal(runtime.Postcondition, analysis.Postcondition);
    }

    /// <summary>Accepts only strictly wider built-in widths with their exact CLR contracts.</summary>
    /// <param name="sourceType">The source built-in store type.</param>
    /// <param name="targetType">The target built-in store type.</param>
    /// <param name="nullable">The unchanged nullability contract.</param>
    [Theory]
    [InlineData("smallint", "integer", false)]
    [InlineData("smallint", "bigint", false)]
    [InlineData("integer", "bigint", false)]
    [InlineData("smallint", "integer", true)]
    [InlineData("smallint", "bigint", true)]
    [InlineData("integer", "bigint", true)]
    public void BuiltInWideningUsesExactSourceWithoutDataScan(string sourceType, string targetType, bool nullable)
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("integer_rows",
            new ExpectedColumnDefinition("value", ClrType(targetType), nullable, targetType),
            new ExpectedColumnDefinition("value", ClrType(sourceType), nullable, sourceType)),
            SafeMigrationPolicy.RepairIfSafe);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = builder.Build(operation, includeTransitionEvidence: true);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, plan.RepairOperationalImpact);
        Assert.Null(plan.DataProbe);
        Assert.Null(plan.NullabilityDataProbe);
        Assert.Contains("LOCK TABLE ONLY", plan.ColumnTransitionLockSqlExpression, StringComparison.Ordinal);
        Assert.Contains("read committed", plan.ColumnTransitionRuntimeGuardExpression, StringComparison.Ordinal);
        Assert.Contains(sourceType, plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("widening_fk", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("widening_dependency", plan.RepairPrecondition, StringComparison.Ordinal);
        Assert.Contains("widening_check", plan.RepairPrecondition, StringComparison.Ordinal);
    }

    /// <summary>Does not borrow range inclusion for narrowing, aliases, domains or unrelated facet changes.</summary>
    /// <param name="caseId">The independently selectable unsupported transition.</param>
    [Theory]
    [InlineData("narrowing")]
    [InlineData("domain")]
    [InlineData("clr")]
    [InlineData("nullability")]
    [InlineData("default")]
    [InlineData("comment")]
    [InlineData("length")]
    [InlineData("precision")]
    [InlineData("identity")]
    [InlineData("unknown-source-annotation")]
    [InlineData("strict")]
    public void UnsupportedTransitionsDoNotAcquireRepair(string caseId)
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var source = new ExpectedColumnDefinition("value", typeof(int), false, "integer");
        var target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint");
        switch (caseId)
        {
            case "narrowing":
                (source, target) = (target, source);
                break;
            case "domain":
                source = new ExpectedColumnDefinition("value", typeof(int), false, "custom_integer");
                break;
            case "clr":
                source = new ExpectedColumnDefinition("value", typeof(uint), false, "integer");
                break;
            case "nullability":
                target = new ExpectedColumnDefinition("value", typeof(long), true, "bigint");
                break;
            case "default":
                target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint",
                    defaultValue: SafeMigrationDefaultValue.Literal(0L));
                break;
            case "comment":
                target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint", comment: "next");
                break;
            case "length":
                target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint", maxLength: 16);
                break;
            case "precision":
                target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint", precision: 16);
                break;
            case "identity":
            case "unknown-source-annotation":
                var identityOperation = new AddColumnOperation();
                if (caseId == "identity")
                {
                    identityOperation.AddAnnotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
                }
                else
                {
                    identityOperation.AddAnnotation("Probe:Unreviewed", true);
                }

                source = new ExpectedColumnDefinition("value", typeof(int), false, "integer")
                {
                    ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(identityOperation),
                };
                break;
        }

        var operation = new SafeMigrationOperation(new AlterColumnIntent("integer_rows", target, source),
            caseId == "strict" ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.RepairIfSafe);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = builder.Build(operation, includeTransitionEvidence: true);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, plan.RepairCapability);
        Assert.Null(plan.DataProbe);
    }

    /// <summary>Maps only the three explicitly tested native signed integer representations.</summary>
    private static Type ClrType(string storeType) => storeType switch
    {
        "smallint" => typeof(short),
        "integer" => typeof(int),
        "bigint" => typeof(long),
        _ => throw new ArgumentOutOfRangeException(nameof(storeType)),
    };
}
