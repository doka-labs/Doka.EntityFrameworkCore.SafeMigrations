namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlCatalogQueryParameterizerTests
{
    [Fact]
    public void Parameterize_HandlesQuotedAndUtf8HexLiteralsWithoutTouchingIdentifiers()
    {
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        var sql = parameterizer.Parameterize(
            "c = 'O''Reilly' AND x = _utf8mb4 X'615C62' " + "AND `odd'name``x` = 'YES'");

        Assert.Equal("c = @doka_sm_p0 AND x = @doka_sm_p1 " + "AND `odd'name``x` = @doka_sm_p2", sql);
        Assert.Equal(3, command.Parameters.Count);
        Assert.Equal("O'Reilly", command.Parameters[0].Value);
        Assert.Equal("a\\b", command.Parameters[1].Value);
        Assert.Equal("YES", command.Parameters[2].Value);
    }

    [Fact]
    public void Parameterize_RejectsModeDependentOrMalformedLiterals()
    {
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        Assert.Throws<InvalidOperationException>(() => parameterizer.Parameterize("x = 'a\\b'"));
        Assert.Throws<InvalidOperationException>(() => parameterizer.Parameterize("x = 'unterminated"));
        Assert.Throws<InvalidOperationException>(() => parameterizer.Parameterize("x = _utf8mb4 X'ABC'"));
        Assert.Throws<InvalidOperationException>(() => parameterizer.Parameterize("x = _utf8mb4 X'GG'"));
        Assert.Throws<InvalidOperationException>(() => parameterizer.Parameterize("`unterminated"));
    }
}
