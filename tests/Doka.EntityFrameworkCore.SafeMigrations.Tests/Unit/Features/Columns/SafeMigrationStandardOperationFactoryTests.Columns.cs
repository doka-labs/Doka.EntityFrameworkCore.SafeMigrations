namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    /// <summary>A provider rejection never falls back to the annotation-free default repair decision.</summary>
    /// <param name="hasAnnotations">Whether the definition carries provider-owned metadata.</param>
    /// <param name="approval">The provider decision, or null when no provider validator is supplied.</param>
    /// <param name="expectedAllowed">Whether Core may construct the repair operation.</param>
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ProviderRepairValidationPreservesFalseAndAbsentDecisions(
        bool hasAnnotations,
        bool? approval,
        bool expectedAllowed
    )
    {
        // Arrange
        var source = new AddColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
        };

        if (hasAnnotations)
        {
            source.AddAnnotation("Test:Metadata", "owned");
        }

        var target = SafeMigrationExpectedDefinitionFactory.From(source);
        var calls = 0;
        ExpectedColumnDefinition? observed = null;
        Func<ExpectedColumnDefinition, bool>? validator = approval is { } accepted
            ? definition =>
            {
                calls++;
                observed = definition;

                return accepted;
            }
            : null;

        MigrationOperation? repair = null;

        // Act
        var exception = Record.Exception(() =>
        {
            repair = SafeMigrationStandardOperationFactory.CreateRepair(
                new EnsureColumnIntent("items", target),
                providerRepairValidator: validator);
        });

        // Assert
        Assert.Equal(approval.HasValue
                ? 1
                : 0,
            calls);

        if (approval.HasValue)
        {
            Assert.Same(target, observed);
        }
        else
        {
            Assert.Null(observed);
        }

        if (expectedAllowed)
        {
            Assert.Null(exception);
            Assert.IsType<AlterColumnOperation>(repair);
        }
        else
        {
            Assert.IsType<NotSupportedException>(exception);
            Assert.Null(repair);
        }
    }

    /// <summary>
    /// Reconstructs literal NULL as explicit SQL NULL while leaving an absent default absent in the ordinary EF
    /// operation.
    /// </summary>
    [Fact]
    public void LiteralNull_RemainsDistinctFromNoDefaultInEfOperation()
    {
        // Arrange
        var nullIntent = new EnsureColumnIntent(
            "items",
            new ExpectedColumnDefinition("value",
                typeof(string),
                true,
                defaultValue: SafeMigrationDefaultValue.Literal(null)));

        var absentIntent = new EnsureColumnIntent("items", new ExpectedColumnDefinition("other", typeof(string), true));

        // Act
        var nullOperation = SafeMigrationStandardOperationFactory.Create(nullIntent);
        var absentOperation = SafeMigrationStandardOperationFactory.Create(absentIntent);

        // Assert
        var withLiteralNull = Assert.IsType<AddColumnOperation>(nullOperation);
        var withoutDefault = Assert.IsType<AddColumnOperation>(absentOperation);

        Assert.Equal("NULL", withLiteralNull.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValue);
    }

    /// <summary>
    /// Declares only safely mutable old facets while retaining the target's storage metadata and default.
    /// </summary>
    [Fact]
    public void EnsureColumnRepairUsesAlterOperationWithOnlyMutableOldFacetsChanged()
    {
        // Arrange
        var target = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: false,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical",
            defaultValue: SafeMigrationDefaultValue.Literal("fallback"));

        // Act
        var result = SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", target));

        // Assert
        var operation = Assert.IsType<AlterColumnOperation>(result);

        Assert.Equal("items", operation.Table);
        Assert.Equal("value", operation.Name);
        Assert.False(operation.IsNullable);
        Assert.Equal("canonical", operation.Comment);
        Assert.Equal("fallback", operation.DefaultValue);
        Assert.True(operation.OldColumn.IsNullable);
        Assert.Null(operation.OldColumn.Comment);
        Assert.Null(operation.OldColumn.DefaultValue);
        Assert.Null(operation.OldColumn.DefaultValueSql);
        Assert.Equal(operation.ColumnType, operation.OldColumn.ColumnType);
        Assert.Equal(operation.MaxLength, operation.OldColumn.MaxLength);
    }

    /// <summary>
    /// Rejects an annotated column repair when no owning provider validator has approved the preserved metadata.
    /// </summary>
    [Fact]
    public void EnsureColumnRepairRejectsProviderOwnedDefinitions()
    {
        // Arrange
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
            ["Test:Identity"] = "identity",
        };

        var definition = SafeMigrationExpectedDefinitionFactory.From(providerOperation);

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", definition)));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>
    /// Retains the exact provider annotation after the owning validator explicitly approves a repair.
    /// </summary>
    [Fact]
    public void EnsureColumnRepairAllowsProviderToValidateItsOwnMetadata()
    {
        // Arrange
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
            ["Test:Identity"] = "identity",
        };

        var definition = SafeMigrationExpectedDefinitionFactory.From(providerOperation);

        // Act
        var result = SafeMigrationStandardOperationFactory.CreateRepair(
            new EnsureColumnIntent("items", definition),
            providerRepairValidator: static _ => true);

        // Assert
        var repair = Assert.IsType<AlterColumnOperation>(result);

        Assert.Equal("identity", repair["Test:Identity"]);
    }

    /// <summary>
    /// Keeps computed-column repair excluded even when a provider validator returns unconditional approval.
    /// </summary>
    [Fact]
    public void EnsureColumnRepairDoesNotLetProviderOverrideIntrinsicExclusions()
    {
        // Arrange
        var computed = new ExpectedColumnDefinition(
            "computed_value",
            typeof(int),
            isNullable: false,
            storeType: "int",
            computedColumnSql: "1 + 1",
            isStored: true);

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationStandardOperationFactory.CreateRepair(
                new EnsureColumnIntent("items", computed),
                providerRepairValidator: static _ => true));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }
}
