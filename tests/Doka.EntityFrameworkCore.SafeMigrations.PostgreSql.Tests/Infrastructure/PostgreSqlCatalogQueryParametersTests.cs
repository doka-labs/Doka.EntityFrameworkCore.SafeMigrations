namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlCatalogQueryParametersTests
{
    [Fact]
    public void AddString_CreatesOrderedCommandParameters()
    {
        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);

        Assert.Equal("@doka_sm_p0", parameters.AddString("O'Reilly"));
        Assert.Equal("@doka_sm_p1", parameters.AddString("value"));
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("O'Reilly", command.Parameters[0].Value);
        Assert.Equal("value", command.Parameters[1].Value);
    }
}
