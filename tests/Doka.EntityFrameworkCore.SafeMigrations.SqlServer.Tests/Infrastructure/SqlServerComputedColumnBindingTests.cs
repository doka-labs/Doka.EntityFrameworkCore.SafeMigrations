namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies native identifier binding ownership independently of database collation.</summary>
public sealed class SqlServerComputedColumnBindingTests
{
    /// <summary>Two native aliases retain the same exact authored source rather than an unbound CLR spelling.</summary>
    [Fact]
    public void RepeatedNativeIdentityUsesOneImmutableAuthoredBinding()
    {
        // Arrange
        var column = new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint");
        var table = new ExpectedTableDefinition("nodes", [column]);

        // Act
        var bindings = SqlServerComputedColumnBindings.Read("0:10:left_bound0:10:left_bound", 2, table);

        // Assert
        Assert.Single(bindings);
        Assert.Same(column, bindings["left_bound"]);
    }

    /// <summary>Live source names preserve delimiters and UTF-16 length without delimiter-based splitting.</summary>
    [Fact]
    public void NativeLiveIdentityPreservesArbitraryMemberText()
    {
        // Arrange
        const string name = "L:;\U0001F600";
        var encoded = "-1:" + name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name;

        // Act
        var bindings = SqlServerComputedColumnBindings.Read(encoded, 1, null);

        // Assert
        Assert.Single(bindings);
        Assert.Null(bindings[name]);
    }

    /// <summary>Malformed or foreign native bindings cannot publish a projection certificate.</summary>
    /// <param name="encoded">The malformed wire value.</param>
    /// <param name="references">The number of source references owned by the candidate.</param>
    [Theory]
    [InlineData("", 1)]
    [InlineData("0:1:L", 2)]
    [InlineData("0:1:L0:1:L", 1)]
    [InlineData("-2:1:L", 1)]
    [InlineData("1:1:L", 1)]
    [InlineData("0:1:R", 1)]
    [InlineData("0:0:", 1)]
    [InlineData("0:129:L", 1)]
    [InlineData("0:2:L", 1)]
    [InlineData("x:1:L", 1)]
    [InlineData("0:x:L", 1)]
    [InlineData("9999999999999999:1:L", 1)]
    public void InvalidNativeBindingsAreRejected(string encoded, int references)
    {
        // Arrange
        var table = new ExpectedTableDefinition("nodes",
            [new ExpectedColumnDefinition("L", typeof(long), false, "bigint")]);

        // Act
        var failure = Record.Exception(() => SqlServerComputedColumnBindings.Read(encoded, references, table));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }
}
