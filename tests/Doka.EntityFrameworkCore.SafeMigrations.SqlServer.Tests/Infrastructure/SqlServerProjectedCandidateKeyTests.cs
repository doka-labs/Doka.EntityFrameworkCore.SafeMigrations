namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies the compact candidate-key lifecycle used by ordered inline FK proofs.</summary>
public sealed class SqlServerProjectedCandidateKeyTests
{
    /// <summary>A single catalog-equivalent spelling matches by physical id, never by guessed name folding.</summary>
    [Theory]
    [InlineData("id", 7, true)]
    [InlineData("Id", 8, false)]
    [InlineData("other", 9, false)]
    [InlineData("Id", 0, false)]
    public void LiveCandidateKeyUsesResolvedColumnIdentity(
        string requestedName,
        int requestedId,
        bool expected
    )
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "key", ["Id"], [7]);

        // Act
        var present = keys.Contains(1, [requestedName], [requestedId]);

        // Assert
        Assert.Equal(expected, present);
    }

    /// <summary>Removing the last physical key cannot be bypassed through a catalog-equivalent spelling.</summary>
    [Fact]
    public void DroppedPhysicalKeyRemovesCaseEquivalentColumnEvidence()
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "key", ["Id"], [7]);

        // Act
        keys.Remove(1, "key");
        var present = keys.Contains(1, ["id"], [7]);

        // Assert
        Assert.False(present);
    }

    /// <summary>
    /// Removing either physical alias removes one backing key without losing an equivalent independent key.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RemovedAliasCannotPreservePhysicalKey(
        bool alternateKey,
        bool expected
    )
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "primary", ["Id"]);
        keys.BindName(1, "alias", -1);
        if (alternateKey)
        {
            keys.Add(1, "unique", ["Id"]);
        }

        // Act
        keys.Remove(1, "alias");
        var present = keys.Contains(1, ["Id"]);

        // Assert
        Assert.Equal(expected, present);
    }

    /// <summary>A key rename preserves column evidence but does not keep the old name as a drop handle.</summary>
    [Fact]
    public void RenamedKeyRetainsOnlyCurrentDropName()
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "before", ["Id"]);

        // Act
        keys.Rename(1, "before", "after");
        keys.Remove(1, "before");
        var present = keys.Contains(1, ["Id"]);

        // Assert
        Assert.True(present);
    }

    /// <summary>A column rename changes candidate-key evidence to the current ordered column names.</summary>
    [Fact]
    public void RenamedColumnUpdatesKeyOrderWithoutInventingOldColumnEvidence()
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "key", ["First", "Second"]);

        // Act
        keys.RenameColumn(1, "First", "Renamed");
        var oldPresent = keys.Contains(1, ["First", "Second"]);
        var newPresent = keys.Contains(1, ["Renamed", "Second"]);
        var reordered = keys.Contains(1, ["Second", "Renamed"]);

        // Assert
        Assert.False(oldPresent);
        Assert.True(newPresent);
        Assert.False(reordered);
    }

    /// <summary>A table drop invalidates all its keys while unrelated principal keys survive.</summary>
    [Fact]
    public void DroppedTableRemovesOnlyItsOwnCandidateKeys()
    {
        // Arrange
        var keys = new SqlServerProjectedCandidateKeys();
        keys.Add(1, "first", ["Id"]);
        keys.Add(2, "second", ["Id"]);

        // Act
        keys.RemoveTable(1);
        var removed = keys.Contains(1, ["Id"]);
        var unrelated = keys.Contains(2, ["Id"]);

        // Assert
        Assert.False(removed);
        Assert.True(unrelated);
    }
}
