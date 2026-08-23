namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlCatalogQueryParametersTests
{
    [Fact]
    public void AddString_InternsValuesAndTracksUtf8Payload()
    {
        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);

        var first = parameters.AddString("schema");
        var duplicate = parameters.AddString("schema");
        var unicode = parameters.AddString("\u00fc");

        Assert.Equal("@doka_sm_p0", first);
        Assert.Equal(first, duplicate);
        Assert.Equal("@doka_sm_p1", unicode);
        Assert.Equal(2, parameters.Count);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("schema") + Encoding.UTF8.GetByteCount("\u00fc") + 64,
            parameters.Utf8PayloadBytes);
    }

    [Fact]
    public void Rollback_RestoresParameterAndPayloadState()
    {
        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);
        _ = parameters.AddString("retained");
        var checkpoint = parameters.Capture();
        _ = parameters.AddString("discarded");

        parameters.Rollback(checkpoint);

        Assert.Equal(1, parameters.Count);
        Assert.Single(command.Parameters.Cast<NpgsqlParameter>());
        Assert.Equal(checkpoint.Utf8PayloadBytes, parameters.Utf8PayloadBytes);
        Assert.Equal("@doka_sm_p1", parameters.AddString("discarded"));
    }

    [Fact]
    public void Rollback_RejectsCheckpointFromAForwardState()
    {
        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            parameters.Rollback(new PostgreSqlCatalogQueryParameters.Checkpoint(1, 0)));
    }
}
