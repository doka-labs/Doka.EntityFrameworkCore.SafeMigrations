namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies typed filtered-index proofs before projected empty-table and nullable-column promotions.</summary>
public sealed class SqlServerProjectedIndexFilterProofTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=filter_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Repeated filters reuse one provider command, including rejected and unproven results.</summary>
    [Theory]
    [InlineData(-1, SafeMigrationObservedState.Unsupported)]
    [InlineData(0, SafeMigrationObservedState.Unsupported)]
    [InlineData(1, SafeMigrationObservedState.Missing)]
    public async Task RepeatedAuthoredFilter_UsesOneCommandAndExactColumnReferences(
        int result,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var column = new ExpectedColumnDefinition("Code", typeof(int), true, "int");
        var table = new ExpectedTableDefinition("filters", [column]);
        var operations = new List<SafeMigrationOperation>
        {
            new(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
        };

        for (var ordinal = 0; ordinal < 100; ordinal++)
        {
            operations.Add(Index("IX_filters_" + ordinal));
        }

        var commands = new List<string>();
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            commands.Add(sql);

            return Task.FromResult(result);
        }

        // Act
        await analyzer.CaptureProjectedSeedProofsAsync(operations, Execute, CancellationToken.None);
        var analysis = ((ISafeMigrationProjectedKeyAnalyzer)analyzer).ValidateProjectedIndex(
            (EnsureIndexIntent)operations[^1].Intent, new Source(column), Missing(), Missing());

        // Assert
        Assert.Single(commands);
        Assert.Contains("TRY_CAST(N''1'' AS int) IS NOT NULL", commands[0]);
        Assert.DoesNotContain("FROM [dbo].[filters]", commands[0]);
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>
    /// A changed target column cannot borrow the earlier filter proof even when its name is unchanged.
    /// </summary>
    [Fact]
    public async Task ChangedAuthoredFilterColumn_DoesNotReusePriorTypeProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var initial = new ExpectedColumnDefinition("Code", typeof(int), true, "int");
        var replacement = new ExpectedColumnDefinition("Code", typeof(long), true, "bigint");
        var first = Index("IX_filters_first");
        var second = Index("IX_filters_second");
        var operations = new SafeMigrationOperation[]
        {
            new(new EnsureTableIntent(new ExpectedTableDefinition("filters", [initial]),
                SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            first,
            new(new AlterColumnIntent("filters", replacement, initial), SafeMigrationPolicy.RepairIfSafe),
            second,
        };

        var commands = new List<string>();
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            commands.Add(sql);

            return Task.FromResult(1);
        }

        // Act
        await analyzer.CaptureProjectedSeedProofsAsync(operations, Execute, CancellationToken.None);
        var stale = ((ISafeMigrationProjectedKeyAnalyzer)analyzer).ValidateProjectedIndex(
            (EnsureIndexIntent)first.Intent, new Source(replacement), Missing(), Missing());

        var current = ((ISafeMigrationProjectedKeyAnalyzer)analyzer).ValidateProjectedIndex(
            (EnsureIndexIntent)second.Intent, new Source(replacement), Missing(), Missing());

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.Contains("AS int)", commands[0]);
        Assert.Contains("AS bigint)", commands[1]);
        Assert.Equal(SafeMigrationObservedState.Unsupported, stale.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, current.ObservedState);
    }

    /// <summary>A physical filtered index cannot convert the column side even when SELECT could evaluate it.</summary>
    [Theory]
    [InlineData("int", "1", true)]
    [InlineData("nvarchar(20)", 1, false)]
    [InlineData("nvarchar(20)", "1", true)]
    public void FilterTypePrecedence_PreservesThePhysicalColumnSide(
        string storeType,
        object value,
        bool expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var literal = new SafeMigrationSqlLiteralExpression(value);

        // Act
        var supported = builder.IsIndexFilterLiteralCompatible(literal, storeType);

        // Assert
        Assert.Equal(expected, supported);
    }

    private static SafeMigrationOperation Index(string name)
        => new(new EnsureIndexIntent(new ExpectedIndexDefinition(name, "filters",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Code"),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("1")))),
            SafeMigrationPolicy.ThrowIfDifferent);

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationProviderAnalysis Missing()
        => new(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, false, "missing");

    private sealed class Source(ExpectedColumnDefinition column)
        : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string name,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = StringComparer.Ordinal.Equals(column.Name, name) ? column : null;

            return definition is not null;
        }

        public bool TryGetProjectedTableState(
            string table,
            string? schema,
            out SafeMigrationProjectedTableState state
        )
        {
            state = new SafeMigrationProjectedTableState(true, false, false, false);

            return true;
        }
    }
}
