namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlCatalogQueryParameterizer
{
    private readonly DbCommand _command;
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly List<string> _values = [];
    private int _utf8PayloadBytes;

    public MySqlCatalogQueryParameterizer(
        DbCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        _command = command;
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
            _command.Parameters.RemoveAt(last);
        }

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

        var name = $"@doka_sm_p{_command.Parameters.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _command.Parameters.Add(parameter);
        _parameters.Add(value, name);
        _values.Add(value);
        _utf8PayloadBytes += Encoding.UTF8.GetByteCount(value) + 32;

        return name;
    }

    public readonly record struct Checkpoint(
        int Count,
        int Utf8PayloadBytes
    );
}
