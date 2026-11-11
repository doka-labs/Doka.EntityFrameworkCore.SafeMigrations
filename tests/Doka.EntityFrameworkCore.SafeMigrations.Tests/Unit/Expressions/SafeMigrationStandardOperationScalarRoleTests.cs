namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Separates provider predicate syntax from scalar column expressions during EF reconstruction.</summary>
public sealed class SafeMigrationStandardOperationScalarRoleTests
{
    /// <summary>Every column reconstruction path uses the scalar role when the provider supplies it.</summary>
    /// <param name="mode">The column creation, alteration or repair path under test.</param>
    [Theory]
    [InlineData("add_default")]
    [InlineData("add_computed")]
    [InlineData("alter_default")]
    [InlineData("alter_computed")]
    [InlineData("repair")]
    [InlineData("store_type_repair")]
    [InlineData("full_definition_repair")]
    public void ColumnPathsUseScalarRenderer(
        string mode
    )
    {
        // Arrange
        Func<SafeMigrationSqlExpression, string> predicate = static _ => "predicate";
        Func<SafeMigrationSqlExpression, string> scalar = static _ => "scalar";

        // Act
        var operation = ColumnOperation(mode, predicate, scalar);

        // Assert
        Assert.Equal("scalar",
            mode.EndsWith("computed", StringComparison.Ordinal)
                ? operation.ComputedColumnSql
                : operation.DefaultValueSql);

        if (mode == "alter_computed")
        {
            Assert.Equal("scalar",
                Assert.IsType<AlterColumnOperation>(operation)
                    .OldColumn.ComputedColumnSql);
        }
    }

    /// <summary>Providers without distinct scalar syntax retain their existing expression callback contract.</summary>
    /// <param name="mode">The column creation, alteration or repair path under test.</param>
    [Theory]
    [InlineData("add_default")]
    [InlineData("add_computed")]
    [InlineData("alter_default")]
    [InlineData("alter_computed")]
    [InlineData("repair")]
    [InlineData("store_type_repair")]
    [InlineData("full_definition_repair")]
    public void ScalarRoleFallsBackToExistingRenderer(
        string mode
    )
    {
        // Arrange
        Func<SafeMigrationSqlExpression, string> legacy = static _ => "legacy";

        // Act
        var operation = ColumnOperation(mode, legacy, scalar: null);

        // Assert
        Assert.Equal("legacy",
            mode.EndsWith("computed", StringComparison.Ordinal)
                ? operation.ComputedColumnSql
                : operation.DefaultValueSql);
    }

    /// <summary>Table column roots are scalar while table CHECK roots remain predicates.</summary>
    [Fact]
    public void TableColumnsAndChecksKeepDistinctRoles()
    {
        // Arrange
        var expression = SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("source"));
        var definition = new ExpectedTableDefinition("items",
            [
                new ExpectedColumnDefinition("source", typeof(int), true),
                new ExpectedColumnDefinition("fallback",
                    typeof(bool),
                    false,
                    defaultValue: SafeMigrationDefaultValue.Sql(expression)),
                new ExpectedColumnDefinition("flag",
                    typeof(bool),
                    false,
                    computedExpression: expression,
                    isStored: false),
            ],
            checkConstraints: [ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items", expression)]);

        // Act
        var operation = SafeMigrationStandardOperationFactory.Create(
            new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
            static _ => "predicate",
            renderScalarExpression: static _ => "scalar");

        // Assert
        var table = Assert.IsType<CreateTableOperation>(operation);
        Assert.Equal("scalar", table.Columns[1].DefaultValueSql);
        Assert.Equal("scalar", table.Columns[2].ComputedColumnSql);
        Assert.Equal("predicate",
            Assert.Single(table.CheckConstraints)
                .Sql);
    }

    /// <summary>CHECK and partial-index roots must not be converted to scalar expressions.</summary>
    /// <param name="index">Whether to reconstruct a partial index rather than a standalone CHECK.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PredicateRootsNeverUseScalarRenderer(
        bool index
    )
    {
        // Arrange
        var expression = SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("source"));
        SafeMigrationIntent intent = index
            ? new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items",
                "items",
                [new ExpectedIndexKeyDefinition("source")],
                structuredFilter: expression))
            : new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression("ck_items",
                "items",
                expression));

        // Act
        var operation = SafeMigrationStandardOperationFactory.Create(intent,
            static _ => "predicate",
            renderScalarExpression: static _ => throw new InvalidOperationException("A predicate is not a scalar."));

        // Assert
        Assert.Equal("predicate",
            operation is CreateIndexOperation createdIndex
                ? createdIndex.Filter
                : Assert.IsType<AddCheckConstraintOperation>(operation)
                    .Sql);
    }

    /// <summary>Preserved authored scalar SQL is not reinterpreted by either structured callback.</summary>
    /// <param name="computed">Whether the captured native source belongs to a computed column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredSqlBypassesScalarAndPredicateRenderers(
        bool computed
    )
    {
        // Arrange
        const string sql = "native_expression";
        var definition = computed
            ? new ExpectedColumnDefinition("value", typeof(int), true, computedColumnSql: sql, isStored: false)
            : new ExpectedColumnDefinition("value",
                typeof(int),
                true,
                defaultValue: SafeMigrationDefaultValue.Sql(sql));

        // Act
        var operation = SafeMigrationStandardOperationFactory.Create(new EnsureColumnIntent("items", definition),
            static _ => throw new InvalidOperationException("Authored SQL is not a portable predicate."),
            renderScalarExpression: static _ => throw new InvalidOperationException("Authored SQL stays native."));

        // Assert
        var column = Assert.IsType<AddColumnOperation>(operation);
        Assert.Same(sql,
            computed
                ? column.ComputedColumnSql
                : column.DefaultValueSql);
    }

    /// <summary>Uses the same immutable target across every existing column-factory entry.</summary>
    private static ColumnOperation ColumnOperation(
        string mode,
        Func<SafeMigrationSqlExpression, string> predicate,
        Func<SafeMigrationSqlExpression, string>? scalar
    )
    {
        var expression = SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("source"));
        var target = mode.EndsWith("computed", StringComparison.Ordinal)
            ? new ExpectedColumnDefinition("value",
                typeof(bool),
                false,
                computedExpression: expression,
                isStored: false)
            : new ExpectedColumnDefinition("value",
                typeof(bool),
                false,
                defaultValue: SafeMigrationDefaultValue.Sql(expression));

        var ensure = new EnsureColumnIntent("items", target);
        var operation = mode switch
        {
            "add_default" or "add_computed" => SafeMigrationStandardOperationFactory.Create(ensure,
                predicate,
                renderScalarExpression: scalar),
            "alter_default" or "alter_computed" => SafeMigrationStandardOperationFactory.Create(
                new AlterColumnIntent("items", target, target),
                predicate,
                renderScalarExpression: scalar),
            "repair" => SafeMigrationStandardOperationFactory.CreateRepair(ensure,
                predicate,
                renderScalarExpression: scalar),
            "store_type_repair" => SafeMigrationStandardOperationFactory.CreateStoreTypeRepair(ensure,
                "int",
                predicate,
                renderScalarExpression: scalar),
            "full_definition_repair" => SafeMigrationStandardOperationFactory.CreateFullDefinitionRepair(ensure,
                predicate,
                renderScalarExpression: scalar),
            _ => throw new InvalidOperationException("Unknown test path."),
        };

        return (ColumnOperation)operation;
    }
}
