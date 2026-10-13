namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies modern binary catalog casts without changing native identity qualification.</summary>
public sealed class MySqlBinaryCatalogComparisonTests
{
    /// <summary>Both native owner branches use length-free casts and reject an unknown table-name mode.</summary>
    [Fact]
    public void NativeOwnerComparisonKeepsExactAndNativeFoldedBranches()
    {
        // Arrange
        const string catalog = "physical_owner.TABLE_NAME";
        const string expected = "source_owner.owner";

        // Act
        var sql = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(catalog, expected);

        // Assert
        Assert.Equal("(CASE WHEN @@lower_case_table_names = 0 THEN "
            + "CAST(physical_owner.TABLE_NAME AS BINARY) = CAST(source_owner.owner AS BINARY) "
            + "WHEN @@lower_case_table_names IN (1, 2) THEN "
            + "CAST(LOWER(physical_owner.TABLE_NAME) AS BINARY) = CAST(LOWER(source_owner.owner) AS BINARY) "
            + "ELSE FALSE END)", sql);
        Assert.DoesNotContain("BINARY ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AS BINARY(", sql, StringComparison.Ordinal);
    }

    /// <summary>Dependency transport joins retain byte-exact physical owners rather than collation aliases.</summary>
    [Fact]
    public void DependencyTransportUsesLengthFreeBinaryOwnerJoins()
    {
        // Arrange
        const string source = "SELECT 0 AS request_ordinal, 'items' AS table_name, 'id' AS column_name, "
            + "FALSE AS target_nullable";

        // Act
        var sql = MySqlSafeMigrationProviderAnalyzer.BuildColumnDependencyQuery(source);

        // Assert
        Assert.Contains("CAST(primary_column.CONSTRAINT_SCHEMA AS BINARY) = "
            + "CAST(primary_key.CONSTRAINT_SCHEMA AS BINARY)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(primary_column.TABLE_NAME AS BINARY) = CAST(primary_key.TABLE_NAME AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(local_key.CONSTRAINT_SCHEMA AS BINARY) = CAST(rc.CONSTRAINT_SCHEMA AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(local_key.TABLE_NAME AS BINARY) = CAST(rc.TABLE_NAME AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BINARY ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AS BINARY(", sql, StringComparison.Ordinal);
        Assert.Contains("ELSE FALSE END", sql, StringComparison.Ordinal);
    }
}
