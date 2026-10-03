namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlCatalogQueryParametersTests
{
    /// <summary>
    /// Rollback and subsequent growth retain every physical parameter's named binding and type mapping.
    /// </summary>
    /// <param name="native">Whether the parameters belong to a real Npgsql batch command.</param>
    /// <param name="retainedCount">The number of mixed parameters retained across rollback.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public async Task Rollback_PreservesBindingsAfterTransientGrowth(
        bool native,
        int retainedCount
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Port=1;Database=parameter_test;Username=test;Password=test",
            registerSafeMigrations: false);

        await using var connection = new NpgsqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        using var command = new NpgsqlCommand();
        var batchCommand = batch.CreateCommand();
        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var parameters = native
            ? new PostgreSqlCatalogQueryParameters(batchCommand, mappingSource)
            : new PostgreSqlCatalogQueryParameters(command, mappingSource);

        var collection = native ? batchCommand.Parameters : command.Parameters;
        for (var index = 0; index < retainedCount; index++)
        {
            _ = (index % 3) switch
            {
                0 => parameters.AddString($"retained_{index}"),
                1 => parameters.Add(index, "integer"),
                _ => parameters.Add(index == 2 ? null : (object)index, "integer"),
            };
        }

        var checkpoint = parameters.Capture();
        var originalParameters = collection.Cast<NpgsqlParameter>().ToArray();
        var originalShapes = originalParameters.Select(parameter =>
            (parameter.ParameterName, parameter.Value, parameter.DbType, parameter.NpgsqlDbType)).ToArray();

        string? lastDiscarded = null;
        for (var index = 0; index < 8; index++)
        {
            lastDiscarded = parameters.AddString($"discarded_{index}");
        }

        // WHY: Trigger the real provider's named lookup before rollback;
        // enumeration alone would miss a stale lookup with surviving objects.
        var warmedLookup = collection.IndexOf(lastDiscarded!);

        // Act
        parameters.Rollback(checkpoint);
        for (var index = 0; index < 8; index++)
        {
            _ = parameters.AddString($"replacement_{index}");
        }

        // Assert
        Assert.Equal(retainedCount + 7, warmedLookup);
        Assert.Equal(retainedCount + 8, parameters.Count);
        Assert.Equal(parameters.Count, collection.Count);
        Assert.Equal(checkpoint.Utf8PayloadBytes + Enumerable.Range(0, 8)
            .Sum(index => Encoding.UTF8.GetByteCount($"replacement_{index}") + 32), parameters.Utf8PayloadBytes);
        for (var index = 0; index < collection.Count; index++)
        {
            var parameter = Assert.IsType<NpgsqlParameter>(collection[index]);
            Assert.Equal($"@doka_sm_p{index}", parameter.ParameterName);
            Assert.Equal(index, collection.IndexOf(parameter.ParameterName));
            Assert.True(collection.Contains(parameter.ParameterName));
            Assert.Same(parameter, collection[parameter.ParameterName]);
            if (index < retainedCount)
            {
                Assert.Same(originalParameters[index], parameter);
                Assert.Equal(originalShapes[index],
                    (parameter.ParameterName, parameter.Value, parameter.DbType, parameter.NpgsqlDbType));
            }
        }
    }

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

    /// <summary>Invalid count and payload checkpoints cannot mutate parameter accounting.</summary>
    /// <param name="count">The supplied checkpoint parameter count.</param>
    /// <param name="payload">The supplied checkpoint payload.</param>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    public void Rollback_RejectsInvalidCheckpoint(
        int count,
        int payload
    )
    {
        // Arrange
        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);

        // Act
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            parameters.Rollback(new PostgreSqlCatalogQueryParameters.Checkpoint(count, payload)));

        // Assert
        Assert.Equal(0, parameters.Count);
        Assert.Equal(0, parameters.Utf8PayloadBytes);
        Assert.Empty(command.Parameters);
    }
}
