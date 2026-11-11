namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlCatalogQueryParametersTests
{
    /// <summary>Typed bindings retain provider-specific identity, facets, converters and typed nulls.</summary>
    /// <param name="native">Whether the parameter belongs to a native batch rather than a sequential command.</param>
    /// <param name="kind">The independently selected provider mapping family.</param>
    [Theory]
    [InlineData(false, "clock")]
    [InlineData(true, "clock")]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "varchar")]
    [InlineData(true, "varchar")]
    [InlineData(false, "typed-null")]
    [InlineData(true, "typed-null")]
    [InlineData(false, "converted")]
    [InlineData(true, "converted")]
    public async Task TypedBindingsRetainAuthoritativeNativeMapping(
        bool native,
        string kind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Port=1;Database=parameter_test;Username=test;Password=test",
            registerSafeMigrations: false);

        await using var connection = new NpgsqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        using var command = new NpgsqlCommand();
        using var expectedFactory = new NpgsqlCommand();
        var batchCommand = batch.CreateCommand();
        var (value, storeType) = kind switch
        {
            "clock" => ((object?)TimeSpan.FromDays(1), "time(6) without time zone"),
            "json" => ("{}", "jsonb"),
            "varchar" => ("retained", "varchar(40)"),
            "typed-null" => (null, "date"),
            "converted" => (ParameterState.Active, "text"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = value is null
            ? mappingSource.FindMapping(storeType)!
            : mappingSource.FindMapping(value.GetType(), storeType)!;

        var expected = Assert.IsType<NpgsqlParameter>(mapping.CreateParameter(expectedFactory, "expected", value));
        using var parameters = native
            ? new PostgreSqlCatalogQueryParameters(batchCommand, mappingSource)
            : new PostgreSqlCatalogQueryParameters(command, mappingSource);

        var collection = native
            ? batchCommand.Parameters
            : command.Parameters;

        // Act
        var marker = parameters.Add(value, storeType);
        var actual = Assert.IsType<NpgsqlParameter>(collection[marker]);

        // Assert
        Assert.Equal(expected.NpgsqlDbType, actual.NpgsqlDbType);
        Assert.Equal(expected.DbType, actual.DbType);
        Assert.Equal(expected.DataTypeName, actual.DataTypeName);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Precision, actual.Precision);
        Assert.Equal(expected.Scale, actual.Scale);
        Assert.Equal(expected.IsNullable, actual.IsNullable);
        Assert.Single(collection);
        Assert.Empty(expectedFactory.Parameters);
    }

    /// <summary>Disposal owns only the private factory and cannot remove or rebind command-owned parameters.</summary>
    /// <param name="native">Whether a private factory is needed for a native batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalRetainsOwnerBindingsAndRejectsFurtherMutation(
        bool native
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Port=1;Database=parameter_test;Username=test;Password=test",
            registerSafeMigrations: false);

        await using var connection = new NpgsqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        using var command = new NpgsqlCommand();
        using var disposedControl = new NpgsqlCommand();
        var batchCommand = batch.CreateCommand();
        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        using var parameters = native
            ? new PostgreSqlCatalogQueryParameters(batchCommand, mappingSource)
            : new PostgreSqlCatalogQueryParameters(command, mappingSource);

        var marker = parameters.Add(TimeSpan.FromDays(1), "time(6) without time zone");
        var checkpoint = parameters.Capture();
        var collection = native
            ? batchCommand.Parameters
            : command.Parameters;

        var original = collection[marker];

        // Act
        parameters.Dispose();
        parameters.Dispose();
        var retained = collection[marker];
        var addFailure = Record.Exception(() => parameters.Add(TimeSpan.FromDays(1), "time(6) without time zone"));
        var stringFailure = Record.Exception(() => parameters.AddString("after-disposal"));
        var captureFailure = Record.Exception(() => parameters.Capture());
        var rollbackFailure = Record.Exception(() => parameters.Rollback(checkpoint));
        var borrowedCommandFailure = Record.Exception(() => command.CommandText = "SELECT 1");
        disposedControl.Dispose();
        var disposedCommandFailure = Record.Exception(() => disposedControl.CommandText = "SELECT 1");

        // Assert
        Assert.Same(original, retained);
        Assert.Equal(TimeSpan.FromDays(1), retained.Value);
        Assert.Single(collection);
        Assert.Null(borrowedCommandFailure);
        Assert.IsType<ObjectDisposedException>(disposedCommandFailure);
        Assert.IsType<ObjectDisposedException>(addFailure);
        Assert.IsType<ObjectDisposedException>(stringFailure);
        Assert.IsType<ObjectDisposedException>(captureFailure);
        Assert.IsType<ObjectDisposedException>(rollbackFailure);
    }

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
        using var parameters = native
            ? new PostgreSqlCatalogQueryParameters(batchCommand, mappingSource)
            : new PostgreSqlCatalogQueryParameters(command, mappingSource);

        var collection = native
            ? batchCommand.Parameters
            : command.Parameters;

        for (var index = 0; index < retainedCount; index++)
        {
            _ = (index % 3) switch
            {
                0 => parameters.AddString($"retained_{index}"),
                1 => parameters.Add(index, "integer"),
                _ => parameters.Add(
                    index == 2
                        ? null
                        : index,
                    "integer"),
            };
        }

        var checkpoint = parameters.Capture();
        var originalParameters = collection
            .Cast<NpgsqlParameter>()
            .ToArray();

        var originalShapes = originalParameters
            .Select(parameter =>
                (parameter.ParameterName, parameter.Value, parameter.DbType, parameter.NpgsqlDbType))
            .ToArray();

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
        Assert.Equal(
            checkpoint.Utf8PayloadBytes
            + Enumerable
                .Range(0, 8)
                .Sum(index => Encoding.UTF8.GetByteCount($"replacement_{index}") + 32),
            parameters.Utf8PayloadBytes);

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
                Assert.Equal(
                    originalShapes[index],
                    (parameter.ParameterName, parameter.Value, parameter.DbType, parameter.NpgsqlDbType));
            }
        }
    }

    [Fact]
    public void AddString_InternsValuesAndTracksUtf8Payload()
    {
        using var command = new NpgsqlCommand();
        using var parameters = new PostgreSqlCatalogQueryParameters(command);

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
        using var parameters = new PostgreSqlCatalogQueryParameters(command);
        _ = parameters.AddString("retained");
        var checkpoint = parameters.Capture();
        _ = parameters.AddString("discarded");

        parameters.Rollback(checkpoint);

        Assert.Equal(1, parameters.Count);
        Assert.Single(command.Parameters);
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
        using var parameters = new PostgreSqlCatalogQueryParameters(command);

        // Act
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            parameters.Rollback(new PostgreSqlCatalogQueryParameters.Checkpoint(count, payload)));

        // Assert
        Assert.Equal(0, parameters.Count);
        Assert.Equal(0, parameters.Utf8PayloadBytes);
        Assert.Empty(command.Parameters);
    }

    /// <summary>Exercises the mapping source's native enum-to-text converter.</summary>
    private enum ParameterState
    {
        Active,
    }
}
