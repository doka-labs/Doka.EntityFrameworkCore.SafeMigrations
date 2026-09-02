namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlCatalogQueryParameters
{
    private readonly Func<DbParameter> _createParameter;
    private readonly DbParameterCollection _parametersCollection;
    private readonly IRelationalTypeMappingSource? _typeMappingSource;
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<PostgreSqlCatalogParameterValue, string> _typedParameters = new(
        PostgreSqlCatalogParameterValueComparer.Instance);
    private readonly List<string> _values = [];
    private int _utf8PayloadBytes;

    public PostgreSqlCatalogQueryParameters(
        DbCommand command,
        IRelationalTypeMappingSource? typeMappingSource = null
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        _createParameter = command.CreateParameter;
        _parametersCollection = command.Parameters;
        _typeMappingSource = typeMappingSource;
    }

    public PostgreSqlCatalogQueryParameters(
        SafeMigrationCatalogCommand command,
        IRelationalTypeMappingSource? typeMappingSource = null
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        _createParameter = command.CreateParameter;
        _parametersCollection = command.Parameters;
        _typeMappingSource = typeMappingSource;
    }

    public int Count => _values.Count;

    public int Utf8PayloadBytes => _utf8PayloadBytes;

    public Checkpoint Capture() => new(_values.Count, _utf8PayloadBytes);

    public void Rollback(
        Checkpoint checkpoint
    )
    {
        if ((uint)checkpoint.Count > (uint)_values.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        while (_values.Count > checkpoint.Count)
        {
            var last = _values.Count - 1;
            _parameters.Remove(_values[last]);
            _values.RemoveAt(last);
            _parametersCollection.RemoveAt(last);
        }

        // The retained parameters remain valid. Rebuilding lookup state on
        // demand is cheaper and less error-prone than retaining stale marker
        // entries after an oversized statement is rolled back.
        _parameters.Clear();
        _typedParameters.Clear();

        _utf8PayloadBytes = checkpoint.Utf8PayloadBytes;
    }

    public string AddString(
        string value
    )
    {
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
        _values.Add(value);
        _utf8PayloadBytes += Encoding.UTF8.GetByteCount(value) + 32;

        return name;
    }

    public string Add(
        object? value,
        string storeType
    )
    {
        var candidate = new PostgreSqlCatalogParameterValue(value, storeType);
        if (_typedParameters.TryGetValue(candidate, out var existing))
        {
            return existing;
        }

        var mappingSource = _typeMappingSource
            ?? throw new InvalidOperationException(
                "Typed PostgreSQL catalog values require a relational type-mapping source.");

        var mapping = value is null
            ? mappingSource.FindMapping(storeType)
            : mappingSource.FindMapping(value.GetType(), storeType);

        if (mapping is null)
        {
            throw new NotSupportedException(
                $"PostgreSQL has no type mapping for store type '{storeType}'.");
        }

        var name = $"@doka_sm_p{_parametersCollection.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _createParameter();
        parameter.ParameterName = name;
        parameter.Value = mapping.Converter?.ConvertToProvider(value) ?? value ?? DBNull.Value;
        if (mapping.DbType is { } dbType)
        {
            parameter.DbType = dbType;
        }

        _parametersCollection.Add(parameter);
        _typedParameters.Add(candidate, name);
        _values.Add(name);
        _utf8PayloadBytes += EstimatePayloadBytes(value) + 32;

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
