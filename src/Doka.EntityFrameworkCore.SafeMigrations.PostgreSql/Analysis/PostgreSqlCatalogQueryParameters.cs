namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlCatalogQueryParameters
{
    private readonly DbCommand _command;

    public PostgreSqlCatalogQueryParameters(
        DbCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        _command = command;
    }

    public string AddString(
        string value
    )
    {
        var name = $"@doka_sm_p{_command.Parameters.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _command.Parameters.Add(parameter);

        return name;
    }
}
