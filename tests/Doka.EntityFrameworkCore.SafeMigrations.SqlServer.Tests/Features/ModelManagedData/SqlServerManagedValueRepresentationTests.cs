namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies scalar domain boundaries and shared guarded conversion before any typed data relation.</summary>
public sealed class SqlServerManagedValueRepresentationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=value_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Gets documented positive and negative temporal and numeric representability boundaries.</summary>
    public static TheoryData<string, string, bool> DomainCases() => new()
    {
        { "datetime", "date-min", false },
        { "datetime", "datetime-lower", true },
        { "datetime", "datetime-upper", true },
        { "datetime", "date-max", false },
        { "smalldatetime", "smalldatetime-underflow", false },
        { "smalldatetime", "smalldatetime-lower", true },
        { "smalldatetime", "smalldatetime-upper", true },
        { "smalldatetime", "smalldatetime-overflow", false },
        { "datetime2(7)", "date-min", true },
        { "datetime2(7)", "date-max", true },
        { "datetime2(3)", "date-max", false },
        { "date", "date-only-min", true },
        { "time(7)", "time-negative", false },
        { "time(7)", "time-day", false },
        { "time(7)", "time-zero", true },
        { "tinyint", "byte-max", true },
        { "tinyint", "byte-overflow", false },
        { "decimal(3,2)", "decimal-rounded-fit", true },
        { "decimal(3,2)", "decimal-rounded-overflow", false },
        { "money", "money-max", true },
        { "money", "money-overflow", false },
        { "real", "double-max", false },
        { "float", "nan", false },
        { "float", "infinity", false },
        { "float", "finite", true },
    };

    /// <summary>Rejects known scalar overflows while preserving valid minimum and rounded boundary values.</summary>
    /// <param name="storeType">The native destination domain.</param>
    /// <param name="source">The serializable identity of the exact CLR boundary value.</param>
    /// <param name="expected">Whether the scalar is representable in the native domain.</param>
    [Theory]
    [MemberData(nameof(DomainCases))]
    public void ScalarDomain_UsesBuiltInSqlServerRanges(
        string storeType,
        string source,
        bool expected
    )
    {
        // Arrange
        // WHY: Primitive case identities are discoverable without serializing heterogeneous boxed CLR values.
        var candidate = DomainValue(source);

        // Act
        var representable = SqlServerSafeMigrationCatalogSqlBuilder.IsManagedValueRepresentable(candidate, storeType);

        // Assert
        Assert.Equal(expected, representable);
    }

    /// <summary>Creates the exact CLR type and value identified by one discoverable scalar case.</summary>
    /// <param name="source">The closed synthetic case identity.</param>
    /// <returns>The original scalar boundary value, retaining its CLR representation.</returns>
    private static object DomainValue(
        string source
    ) => source switch
    {
        "date-min" => DateTime.MinValue,
        "date-max" => DateTime.MaxValue,
        "datetime-lower" => new DateTime(1753, 1, 1),
        "datetime-upper" => new DateTime(9999, 12, 31, 23, 59, 59, 997),
        "smalldatetime-underflow" => new DateTime(1899, 12, 31),
        "smalldatetime-lower" => new DateTime(1900, 1, 1),
        "smalldatetime-upper" => new DateTime(2079, 6, 6, 23, 59, 29, 998),
        "smalldatetime-overflow" => new DateTime(2079, 6, 6, 23, 59, 30),
        "date-only-min" => DateOnly.MinValue,
        "time-negative" => TimeSpan.FromHours(-1),
        "time-day" => TimeSpan.FromDays(1),
        "time-zero" => TimeSpan.Zero,
        "byte-max" => 255,
        "byte-overflow" => 256,
        "decimal-rounded-fit" => 9.994m,
        "decimal-rounded-overflow" => 9.995m,
        "money-max" => 922337203685477.5807m,
        "money-overflow" => 922337203685477.5808m,
        "double-max" => double.MaxValue,
        "nan" => double.NaN,
        "infinity" => double.PositiveInfinity,
        "finite" => 1.5d,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>
    /// Unrepresentable authored dates become invariant Unsupported before any CAST-bearing plan is generated.
    /// </summary>
    [Theory]
    [InlineData("datetime")]
    [InlineData("smalldatetime")]
    public void ManagedTemporalOverflow_IsUnsupportedBeforeRelations(
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var intent = new EnsureModelManagedDataIntent(
            "dates",
            ["Id"],
            ["int"],
            ["Id", "Date"],
            ["int", storeType],
            new object?[,] { { 1, DateTime.MinValue } },
            null,
            null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("model_managed_value_range", plan.UnsupportedCode);
        Assert.DoesNotContain("CAST(", plan.StateExpression);
    }

    /// <summary>
    /// Both live-plan and authored-row proofs guard valid typed values before their actual CAST evaluation.
    /// </summary>
    [Fact]
    public void ManagedTypedValue_UsesTheSameConversionGuardForLiveAndProjectedPlans()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var intent = new EnsureModelManagedDataIntent(
            "dates",
            ["Id"],
            ["int"],
            ["Id", "Date"],
            ["int", "datetime2(7)"],
            new object?[,] { { 1, DateTime.MinValue } },
            null,
            null);

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Date", typeof(DateTime), false, "datetime2(7)"),
        };

        // Act
        var guard = catalog.ManagedValueRepresentationGuard(DateTime.MinValue, "datetime2(7)");
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var projected = catalog.BuildProjectedSeedKeyProofSql([intent], columns, ["Id"]);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("TRY_CAST(TRY_CAST(", guard);
        Assert.DoesNotContain("TRY_CAST(CAST(", guard);
        Assert.Contains(guard, plan.StateEvaluationGuardExpression);
        Assert.Contains(guard.Replace("'", "''", StringComparison.Ordinal), projected);
    }

    /// <summary>Converts CLR dates from their natural temporal type instead of legacy text parsing.</summary>
    [Theory]
    [InlineData("datetime", 1753)]
    [InlineData("smalldatetime", 1900)]
    public void ManagedLegacyTemporalValue_QualifiesTheNaturalTypedOperand(
        string storeType,
        int year
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var value = new DateTime(year, 1, 1);

        // Act
        var guard = catalog.ManagedValueRepresentationGuard(value, storeType);
        var overflowGuard = catalog.ManagedValueRepresentationGuard(DateTime.MinValue, storeType);

        // Assert
        Assert.StartsWith("TRY_CAST(TRY_CAST('", guard, StringComparison.Ordinal);
        Assert.Contains(" AS datetime2) AS " + storeType + ") IS NOT NULL", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("TRY_CAST(CAST(", guard, StringComparison.Ordinal);
        Assert.Equal("1 = 0", overflowGuard);
    }

    /// <summary>
    /// Large same-family LOB values remain supported instead of hitting TRY_CAST's input-length limitation.
    /// </summary>
    [Theory]
    [InlineData("nvarchar(max)")]
    [InlineData("varchar(max)")]
    public void ManagedLargeCharacterValue_RemainsSupported(
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var value = new string('a', 4_001);

        // Act
        var guard = catalog.ManagedValueRepresentationGuard(value, storeType);

        // Assert
        Assert.Equal("1 = 1", guard);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder Catalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
