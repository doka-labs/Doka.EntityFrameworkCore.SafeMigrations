namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies physical inventory owner bindings without changing authored table contracts.</summary>
public sealed class MySqlUnexpectedInventoryOwnerTests
{
    /// <summary>A native stored name changes only lookup keys, not the raw owner or expected facets.</summary>
    [Fact]
    public void NativeOwnerBindingPreservesAuthoredDefinition()
    {
        // Arrange
        var definition = Inventory("MixedItems");
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = definition,
        };

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "mixeditems",
        };

        // Act
        var inventory = MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings);

        // Assert
        var entry = Assert.Single(inventory);
        Assert.Equal("mixeditems", entry.Key);
        Assert.Same(definition, entry.Value);
        Assert.Equal("MixedItems", entry.Value.Table);
        Assert.Equal("int", entry.Value.ColumnStoreTypes["required_value"]);
        Assert.True(expected.ContainsKey("MixedItems"));
        Assert.False(expected.ContainsKey("mixeditems"));
    }

    /// <summary>A missing live owner retains its authored key without inventing a physical identity.</summary>
    [Fact]
    public void MissingNativeOwnerPreservesAuthoredKey()
    {
        // Arrange
        var definition = Inventory("MissingItems");
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal)
        {
            ["MissingItems"] = definition,
        };

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal)
        {
            ["MissingItems"] = null,
        };

        // Act
        var inventory = MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings);

        // Assert
        Assert.Same(definition, inventory["MissingItems"]);
        Assert.Equal("MissingItems", Assert.Single(inventory).Key);
    }

    /// <summary>Distinct authored definitions cannot merge after the server resolves one physical owner.</summary>
    [Fact]
    public void DuplicatePhysicalExpectedOwnerIsRejected()
    {
        // Arrange
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = Inventory("MixedItems"),
            ["MIXEDITEMS"] = Inventory("MIXEDITEMS"),
        };

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "mixeditems",
            ["MIXEDITEMS"] = "mixeditems",
        };

        // Act
        var exception = Record.Exception(
            () => MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, expected.Count);
    }

    /// <summary>Case-sensitive native identities remain distinct instead of inheriting a global comparer.</summary>
    [Fact]
    public void DistinctNativeExpectedOwnersRemainDistinct()
    {
        // Arrange
        var first = Inventory("MixedItems");
        var second = Inventory("mixeditems");
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = first,
            ["mixeditems"] = second,
        };

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "MixedItems",
            ["mixeditems"] = "mixeditems",
        };

        // Act
        var inventory = MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings);

        // Assert
        Assert.Equal(2, inventory.Count);
        Assert.Same(first, inventory["MixedItems"]);
        Assert.Same(second, inventory["mixeditems"]);
    }

    /// <summary>A truncated binding result cannot fall back to an unverified authored identity.</summary>
    [Fact]
    public void MissingExpectedOwnerResultIsRejected()
    {
        // Arrange
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = Inventory("MixedItems"),
        };

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal);

        // Act
        var exception = Record.Exception(
            () => MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Empty expected inventories require no owner-binding results.</summary>
    [Fact]
    public void EmptyExpectedInventoryRemainsEmpty()
    {
        // Arrange
        Dictionary<string, SafeMigrationExpectedTableInventory> expected = new(StringComparer.Ordinal);
        Dictionary<string, string?> bindings = new(StringComparer.Ordinal);

        // Act
        var inventory = MySqlSafeMigrationProviderAnalyzer.BindUnexpectedInventoryOwners(expected, bindings);

        // Assert
        Assert.Empty(inventory);
    }

    /// <summary>Creates one immutable raw table inventory for owner-only mapping tests.</summary>
    /// <param name="table">The untouched authored owner.</param>
    /// <returns>The expected table and its original column facets.</returns>
    private static SafeMigrationExpectedTableInventory Inventory(string table) => new(
        table, schema: null,
        columns: [new KeyValuePair<string, string?>("required_value", "int")],
        indexes: [], uniqueIndexes: [], indexDefinitions: [], constraints: []);
}
