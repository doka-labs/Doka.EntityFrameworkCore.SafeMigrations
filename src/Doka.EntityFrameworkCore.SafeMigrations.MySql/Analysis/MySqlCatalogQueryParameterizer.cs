namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlCatalogQueryParameterizer
{
    private readonly Func<DbParameter> _createParameter;
    private readonly DbParameterCollection _parametersCollection;
    private readonly IRelationalTypeMappingSource? _typeMappingSource;
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<MySqlCatalogParameterValue, string> _typedParameters = new(
        MySqlCatalogParameterValueComparer.Instance);
    private int _utf8PayloadBytes;

    public MySqlCatalogQueryParameterizer(
        DbCommand command,
        IRelationalTypeMappingSource? typeMappingSource = null
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        _createParameter = command.CreateParameter;
        _parametersCollection = command.Parameters;
        _typeMappingSource = typeMappingSource;
    }

    public MySqlCatalogQueryParameterizer(
        SafeMigrationCatalogCommand command,
        IRelationalTypeMappingSource? typeMappingSource = null
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        _createParameter = command.CreateParameter;
        _parametersCollection = command.Parameters;
        _typeMappingSource = typeMappingSource;
    }

    public int Count => _parametersCollection.Count;

    public int Utf8PayloadBytes => _utf8PayloadBytes;

    public Checkpoint Capture() => new(_parametersCollection.Count, _utf8PayloadBytes);

    public void Rollback(
        Checkpoint checkpoint
    )
    {
        if ((uint)checkpoint.Count > (uint)_parametersCollection.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        while (_parametersCollection.Count > checkpoint.Count)
        {
            _parametersCollection.RemoveAt(_parametersCollection.Count - 1);
        }

        _parameters.Clear();
        _typedParameters.Clear();

        // Rebuild both lookup maps lazily. Retaining entries for parameters
        // removed by an oversized statement would allow stale marker reuse.
        _utf8PayloadBytes = checkpoint.Utf8PayloadBytes;
    }

    public string AddString(
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_parameters.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var name = $"@doka_sm_p{_parametersCollection.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _createParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _parametersCollection.Add(parameter);
        _parameters.Add(value, name);
        _utf8PayloadBytes += Encoding.UTF8.GetByteCount(value) + 32;

        return name;
    }

    public string Add(
        MySqlCatalogParameterValue value
    )
    {
        if (value.StoreType is null)
        {
            return AddString((string)value.Value!);
        }

        if (_typedParameters.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var name = $"@doka_sm_p{_parametersCollection.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _createParameter();
        parameter.ParameterName = name;

        var mappingSource = _typeMappingSource
            ?? throw new InvalidOperationException(
                "Typed MySQL catalog values require a relational type-mapping source.");

        var mapping = value.Value is null
            ? mappingSource.FindMapping(value.StoreType)
            : mappingSource.FindMapping(value.Value.GetType(), value.StoreType);

        if (mapping is null)
        {
            throw new NotSupportedException($"MySQL has no type mapping for store type '{value.StoreType}'.");
        }

        parameter.Value = mapping.Converter?.ConvertToProvider(value.Value) ?? value.Value ?? DBNull.Value;
        if (mapping.DbType is { } dbType)
        {
            parameter.DbType = dbType;
        }

        _parametersCollection.Add(parameter);
        _typedParameters.Add(value, name);
        _utf8PayloadBytes += EstimatePayloadBytes(value.Value) + 32;

        return name;
    }

    private static int EstimatePayloadBytes(
        object? value
    ) => value switch
    {
        null => 4,
        string text => Encoding.UTF8.GetByteCount(text),
        byte[] bytes => bytes.Length,
        _ => 32,
    };

    public readonly record struct Checkpoint(
        int Count,
        int Utf8PayloadBytes
    );
}
