namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks provider-owned eligibility for explicitly declared VARCHAR transitions.</summary>
public sealed class PostgreSqlAlterTransitionTests
{
    /// <summary>Routes supported length changes through the existing guarded row-proof machinery.</summary>
    /// <param name="sourceType">The declared source store type.</param>
    /// <param name="targetType">The declared target store type.</param>
    [Theory]
    [InlineData("character varying(10)", "character varying(40)")]
    [InlineData("varchar(40)", "varchar(10)")]
    [InlineData("character varying", "varchar(10)")]
    public void AlterTransition_RequiresDeclaredSourceAndUsesProviderProofs(
        string sourceType,
        string targetType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, targetType),
            new ExpectedColumnDefinition("value", typeof(string), true, sourceType)),
            SafeMigrationPolicy.RepairIfSafe);

        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = builder.Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.NotNull(plan.DataProbe);
        Assert.NotNull(plan.NullabilityDataProbe);
        Assert.Contains("pg_catalog.format_type", plan.DataProbe.TransitionInvariantExpression,
            StringComparison.Ordinal);
        Assert.Contains("NOT a.attnotnull", plan.DataProbe.TransitionInvariantExpression, StringComparison.Ordinal);
        Assert.Contains("column_max_length", plan.DiagnosticEvidenceExpression, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, plan.RepairOperationalImpact);
        Assert.Contains("char_length", Assert.Single(commands).CommandText, StringComparison.Ordinal);
    }

    /// <summary>Keeps unrelated type families and non-repair policies outside the transition gate.</summary>
    /// <param name="sourceType">The declared source type.</param>
    /// <param name="policy">The requested decision policy.</param>
    [Theory]
    [InlineData("text", SafeMigrationPolicy.RepairIfSafe)]
    [InlineData("character(10)", SafeMigrationPolicy.RepairIfSafe)]
    [InlineData("character varying(10)", SafeMigrationPolicy.ThrowIfDifferent)]
    public void AlterTransition_UnsupportedOrStrictHasNoRowProbe(
        string sourceType,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(40)"),
            new ExpectedColumnDefinition("value", typeof(string), true, sourceType)), policy);

        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = builder.Build(operation, includeTransitionEvidence: true);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, plan.RepairCapability);
        Assert.Null(plan.DataProbe);
        Assert.Null(plan.NullabilityDataProbe);
        Assert.False(plan.MayRequireNullabilityDataProof);
    }

    /// <summary>Does not add length-probe work to an existing same-type mutable-facet alteration.</summary>
    [Fact]
    public void AlterTransition_SameTypeRepairDoesNotAcquireLengthProbe()
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Host=localhost;Database=unused;Username=unused");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)", comment: "next"),
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)")),
            SafeMigrationPolicy.RepairIfSafe);

        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = builder.Build(operation, includeTransitionEvidence: true);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Null(plan.DataProbe);
        Assert.Null(plan.NullabilityDataProbe);
    }
}
