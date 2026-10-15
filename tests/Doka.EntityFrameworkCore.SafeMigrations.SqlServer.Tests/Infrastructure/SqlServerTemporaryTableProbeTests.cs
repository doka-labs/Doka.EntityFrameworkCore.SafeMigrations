namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Protects exact session-local observation without starting a server or opening a connection.</summary>
public sealed class SqlServerTemporaryTableProbeTests
{
    /// <summary>
    /// Uses one exact native lookup with a bounded parameter and leaves connection ownership unchanged.
    /// </summary>
    /// <param name="localName">A caller/provider name, including literal identifier punctuation.</param>
    /// <param name="qualifiedName">The safely quoted object-name value, never SQL statement text.</param>
    [Theory]
    [InlineData("#doka_sm_identifiers", "tempdb..[#doka_sm_identifiers]")]
    [InlineData("#doka_sm_identifiers_00000000000000000000000000000001",
        "tempdb..[#doka_sm_identifiers_00000000000000000000000000000001]")]
    [InlineData("#owner']; SELECT 1; --", "tempdb..[#owner']]; SELECT 1; --]")]
    [InlineData("#owner.with.dot[%]", "tempdb..[#owner.with.dot[%]]]")]
    public void ObjectIdCommand_IsExactParameterizedAndDoesNotOpenTheSession(
        string localName,
        string qualifiedName
    )
    {
        // Arrange
        using var connection = new SqlConnection();
        using var defaultCommand = connection.CreateCommand();

        // Act
        using var command = SqlServerTemporaryTableProbe.CreateObjectIdCommand(connection, localName);

        // Assert
        Assert.Equal("SELECT OBJECT_ID(@qualified_name, N'U');", command.CommandText);
        var parameter = Assert.IsType<SqlParameter>(Assert.Single(command.Parameters.Cast<SqlParameter>()));
        Assert.Equal("@qualified_name", parameter.ParameterName);
        Assert.Equal(SqlDbType.NVarChar, parameter.SqlDbType);
        Assert.Equal(258, parameter.Size);
        Assert.Equal(qualifiedName, parameter.Value);
        Assert.Equal(defaultCommand.CommandTimeout, command.CommandTimeout);
        Assert.Null(command.Transaction);
        Assert.Same(connection, command.Connection);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Rejects nonlocal or incomplete names instead of broadening a session-local observation.</summary>
    /// <param name="localName">An invalid, global or nonlocal name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("#")]
    [InlineData("##global")]
    [InlineData("dbo.owner")]
    public void ObjectIdCommand_RejectsNonlocalNames(string? localName)
    {
        // Arrange
        using var connection = new SqlConnection();

        // Act
        var failure = Record.Exception(() =>
            SqlServerTemporaryTableProbe.CreateObjectIdCommand(connection, localName!));

        // Assert
        Assert.Equal("localName", Assert.IsAssignableFrom<ArgumentException>(failure).ParamName);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Bounds complete bracket escaping at the native local-name length boundary.</summary>
    /// <param name="length">The total local identifier length including its single hash.</param>
    /// <param name="accepted">Whether the name fits the native local-table contract.</param>
    [Theory]
    [InlineData(116, true)]
    [InlineData(117, false)]
    public void ObjectIdCommand_EnforcesLocalNameBoundary(
        int length,
        bool accepted
    )
    {
        // Arrange
        using var connection = new SqlConnection();
        var localName = "#" + new string(']', length - 1);
        SqlCommand? command = null;

        // Act
        var failure = Record.Exception(() =>
            command = SqlServerTemporaryTableProbe.CreateObjectIdCommand(connection, localName));

        using var ownedCommand = command;

        // Assert
        if (accepted)
        {
            Assert.Null(failure);
            var parameter = Assert.Single(Assert.IsType<SqlCommand>(ownedCommand).Parameters.Cast<SqlParameter>());
            var value = Assert.IsType<string>(parameter.Value);
            Assert.InRange(value.Length, 1, parameter.Size);
            Assert.Equal("tempdb..[#" + new string(']', (length - 1) * 2) + "]", value);
        }
        else
        {
            Assert.Equal("localName", Assert.IsType<ArgumentException>(failure).ParamName);
            Assert.Null(command);
        }

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>A missing session cannot silently open a different connection for the lookup.</summary>
    [Fact]
    public void ObjectIdCommand_RejectsMissingConnection()
    {
        // Arrange
        const string localName = "#caller_owned";

        // Act
        var failure = Record.Exception(() => SqlServerTemporaryTableProbe.CreateObjectIdCommand(null!, localName));

        // Assert
        Assert.Equal("connection", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }
}
