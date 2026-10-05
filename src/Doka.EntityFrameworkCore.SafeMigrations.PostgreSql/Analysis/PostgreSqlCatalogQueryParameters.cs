namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Interns bounded catalog parameters and restores provider bindings across statement rollback.</summary>
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

    /// <summary>Creates parameter accounting for a caller-owned sequential catalog command.</summary>
    /// <param name="command">The command supplying provider parameters and their physical collection.</param>
    /// <param name="typeMappingSource">The optional mapping source required for typed values.</param>
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

    /// <summary>Creates parameter accounting for a native or fallback catalog transport statement.</summary>
    /// <param name="command">The statement supplying provider parameters and their physical collection.</param>
    /// <param name="typeMappingSource">The optional mapping source required for typed values.</param>
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

    /// <summary>Gets the number of physical parameters retained by this statement.</summary>
    public int Count => _values.Count;

    /// <summary>Gets the estimated UTF-8 parameter payload including per-binding metadata.</summary>
    public int Utf8PayloadBytes => _utf8PayloadBytes;

    /// <summary>Captures the parameter count and payload before one candidate statement is built.</summary>
    /// <returns>The exact prefix retained if the candidate exceeds a query bound.</returns>
    public Checkpoint Capture() => new(_values.Count, _utf8PayloadBytes);

    /// <summary>Restores the captured prefix without changing retained parameter identities or mappings.</summary>
    /// <param name="checkpoint">The earlier parameter count and payload to restore.</param>
    public void Rollback(
        Checkpoint checkpoint
    )
    {
        if ((uint)checkpoint.Count > (uint)_values.Count
            || (uint)checkpoint.Utf8PayloadBytes > (uint)_utf8PayloadBytes
            || (checkpoint.Count == _values.Count && checkpoint.Utf8PayloadBytes != _utf8PayloadBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        if (checkpoint.Count == _values.Count)
        {
            return;
        }

        var retained = new DbParameter[checkpoint.Count];
        for (var index = 0; index < retained.Length; index++)
        {
            retained[index] = _parametersCollection[index];
        }

        // WHY: Shrinking and then growing an Npgsql parameter collection can
        // lose named lookup entries for surviving parameters. Re-register the
        // exact bounded prefix so provider lookups and enumeration agree,
        // without recreating parameter objects or their type mappings.
        _parametersCollection.Clear();
        foreach (var parameter in retained)
        {
            _parametersCollection.Add(parameter);
        }

        _values.RemoveRange(checkpoint.Count, _values.Count - checkpoint.Count);
        _parameters.Clear();
        _typedParameters.Clear();
        _utf8PayloadBytes = checkpoint.Utf8PayloadBytes;
    }

    /// <summary>Interns a catalog string and returns its provider parameter marker.</summary>
    /// <param name="value">The catalog identity or other string value to bind.</param>
    /// <returns>The retained marker for an identical string, or a newly registered marker.</returns>
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

    /// <summary>Interns a typed value using the provider mapping and its value converter.</summary>
    /// <param name="value">The model-side value, including a typed null.</param>
    /// <param name="storeType">The requested provider store type used for mapping and identity.</param>
    /// <returns>The retained or newly registered parameter marker.</returns>
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

    /// <summary>Identifies the exact parameter prefix retained before a candidate was constructed.</summary>
    /// <param name="Count">The physical parameter count at capture time.</param>
    /// <param name="Utf8PayloadBytes">The corresponding estimated UTF-8 parameter payload.</param>
    public readonly record struct Checkpoint(
        int Count,
        int Utf8PayloadBytes
    );
}
