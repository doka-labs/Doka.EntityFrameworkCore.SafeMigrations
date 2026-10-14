namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards complete physical dependency inventories and their fail-closed transport boundary.</summary>
public sealed class MySqlMultiValuedIndexDropProofTests
{
    /// <summary>Every physical FK alias remains visible without imposing an arbitrary inventory limit.</summary>
    [Fact]
    public async Task DropInventoryPreservesEveryNativeAlias()
    {
        // Arrange
        using var facts = Facts();
        for (var ordinal = 0; ordinal < 40; ordinal++)
        {
            facts.Rows.Add(1, "fk_" + ordinal.ToString(CultureInfo.InvariantCulture));
        }

        await using var reader = facts.CreateDataReader();

        // Act
        var proof = await MySqlSafeMigrationProviderAnalyzer.ReadMultiValuedIndexDropProofAsync(
            reader, CancellationToken.None);

        // Assert
        Assert.True(proof.IndexExists);
        Assert.Equal(40, proof.ForeignKeyNames.Length);
        Assert.Equal(40, proof.ForeignKeyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>A complete empty inventory distinguishes native absence from an unbound existing index.</summary>
    /// <param name="exists">Whether the independently observed index physically exists.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropInventoryAcceptsOnlyAnOwnedEmptySentinel(bool exists)
    {
        // Arrange
        using var facts = Facts();
        facts.Rows.Add(exists ? 1 : 0, DBNull.Value);
        await using var reader = facts.CreateDataReader();

        // Act
        var proof = await MySqlSafeMigrationProviderAnalyzer.ReadMultiValuedIndexDropProofAsync(
            reader, CancellationToken.None);

        // Assert
        Assert.Equal(exists, proof.IndexExists);
        Assert.Empty(proof.ForeignKeyNames);
    }

    /// <summary>Malformed or contradictory metadata cannot certify dependency-first index removal.</summary>
    /// <param name="shape">The malformed transport or alias inventory being rejected.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("contradiction")]
    [InlineData("unowned")]
    [InlineData("sentinel_first")]
    [InlineData("sentinel_last")]
    [InlineData("duplicate_sentinel")]
    [InlineData("empty_alias")]
    [InlineData("text_boolean")]
    [InlineData("fractional_boolean")]
    public async Task DropInventoryRejectsMalformedFacts(string shape)
    {
        // Arrange
        using var facts = Facts();
        switch (shape)
        {
            case "empty":
                break;
            case "truncated":
                facts.Columns.RemoveAt(1);
                break;
            case "duplicate":
                facts.Rows.Add(1, "fk_native");
                facts.Rows.Add(1, "FK_NATIVE");
                break;
            case "contradiction":
                facts.Rows.Add(1, "fk_native");
                facts.Rows.Add(0, "fk_other");
                break;
            case "unowned":
                facts.Rows.Add(0, "fk_native");
                break;
            case "sentinel_first":
                facts.Rows.Add(1, DBNull.Value);
                facts.Rows.Add(1, "fk_native");
                break;
            case "sentinel_last":
                facts.Rows.Add(1, "fk_native");
                facts.Rows.Add(1, DBNull.Value);
                break;
            case "duplicate_sentinel":
                facts.Rows.Add(1, DBNull.Value);
                facts.Rows.Add(1, DBNull.Value);
                break;
            case "empty_alias":
                facts.Rows.Add(1, string.Empty);
                break;
            case "text_boolean":
                facts.Rows.Add("1", "fk_native");
                break;
            case "fractional_boolean":
                facts.Rows.Add(0.5m, "fk_native");
                break;
            default:
                throw new UnreachableException();
        }

        await using var reader = facts.CreateDataReader();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadMultiValuedIndexDropProofAsync(reader, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Creates the exact native two-field dependency-inventory wire shape.</summary>
    /// <returns>An owned in-memory catalog result with strict scalar types.</returns>
    private static DataTable Facts()
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("index_exists", typeof(object));
        table.Columns.Add("constraint_name", typeof(string));

        return table;
    }
}
