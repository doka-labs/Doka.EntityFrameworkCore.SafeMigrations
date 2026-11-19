namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects parameter interning, rollback, and owned SQL-template marker boundaries.</summary>
public sealed class MySqlCatalogQueryParameterizerTests
{
    /// <summary>Interns equal parameter values without treating their contents as executable SQL.</summary>
    [Fact]
    public void AddString_InternsEqualValuesWithoutParsingSql()
    {
        // Arrange
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        // Act
        var first = parameterizer.AddString("O'Reilly\\value");
        var duplicate = parameterizer.AddString("O'Reilly\\value");
        var second = parameterizer.AddString("YES");

        // Assert
        Assert.Equal("@doka_sm_p0", first);
        Assert.Equal(first, duplicate);
        Assert.Equal("@doka_sm_p1", second);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("O'Reilly\\value", command.Parameters[0].Value);
        Assert.Equal("YES", command.Parameters[1].Value);
    }

    /// <summary>
    /// Removes only post-checkpoint parameters and permits their ordinal to be rebound after rollback.
    /// </summary>
    [Fact]
    public void Rollback_RemovesOnlyParametersAddedAfterCheckpoint()
    {
        // Arrange
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);
        var retained = parameterizer.AddString("retained");
        var checkpoint = parameterizer.Capture();

        // Act
        _ = parameterizer.AddString("discarded");

        parameterizer.Rollback(checkpoint);
        var rebound = parameterizer.AddString("discarded");

        // Assert
        Assert.Equal("@doka_sm_p0", retained);
        Assert.Equal("@doka_sm_p1", rebound);
        Assert.Equal(2, parameterizer.Count);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(
            checkpoint.Utf8PayloadBytes + Encoding.UTF8.GetByteCount("discarded") + 32,
            parameterizer.Utf8PayloadBytes);
    }

    /// <summary>Rejects a parameter checkpoint that describes an impossible forward state.</summary>
    [Fact]
    public void Rollback_RejectsCheckpointFromAForwardState()
    {
        // Arrange
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        // Act
        var capturedException = Record.Exception(() =>
            parameterizer.Rollback(new MySqlCatalogQueryParameterizer.Checkpoint(1, 0)));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(capturedException);
    }

    /// <summary>Replaces only owned value markers without reinterpreting surrounding SQL or quoted tokens.</summary>
    [Fact]
    public void Template_Render_ReplacesOnlyOwnedValueMarkers()
    {
        // Arrange
        var template = "c = "
            + MySqlCatalogSqlTemplate.Marker(0)
            + " AND `literal'value` = "
            + MySqlCatalogSqlTemplate.Marker(1);

        // Act
        var rendered = MySqlCatalogSqlTemplate.Render(
            template,
            ["alpha", "beta"],
            static value => "parameter_" + value);

        // Assert
        Assert.Equal("c = parameter_alpha AND `literal'value` = parameter_beta", rendered);
    }

    /// <summary>
    /// Rejects each malformed or unowned marker independently rather than stopping at the first failure.
    /// </summary>
    /// <param name="template">The unterminated, nonnumeric, or out-of-range marker.</param>
    [Theory]
    [InlineData("x = \u001e0")]
    [InlineData("x = \u001einvalid\u001f")]
    [InlineData("x = \u001e1\u001f")]
    public void Template_Render_RejectsMalformedOrOutOfRangeMarkers(
        string template
    )
    {
        // Arrange
        string[] values = ["value"];

        // Act
        var exception = Record.Exception(() => MySqlCatalogSqlTemplate.Render(template, values, static value => value));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }
}
