namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Uses strict mode and rejecting policies unless the caller explicitly opts into convergence.</summary>
    [Fact]
    public void OptionsBuilderDefaultsToStrictFailClosedConfiguration()
    {
        // Arrange: No fluent overrides are supplied.

        // Act
        var builder = new SafeMigrationOptionsBuilder();

        // Assert
        Assert.Equal(SafeMigrationScaffoldingMode.Strict, builder.Mode);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, builder.LegacyConvergencePolicy);
        Assert.False(builder.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    /// <summary>Rejects an undefined scaffolding mode without accepting it as an option.</summary>
    [Fact]
    public void OptionsBuilderRejectsUndefinedMode()
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        var exception = Record.Exception(() =>
            builder.UseScaffoldingMode((SafeMigrationScaffoldingMode)int.MaxValue));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Rejects both existence-only and undefined convergence policies.</summary>
    /// <param name="policy">The unsupported policy value.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ExistenceOnly)]
    [InlineData((SafeMigrationPolicy)int.MaxValue)]
    public void OptionsBuilderRejectsUnsupportedPolicy(
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        var exception = Record.Exception(() => builder.UseLegacyConvergencePolicy(policy));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Enables excluded-table data ownership and returns the same fluent builder.</summary>
    [Fact]
    public void OptionsBuilder_EnablesExcludedTableDataOwnershipFluently()
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        var returned = builder.ExcludeModelManagedDataForExcludedTables();

        // Assert
        Assert.Same(builder, returned);
        Assert.True(builder.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    /// <summary>Rejects repair policy whenever fluent configuration ends in strict mode.</summary>
    /// <param name="order">The serializable strict-mode configuration-order identifier.</param>
    [Theory]
    [InlineData("default")]
    [InlineData("strict_first")]
    [InlineData("policy_first")]
    [InlineData("reset_to_strict")]
    public void RepairPolicyRequiresLegacyConvergence(
        string order
    )
    {
        // Arrange
        var builder = order switch
        {
            "default" => new SafeMigrationOptionsBuilder()
                .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe),
            "strict_first" => new SafeMigrationOptionsBuilder()
                .UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict)
                .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe),
            "policy_first" => new SafeMigrationOptionsBuilder()
                .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)
                .UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict),
            "reset_to_strict" => new SafeMigrationOptionsBuilder()
                .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)
                .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)
                .UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown policy configuration order."),
        };

        // Act
        var exception = Record.Exception(builder.Validate);

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("requires LegacyConvergence", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>Accepts explicit repair policy with convergence mode in either fluent call order.</summary>
    /// <param name="modeFirst">Whether convergence mode is selected before repair policy.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepairPolicyAcceptsLegacyConvergenceInEitherCallOrder(
        bool modeFirst
    )
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        if (modeFirst)
        {
            builder.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)
                .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)
                .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);
        }

        builder.Validate();

        // Assert
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, builder.Mode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, builder.LegacyConvergencePolicy);
    }

    /// <summary>Freezes the chosen convergence policy in generated source.</summary>
    /// <param name="policy">The selected convergence policy.</param>
    /// <param name="expectedPolicy">The exact policy expression expected in source.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, "SafeMigrationPolicy.ThrowIfDifferent")]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, "SafeMigrationPolicy.RepairIfSafe")]
    public void LegacyConvergenceGenerationFreezesSelectedPolicyInSource(
        SafeMigrationPolicy policy,
        string expectedPolicy
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.LegacyConvergence,
            legacyConvergencePolicy: policy);

        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [CreateTable()], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".ConvergeTableFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(
            string.Concat(
                ",",
                Environment.NewLine,
                "    policy: global::Doka.EntityFrameworkCore.SafeMigrations.",
                expectedPolicy),
            source,
            StringComparison.Ordinal);
        Assert.EndsWith(");", source, StringComparison.Ordinal);
    }

    /// <summary>Freezes the selected table-creation mode in generated source.</summary>
    /// <param name="mode">The selected scaffolding mode.</param>
    /// <param name="expectedMethod">The exact generated migration entry point.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, ".CreateTableIfNotExists(")]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, ".ConvergeTableFromModel(")]
    public void CreateTableGenerationFreezesSelectedModeInSource(
        SafeMigrationScaffoldingMode mode,
        string expectedMethod
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(mode);
        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [CreateTable()], builder);

        // Assert
        Assert.Contains(expectedMethod, builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.CreateTable(", builder.ToString(), StringComparison.Ordinal);
    }
}
