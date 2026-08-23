namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlCatalogQueryParameterizerTests
{
    [Fact]
    public void AddString_InternsEqualValuesWithoutParsingSql()
    {
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        var first = parameterizer.AddString("O'Reilly\\value");
        var duplicate = parameterizer.AddString("O'Reilly\\value");
        var second = parameterizer.AddString("YES");

        Assert.Equal("@doka_sm_p0", first);
        Assert.Equal(first, duplicate);
        Assert.Equal("@doka_sm_p1", second);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("O'Reilly\\value", command.Parameters[0].Value);
        Assert.Equal("YES", command.Parameters[1].Value);
    }

    [Fact]
    public void Rollback_RemovesOnlyParametersAddedAfterCheckpoint()
    {
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);
        var retained = parameterizer.AddString("retained");
        var checkpoint = parameterizer.Capture();
        _ = parameterizer.AddString("discarded");

        parameterizer.Rollback(checkpoint);
        var rebound = parameterizer.AddString("discarded");

        Assert.Equal("@doka_sm_p0", retained);
        Assert.Equal("@doka_sm_p1", rebound);
        Assert.Equal(2, parameterizer.Count);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(
            checkpoint.Utf8PayloadBytes + Encoding.UTF8.GetByteCount("discarded") + 32,
            parameterizer.Utf8PayloadBytes);
    }

    [Fact]
    public void Rollback_RejectsCheckpointFromAForwardState()
    {
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            parameterizer.Rollback(new MySqlCatalogQueryParameterizer.Checkpoint(1, 0)));
    }

    [Fact]
    public void Template_Render_ReplacesOnlyOwnedValueMarkers()
    {
        var template = "c = "
            + MySqlCatalogSqlTemplate.Marker(0)
            + " AND `literal'value` = "
            + MySqlCatalogSqlTemplate.Marker(1);

        var rendered = MySqlCatalogSqlTemplate.Render(
            template,
            ["alpha", "beta"],
            static value => "parameter_" + value);

        Assert.Equal("c = parameter_alpha AND `literal'value` = parameter_beta", rendered);
    }

    [Fact]
    public void Template_Render_RejectsMalformedOrOutOfRangeMarkers()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MySqlCatalogSqlTemplate.Render("x = \u001e0", ["value"], static value => value));
        Assert.Throws<InvalidOperationException>(() =>
            MySqlCatalogSqlTemplate.Render("x = \u001einvalid\u001f", ["value"], static value => value));
        Assert.Throws<InvalidOperationException>(() =>
            MySqlCatalogSqlTemplate.Render("x = \u001e1\u001f", ["value"], static value => value));
    }
}
