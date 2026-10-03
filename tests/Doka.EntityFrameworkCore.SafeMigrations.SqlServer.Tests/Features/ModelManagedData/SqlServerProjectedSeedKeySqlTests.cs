namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that authored key proofs use SQL Server typed and collated equality.</summary>
public sealed class SqlServerProjectedSeedKeySqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=seed_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Structured filters bind to typed row aliases and affect candidate collisions, not primary-key identity.
    /// </summary>
    [Fact]
    public void AuthoredFilteredRows_BindColumnNodesBeforeCandidateGrouping()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)"),
        };

        var filter = SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Code"));

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([Seed(1, null), Seed(2, null)], columns, ["Code"],
            requireCapturedUniqueKey: false, filter);

        // Assert
        Assert.NotNull(sql);
        Assert.Contains("GROUP BY [k0] HAVING COUNT_BIG(*) > 1", sql);
        Assert.Contains("WHERE (([f0] IS NOT NULL)) GROUP BY [u0] HAVING COUNT_BIG(*) > 1", sql);
        Assert.DoesNotContain("WHERE (([Code] IS NOT NULL))", sql);
    }

    /// <summary>An unknown filter identifier cannot be assigned a typed projected destination alias.</summary>
    [Fact]
    public void AuthoredFilteredRows_UnknownDestinationReferenceRemainsUnproven()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)"),
        };

        var filter = SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Unknown"));

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([Seed(1, null)], columns, ["Code"],
            requireCapturedUniqueKey: false, filter);

        // Assert
        Assert.Null(sql);
    }

    /// <summary>Unions batches and deduplicates identical primary-key targets before checking uniqueness.</summary>
    [Fact]
    public void AuthoredRows_UseTypedCollatedSqlAndWholeLineage()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)",
                collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS")),
        };

        var first = Seed(1, "a");
        var second = Seed(2, "A");

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([first, second], columns, ["Code"]);

        // Assert
        Assert.NotNull(sql);
        Assert.Contains("CAST(1 AS int)", sql);
        Assert.Contains("CAST(2 AS int)", sql);
        Assert.Contains("COLLATE Latin1_General_100_CI_AS", sql);
        Assert.Contains("SELECT DISTINCT", sql);
        Assert.Contains("GROUP BY [k0] HAVING COUNT_BIG(*) > 1", sql);
        Assert.Contains("GROUP BY [u0] HAVING COUNT_BIG(*) > 1", sql);
        Assert.DoesNotContain("OBJECT_ID", sql);
    }

    /// <summary>A proof cannot reinterpret captured seed values using a different destination store type.</summary>
    [Fact]
    public void AuthoredRows_CapturedStoreTypeMismatchHasNoProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(30)"),
        };

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([Seed(1, "a")], columns, ["Code"]);

        // Assert
        Assert.Null(sql);
    }

    /// <summary>Authored ANSI proofs retain the runtime's non-ASCII default-collation requirement.</summary>
    [Fact]
    public void AuthoredAnsiRows_RetainTheRuntimeCodePageGuard()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), false, "varchar(20)", isUnicode: false,
                collation: new SafeMigrationCollationIdentifier("Latin1_General_100_BIN2")),
        };

        var seed = new EnsureModelManagedDataIntent("seed_proofs", ["Id"], ["int"],
            ["Id", "Code"], ["int", "varchar(20)"], new object?[,] { { 1, "\u00e9" } },
            schema: null, uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([seed], columns, ["Code"]);

        // Assert
        Assert.NotNull(sql);
        Assert.Contains("DATABASEPROPERTYEX(DB_NAME(), ''Collation'')", sql);
        Assert.Contains("TRY_CAST(N''", sql);
        Assert.Contains("AS varchar(20)) IS NOT NULL", sql);
        Assert.Contains("CONVERT(nvarchar(max)", sql);
    }

    /// <summary>
    /// Omitted columns require a nullable or provably generated destination value, not default presence.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    public void AuthoredRows_OmittedDestinationValueMustBeProven(
        bool nullable,
        bool hasDefault,
        bool nonNullDefault,
        bool expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // WHY: Core rejects literal NULL for a required column at construction.
        // SQL DEFAULT NULL remains an authored expression, so the provider must
        // independently reject its omitted required-value proof.
        var defaultValue = !hasDefault ? null
            : nonNullDefault ? SafeMigrationDefaultValue.Literal(7)
            : nullable ? SafeMigrationDefaultValue.Literal(null) : SafeMigrationDefaultValue.Sql("NULL");

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)"),
            new ExpectedColumnDefinition("RequiredExtra", typeof(int), nullable, "int", defaultValue: defaultValue),
        };

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([Seed(1, "a")], columns, ["Code"]);

        // Assert
        Assert.Equal(expected, sql is not null);
    }

    /// <summary>
    /// Provider-proven temporal built-ins supply omitted required values without weakening opaque defaults.
    /// </summary>
    [Theory]
    [InlineData("GETDATE()", true)]
    [InlineData("CURRENT_TIMESTAMP", true)]
    [InlineData("CAST(NULL AS datetime2(7))", false)]
    [InlineData("unknown_default()", false)]
    public void AuthoredRows_OmittedTemporalDefaultUsesTheProviderProof(
        string defaultSql,
        bool expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)"),
            new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime2(7)",
                defaultValue: SafeMigrationDefaultValue.Sql(defaultSql)),
        };

        // Act
        var sql = builder.BuildProjectedSeedKeyProofSql([Seed(1, "a")], columns, ["Code"]);

        // Assert
        Assert.Equal(expected, sql is not null);
    }

    /// <summary>Shared model-managed key and value columns cannot carry different store-type contracts.</summary>
    [Fact]
    public void AuthoredRows_ConflictingKeyTypeIsRejectedByTheImmutableIntent()
    {
        // Arrange
        static EnsureModelManagedDataIntent CreateConflictingTypes()
            => new("seed_proofs", ["Id"], ["bigint"], ["Id", "Code"], ["int", "nvarchar(20)"],
                new object?[,] { { 1, "one" } }, schema: null,
                uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);

        // Act
        var exception = Record.Exception(CreateConflictingTypes);

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static EnsureModelManagedDataIntent Seed(
        int id,
        string? code
    )
        => new("seed_proofs", ["Id"], ["int"],
            ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { id, code } },
            schema: null, uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
}
