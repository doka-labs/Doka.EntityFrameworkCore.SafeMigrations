namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordered SQL Server key limits and NULL uniqueness proofs.</summary>
public sealed class SqlServerProjectedKeyTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=projected_keys;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Uses the projected target width instead of a stale live key width.</summary>
    [Theory]
    [InlineData(true, 450, SafeMigrationObservedState.Missing)]
    [InlineData(true, 451, SafeMigrationObservedState.Unsupported)]
    [InlineData(false, 850, SafeMigrationObservedState.Missing)]
    [InlineData(false, 851, SafeMigrationObservedState.Unsupported)]
    public void KeyWidth_UsesTheOrderedTarget(
        bool primaryKey,
        int length,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(string), false, maxLength: length),
            new SafeMigrationProjectedTableState(true, false, false, false));

        var intent = CreateKeyIntent(primaryKey);
        var missing = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            intent, source, missing, missing, null, context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>A SQL Server primary key cannot use a physically nullable column.</summary>
    [Fact]
    public void PrimaryKey_NullableTargetIsUnsupportedEvenWhenEmpty()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), true, "int"),
            new SafeMigrationProjectedTableState(true, false, false, false));

        var missing = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(true), source, missing, missing, null,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("primary_key_nullable_column", analysis.Code);
    }

    /// <summary>At most one existing row can acquire a NULL in an unfiltered unique key.</summary>
    [Theory]
    [InlineData(-1, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(0, SafeMigrationObservedState.Missing)]
    [InlineData(1, SafeMigrationObservedState.Missing)]
    [InlineData(2, SafeMigrationObservedState.DataBlocked)]
    public void UniqueAddedNullableColumn_UsesSqlServerNullEquality(
        int rowCount,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), true, "int"),
            new SafeMigrationProjectedTableState(false, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, rowCount, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal));

        var live = Analysis(SafeMigrationObservedState.PrerequisiteMissing);
        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(false), source, live, projected, snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>Two rows with a new NULL key are safe when the filter excludes every NULL.</summary>
    [Fact]
    public void UniqueAddedNullableColumn_NullRejectingFilterProvesAnEmptyIndex()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), true, "int"),
            new SafeMigrationProjectedTableState(false, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 2, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal));

        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_keys_Code", "keys",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: new SafeMigrationSqlNullTestExpression(
                new SafeMigrationSqlIdentifierExpression(["Code"]), negated: true)));

        var live = Analysis(SafeMigrationObservedState.PrerequisiteMissing);
        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            intent, source, live, projected, snapshot, context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Borrowed row proofs never survive an earlier unanalysed data change.</summary>
    [Fact]
    public void UniqueAddedNullableColumn_DataMutationInvalidatesTheSingleRowProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), true, "int"),
            new SafeMigrationProjectedTableState(false, true, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 1, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal));

        var live = Analysis(SafeMigrationObservedState.PrerequisiteMissing);
        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(false), source, live, projected, snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_key_data_state_unknown", analysis.Code);
    }

    /// <summary>An existing primary key must have an accepted drop before a replacement can apply.</summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Different)]
    [InlineData(true, SafeMigrationObservedState.Missing)]
    public void PrimaryKeyReplacement_RequiresAnAcceptedDrop(
        bool wasDropped,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(false, false, wasDropped, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 0, "PK_existing",
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal));

        var live = Analysis(SafeMigrationObservedState.Different);
        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(true), source, live, projected, snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>Replacements require explicit target-row evidence rather than a stale Different result.</summary>
    [Theory]
    [InlineData("key_replacement_data_safe", SafeMigrationObservedState.Missing)]
    [InlineData("unique_constraint_replacement_data_blocked", SafeMigrationObservedState.DataBlocked)]
    [InlineData("index_replacement_data_blocked", SafeMigrationObservedState.DataBlocked)]
    [InlineData("classified_different", SafeMigrationObservedState.PrerequisiteMissing)]
    public void UniqueReplacement_RequiresAnExplicitTargetRowProof(
        string code,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(false, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 2, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal)
            {
                ["Code"] = new("int", 4, false, true, true, null, false),
            });

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, code);

        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(false), source, live, projected, snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>An accepted projection cannot make a statically unsupported provider facet valid.</summary>
    [Fact]
    public void InvariantUnsupportedFacet_CannotBePromotedByAnEmptyTableProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(true, false, false, false));

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "index_unproven_facet")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(false), source, live, Analysis(SafeMigrationObservedState.Missing), null,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>Existing physical key widths are revalidated even when the projected key is a replacement.</summary>
    [Theory]
    [InlineData(1700, SafeMigrationObservedState.Missing)]
    [InlineData(1701, SafeMigrationObservedState.Unsupported)]
    public void IndexReplacement_UnchangedLiveColumnsStillRespectThePhysicalLimit(
        int bytes,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Unrelated", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(false, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 0, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal)
            {
                ["Code"] = new("varchar", bytes, false, true, true, null, false),
            });

        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_keys_Code", "keys",
            [new ExpectedIndexKeyDefinition("Code")]));

        var live = Analysis(SafeMigrationObservedState.Different);
        var projected = Analysis(SafeMigrationObservedState.Missing);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            intent, source, live, projected, snapshot, context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>The replacement marker is actionable only after an accepted drop and target-row proof.</summary>
    [Theory]
    [InlineData(true, "key_replacement_data_safe", SafeMigrationObservedState.Missing)]
    [InlineData(true, "primary_key_replacement_data_blocked", SafeMigrationObservedState.DataBlocked)]
    [InlineData(false, "key_replacement_data_safe", SafeMigrationObservedState.PrerequisiteMissing)]
    public void PrimaryKeyReplacement_UnresolvedOriginalDefinitionUsesProviderProof(
        bool wasDropped,
        string code,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(false, false, wasDropped, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 2, "PK_keys",
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal)
            {
                ["Code"] = new("int", 4, false, true, true, null, false),
            });

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, code);

        var projected = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false,
            "projected_primary_key_replacement_unproven");

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(true), source, live, projected, snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>A new NULL in a composite key proves uniqueness only for an empty or single-row table.</summary>
    [Theory]
    [InlineData(0, SafeMigrationObservedState.Missing)]
    [InlineData(1, SafeMigrationObservedState.Missing)]
    [InlineData(2, SafeMigrationObservedState.PrerequisiteMissing)]
    public void CompositeUniqueAddedNullableColumn_NeverAssumesTheOtherColumnsAreDistinct(
        int rowCount,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), true, "int"),
            new SafeMigrationProjectedTableState(false, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, rowCount, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal)
            {
                ["Other"] = new("int", 4, false, true, true, null, false),
            });

        var intent = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("UQ_keys_composite", "keys", ["Code", "Other"]));

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            intent, source, Analysis(SafeMigrationObservedState.PrerequisiteMissing),
            Analysis(SafeMigrationObservedState.Missing), snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
        if (rowCount == 2)
        {
            Assert.Equal("projected_key_data_state_unknown", analysis.Code);
        }
    }

    /// <summary>Recreating a dropped table invalidates its original primary key and row count.</summary>
    [Theory]
    [InlineData("classified_different")]
    [InlineData("primary_key_replacement_data_blocked")]
    public void RecreatedTable_DoesNotRetainItsOriginalPhysicalPrimaryKey(string code)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new ProjectedSource(
            new ExpectedColumnDefinition("Code", typeof(int), false, "int"),
            new SafeMigrationProjectedTableState(true, false, false, false));

        var snapshot = new SqlServerProjectedKeyTable(true, 2, "PK_old_table",
            new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal));

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, code);

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(
            CreateKeyIntent(true), source, live,
            Analysis(SafeMigrationObservedState.Missing), snapshot,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    private static SafeMigrationIntent CreateKeyIntent(bool primaryKey)
        => primaryKey
            ? new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("PK_keys", "keys", ["Code"]))
            : new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("UQ_keys_Code", "keys", ["Code"]));

    private static SafeMigrationProviderAnalysis Analysis(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "test_state");

    private sealed class ProjectedSource(
        ExpectedColumnDefinition definition,
        SafeMigrationProjectedTableState state
    ) : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? result
        )
        {
            result = StringComparer.Ordinal.Equals(column, definition.Name) ? definition : null;

            return result is not null;
        }

        public bool TryGetProjectedTableState(
            string table,
            string? schema,
            out SafeMigrationProjectedTableState result
        )
        {
            result = state;

            return true;
        }
    }
}
