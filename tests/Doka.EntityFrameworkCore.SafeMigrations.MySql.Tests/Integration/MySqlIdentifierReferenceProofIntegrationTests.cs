namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises native preparation, transaction preservation and invocation-owned session cleanup.</summary>
public sealed class MySqlIdentifierReferenceProofIntegrationTests : MySqlIntegrationTestBase
{
    /// <summary>Uses the shared native engine with an independently leased database per test.</summary>
    /// <param name="fixture">The assembly-owned native engine.</param>
    public MySqlIdentifierReferenceProofIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Native grammar distinguishes ordinary/nonreserved heads from genuinely reserved roots.</summary>
    /// <param name="head">The exact unquoted reference head.</param>
    /// <param name="expected">Whether MariaDB accepts that head as an actual column reference.</param>
    [MariaDbIdentifierProofTheory]
    [InlineData("id", 1)]
    [InlineData("value", 1)]
    [InlineData("select", 0)]
    [InlineData("CURRENT_USER", 0)]
    [InlineData("LOCALTIME", 0)]
    [InlineData("LOCALTIMESTAMP", 0)]
    [InlineData("SESSION_USER", 1)]
    [InlineData("USER", 1)]
    [InlineData("SYSTEM_USER", 1)]
    public async Task NativePreparationClassifiesIdentifierHeads(string head, int expected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var proof = CreateProof(head);

        // Act
        await ExecuteAsync(connection,
            MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, "TRUE", "TRUE"));
        var result = await ScalarAsync(connection, "SELECT " + proof.ResultVariable + ";");
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));

        // Assert
        Assert.Equal(expected, Convert.ToInt32(result, CultureInfo.InvariantCulture));
    }

    /// <summary>Compiling an anonymous handler block does not commit the caller's existing transaction.</summary>
    [MariaDbIdentifierProofFact]
    public async Task NativeProofPreservesExplicitTransactionRollback()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE rollback_probe(id int NOT NULL);");
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, "INSERT INTO rollback_probe VALUES (1);", transaction);
        var before = await ScalarAsync(connection, "SELECT COUNT(*) FROM rollback_probe;", transaction);
        var proof = CreateProof("value");

        // Act
        await ExecuteAsync(connection,
            MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, "TRUE", "TRUE"), transaction);
        var result = await ScalarAsync(connection, "SELECT " + proof.ResultVariable + ";", transaction);
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof), transaction);
        await transaction.RollbackAsync();
        var after = await ScalarAsync(connection, "SELECT COUNT(*) FROM rollback_probe;");

        // Assert
        Assert.Equal(1, Convert.ToInt32(before, CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(result, CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt32(after, CultureInfo.InvariantCulture));
    }

    /// <summary>Success and repeated cleanup cannot replace or release an unrelated caller preparation.</summary>
    [MariaDbIdentifierProofFact]
    public async Task CleanupPreservesCallerOwnedPreparedStatement()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PREPARE doka_sm_stmt FROM 'SELECT 42';");
        var proof = CreateProof("select");

        // Act
        await ExecuteAsync(connection,
            MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, "TRUE", "TRUE"));
        var result = await ScalarAsync(connection, "SELECT " + proof.ResultVariable + ";");
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));
        var caller = await ScalarAsync(connection, "EXECUTE doka_sm_stmt;");
        var cleared = await ScalarAsync(connection, "SELECT " + proof.ResultVariable + " IS NULL;");
        await ExecuteAsync(connection, "DEALLOCATE PREPARE doka_sm_stmt;");

        // Assert
        Assert.Equal(0, Convert.ToInt32(result, CultureInfo.InvariantCulture));
        Assert.Equal(42, Convert.ToInt32(caller, CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(cleared, CultureInfo.InvariantCulture));
    }

    /// <summary>Independent sessions retain different proof values even with identical scoped identities.</summary>
    [MariaDbIdentifierProofFact]
    public async Task ParallelSessionsCannotShareProofResults()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var first = new MySqlConnection(connectionString);
        await using var second = new MySqlConnection(connectionString);
        await Task.WhenAll(first.OpenAsync(), second.OpenAsync());
        var scope = Guid.NewGuid().ToString("N");
        var valid = CreateProof("id", scope);
        var invalid = CreateProof("select", scope);

        // Act
        await Task.WhenAll(
            ExecuteAsync(first, MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(valid, "TRUE", "TRUE")),
            ExecuteAsync(second,
                MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(invalid, "TRUE", "TRUE")));
        var results = await Task.WhenAll(
            ScalarAsync(first, "SELECT " + valid.ResultVariable + ";"),
            ScalarAsync(second, "SELECT " + invalid.ResultVariable + ";"));
        await Task.WhenAll(
            ExecuteAsync(first, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(valid)),
            ExecuteAsync(second, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(invalid)));

        // Assert
        Assert.Equal(valid.ResultVariable, invalid.ResultVariable);
        Assert.Equal(1, Convert.ToInt32(results[0], CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt32(results[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Cleanup cannot clear another invocation's completed value in the same session.</summary>
    [MariaDbIdentifierProofFact]
    public async Task SameSessionScopesHaveIndependentCleanup()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var valid = CreateProof("value");
        var invalid = CreateProof("select");

        // Act
        await ExecuteAsync(connection,
            MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(valid, "TRUE", "TRUE")
            + MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(invalid, "TRUE", "TRUE"));
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(invalid));
        var retained = await ScalarAsync(connection, "SELECT " + valid.ResultVariable + ";");
        var cleared = await ScalarAsync(connection, "SELECT " + invalid.ResultVariable + " IS NULL;");
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(valid));

        // Assert
        Assert.NotEqual(valid.ResultVariable, invalid.ResultVariable);
        Assert.Equal(1, Convert.ToInt32(retained, CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(cleared, CultureInfo.InvariantCulture));
    }

    /// <summary>Cleanup also releases preparation interrupted before its normal deallocation sequence.</summary>
    [MariaDbIdentifierProofFact]
    public async Task CleanupReleasesPartiallyAcquiredOwnedStatements()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var proof = CreateProof("id");
        await ExecuteAsync(connection, "PREPARE " + proof.InnerStatementName + " FROM 'SELECT 1';PREPARE "
            + proof.OuterStatementName + " FROM 'SELECT 2';");

        // Act
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));
        var innerFailure = await Record.ExceptionAsync(() =>
            ExecuteAsync(connection, "EXECUTE " + proof.InnerStatementName + ";"));
        var outerFailure = await Record.ExceptionAsync(() =>
            ExecuteAsync(connection, "EXECUTE " + proof.OuterStatementName + ";"));

        // Assert
        Assert.Equal(1243, Assert.IsType<MySqlException>(innerFailure).Number);
        Assert.Equal(1243, Assert.IsType<MySqlException>(outerFailure).Number);
    }

    /// <summary>False or unknown catalog barriers cannot acquire a positive proof result.</summary>
    /// <param name="executionGuard">The caller's accepted-action barrier.</param>
    /// <param name="eligibility">The catalog selection barrier for this head.</param>
    [MariaDbIdentifierProofTheory]
    [InlineData("FALSE", "TRUE")]
    [InlineData("NULL", "TRUE")]
    [InlineData("TRUE", "FALSE")]
    [InlineData("TRUE", "NULL")]
    public async Task FalseBarriersRemainFailClosed(string executionGuard, string eligibility)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var proof = CreateProof("id");

        // Act
        await ExecuteAsync(connection,
            MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, executionGuard, eligibility));
        var result = await ScalarAsync(connection, "SELECT " + proof.ResultVariable + ";");
        await ExecuteAsync(connection, MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));

        // Assert
        Assert.Equal(0, Convert.ToInt32(result, CultureInfo.InvariantCulture));
    }

    private static MySqlSafeMigrationIdentifierReferenceProof CreateProof(string head, string? scope = null) =>
        new(MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix + "0__", head,
            scope ?? Guid.NewGuid().ToString("N"));

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(
        MySqlConnection connection,
        string sql,
        MySqlTransaction? transaction = null
    )
    {
        await using var command = new MySqlCommand(sql, connection, transaction);

        return await command.ExecuteScalarAsync();
    }
}

/// <summary>Limits anonymous compound preparation tests to the MariaDB engine that supports this protocol.</summary>
internal sealed class MariaDbIdentifierProofFactAttribute : FactAttribute
{
    public MariaDbIdentifierProofFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE"),
                "mysql", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "MariaDB-only parse proof: MySQL exposes native reservedness metadata instead.";
        }
    }
}

/// <summary>Limits native parse-proof matrices to MariaDB without reporting unexecuted MySQL cases as passed.</summary>
internal sealed class MariaDbIdentifierProofTheoryAttribute : TheoryAttribute
{
    public MariaDbIdentifierProofTheoryAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE"),
                "mysql", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "MariaDB-only parse proof: MySQL exposes native reservedness metadata instead.";
        }
    }
}
