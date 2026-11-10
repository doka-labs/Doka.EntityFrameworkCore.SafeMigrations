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

    [Fact]
    public void LiteralNull_RemainsDistinctFromNoDefaultInEfOperation()
    {
        var withLiteralNull = Assert.IsType<AddColumnOperation>(
            SafeMigrationStandardOperationFactory.Create(
                new EnsureColumnIntent(
                    "items",
                    new ExpectedColumnDefinition(
                        "value",
                        typeof(string),
                        true,
                        defaultValue: SafeMigrationDefaultValue.Literal(null)))));

        var withoutDefault = Assert.IsType<AddColumnOperation>(
            SafeMigrationStandardOperationFactory.Create(
                new EnsureColumnIntent("items", new ExpectedColumnDefinition("other", typeof(string), true))));

        Assert.Equal("NULL", withLiteralNull.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValueSql);
        Assert.Null(withoutDefault.DefaultValue);
    }

    [Fact]
    public void EnsureColumnRepairUsesAlterOperationWithOnlyMutableOldFacetsChanged()
    {
        var target = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: false,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical",
            defaultValue: SafeMigrationDefaultValue.Literal("fallback"));

        var operation = Assert.IsType<AlterColumnOperation>(
            SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", target)));

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

    [Fact]
    public void EnsureColumnRepairRejectsProviderOwnedDefinitions()
    {
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
        };

        providerOperation["Test:Identity"] = "identity";
        var definition = SafeMigrationExpectedDefinitionFactory.From(providerOperation);

        Assert.Throws<NotSupportedException>(() =>
            SafeMigrationStandardOperationFactory.CreateRepair(new EnsureColumnIntent("items", definition)));
    }

    [Fact]
    public void EnsureColumnRepairAllowsProviderToValidateItsOwnMetadata()
    {
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
        };

        providerOperation["Test:Identity"] = "identity";
        var definition = SafeMigrationExpectedDefinitionFactory.From(providerOperation);

        var repair = Assert.IsType<AlterColumnOperation>(
            SafeMigrationStandardOperationFactory.CreateRepair(
                new EnsureColumnIntent("items", definition),
                providerRepairValidator: static _ => true));

        Assert.Equal("identity", repair["Test:Identity"]);
    }

    [Fact]
    public void EnsureColumnRepairDoesNotLetProviderOverrideIntrinsicExclusions()
    {
        var computed = new ExpectedColumnDefinition(
            "computed_value",
            typeof(int),
            isNullable: false,
            storeType: "int",
            computedColumnSql: "1 + 1",
            isStored: true);

        Assert.Throws<NotSupportedException>(() =>
            SafeMigrationStandardOperationFactory.CreateRepair(
                new EnsureColumnIntent("items", computed),
                providerRepairValidator: static _ => true));
    }
}
