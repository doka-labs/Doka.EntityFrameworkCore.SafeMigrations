namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Validates real EF fixture identities and script generation without a SQL Server host.</summary>
public sealed class SqlServerMigrationFixtureTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1;Database=fixture_identity;Integrated Security=True;Encrypt=False;Connect Timeout=1;";

    /// <summary>Every declared test migration uses the actual EF migration-ID contract.</summary>
    /// <param name="registerSafeMigrations">Whether the SafeMigrations services replace the EF services.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredFixtureIdsUseEfTimestampFormat(
        bool registerSafeMigrations
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations);
        var idGenerator = context.GetService<IMigrationsIdGenerator>();

        // Act
        var ids = typeof(SqlServerMigrationFixtureTests).Assembly.GetTypes()
            .Where(static type => typeof(Migration).IsAssignableFrom(type))
            .SelectMany(static type => type.GetCustomAttributes(typeof(MigrationAttribute), inherit: false))
            .Cast<MigrationAttribute>()
            .Select(static attribute => attribute.Id)
            .ToArray();

        // Assert
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.True(idGenerator.IsValidId(id), $"Invalid EF fixture migration ID: {id}"));
    }

    /// <summary>The history fixture resolves both by its complete ID and its short name.</summary>
    /// <param name="registerSafeMigrations">Whether the SafeMigrations services replace the EF services.</param>
    /// <param name="byName">Whether the caller supplies the short name instead of the full ID.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HistoryMigrationResolvesByIdAndName(
        bool registerSafeMigrations,
        bool byName
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations);
        var assembly = context.GetService<IMigrationsAssembly>();
        var target = byName ? "SqlServerHistory" : SqlServerHistoryMigration.MigrationIdentifier;

        // Act
        var resolvedId = assembly.FindMigrationId(target);

        // Assert
        Assert.Equal(SqlServerHistoryMigration.MigrationIdentifier, resolvedId);
        Assert.Equal(typeof(SqlServerHistoryMigration), assembly.Migrations[resolvedId!].AsType());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Both real script modes resolve the fixture before any live database is needed.</summary>
    /// <param name="idempotent">Whether EF generates replayable migration-history guards.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryScriptsGenerateWithoutDatabase(
        bool idempotent
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var migrator = context.GetService<IMigrator>();
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;
        var historyPredicate = $"WHERE [MigrationId] = N'{SqlServerHistoryMigration.MigrationIdentifier}'";

        // WHY: These are ordinary tests, not live-theory cases. Target resolution
        // and SQL generation must fail locally even when an ARM host skips DDL.
        // Act
        var script = migrator.GenerateScript(
            Migration.InitialDatabase, SqlServerHistoryMigration.MigrationIdentifier, options);

        // Assert
        Assert.Contains(SqlServerHistoryMigration.MigrationIdentifier, script, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);
        Assert.Contains("safe_history_probe", script, StringComparison.Ordinal);
        Assert.Contains("CK_safe_history_probe_Id", script, StringComparison.Ordinal);
        Assert.Contains("EXEC sys.sp_executesql", script, StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_state", script, StringComparison.Ordinal);

        if (idempotent)
        {
            Assert.Contains("IF NOT EXISTS (", script, StringComparison.Ordinal);
            Assert.Contains(historyPredicate, script, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(historyPredicate, script, StringComparison.Ordinal);
        }

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Malformed and unknown explicit targets remain rejected rather than silently selecting a migration.</summary>
    /// <param name="registerSafeMigrations">Whether the SafeMigrations services replace the EF services.</param>
    /// <param name="target">The invalid or nonexistent requested migration.</param>
    [Theory]
    [InlineData(false, "202609300001_SqlServerHistory")]
    [InlineData(true, "202609300001_SqlServerHistory")]
    [InlineData(false, "20990101000000_MissingMigration")]
    [InlineData(true, "20990101000000_MissingMigration")]
    public void InvalidMigrationTargetsRemainRejected(
        bool registerSafeMigrations,
        string target
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations);
        var migrator = context.GetService<IMigrator>();

        // Act
        var exception = Record.Exception(() => migrator.GenerateScript(Migration.InitialDatabase, target));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains(target, failure.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
