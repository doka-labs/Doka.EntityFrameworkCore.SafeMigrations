namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact member-domain storage certification without changing native FK acceptance rules.</summary>
public sealed class MySqlForeignKeyReplacementStorageProofTests
{
    /// <summary>ENUM/SET domains compare exactly while ordinary type spelling remains case-insensitive.</summary>
    /// <param name="mariaDb">Whether the offline context uses MariaDB metadata.</param>
    /// <param name="sourceExists">Whether the original catalog must contain the requested physical FK.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MemberDomainsRetainExactBinaryEndpointStorage(bool mariaDb, bool sourceExists)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var builder = new MySqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedForeignKeyDefinition(
            "fk_owner", "child_items", ["owner"], "parent_items", ["id"]);

        // Act
        var certificate = builder.BuildForeignKeyReplacementProof(definition, sourceExists);

        // Assert
        Assert.Contains("CASE WHEN dependent_column.DATA_TYPE IN ('enum', 'set') "
            + "OR principal_column.DATA_TYPE IN ('enum', 'set')", certificate, StringComparison.Ordinal);
        Assert.Contains(
            "THEN CAST(dependent_column.COLUMN_TYPE AS BINARY) = CAST(principal_column.COLUMN_TYPE AS BINARY)",
            certificate, StringComparison.Ordinal);
        Assert.Contains("ELSE LOWER(dependent_column.COLUMN_TYPE) = LOWER(principal_column.COLUMN_TYPE) END",
            certificate, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(dependent_column.COLUMN_TYPE) = LOWER(principal_column.COLUMN_TYPE) AND",
            certificate, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `child_items`", certificate, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `parent_items`", certificate, StringComparison.Ordinal);
    }

    /// <summary>The member-domain comparator retains complete candidate-key and collation requirements.</summary>
    /// <param name="mariaDb">Whether the offline context uses MariaDB metadata.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemberDomainStorageProofRetainsOtherPhysicalRequirements(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var builder = new MySqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedForeignKeyDefinition(
            "fk_owner", "child_items", ["first_owner", "second_owner"], "parent_items", ["first_id", "second_id"]);

        // Act
        var certificate = builder.BuildForeignKeyReplacementProof(definition, sourceExists: false);

        // Assert
        Assert.Contains("dependent_column.COLLATION_NAME <=> principal_column.COLLATION_NAME", certificate,
            StringComparison.Ordinal);
        Assert.Contains("COALESCE(dependent_column.GENERATION_EXPRESSION, '') = ''", certificate,
            StringComparison.Ordinal);
        Assert.Contains("COALESCE(principal_column.GENERATION_EXPRESSION, '') = ''", certificate,
            StringComparison.Ordinal);
        Assert.Contains("principal_key.NON_UNIQUE = 0", certificate, StringComparison.Ordinal);
        Assert.Contains("HAVING COUNT(*) = 2", certificate, StringComparison.Ordinal);
        Assert.Contains("principal_key.SEQ_IN_INDEX = 1", certificate, StringComparison.Ordinal);
        Assert.Contains("principal_key.SEQ_IN_INDEX = 2", certificate, StringComparison.Ordinal);
        Assert.Contains("principal_key.SUB_PART IS NULL", certificate, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", certificate, StringComparison.Ordinal);
    }

    /// <summary>Creates an offline provider context without opening a database connection.</summary>
    /// <param name="mariaDb">Whether MariaDB feature metadata applies.</param>
    /// <returns>The disposable SQL-generation context.</returns>
    private static DbContext CreateContext(bool mariaDb)
    {
        var version = mariaDb
            ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
            : MySqlServerVersion.MySql(new Version(8, 4, 11));

        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                version)
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }
}
