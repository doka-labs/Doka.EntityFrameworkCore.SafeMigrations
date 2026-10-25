namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerGuardRenderingAllocationTests
{
    /// <summary>
    /// Preserves provider order, exact SQL, suppression flags and delegated execution without enumeration.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void CommandWrapping_IndexesCompletedBatchAndRetainsEveryProviderBoundary(
        int commandCount,
        bool prepend
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var connection = context.GetService<IRelationalConnection>();
        var commands = Enumerable.Range(0, commandCount).Select(index => new RecordingCommand(
            dependencies.CommandBuilderFactory.Create().Append($"SELECT N'O''Brien {index}';\n").Build(),
            context, dependencies, index + 7, (index % 2) == 1)).ToArray();

        var source = new IndexedOnlyCommands(commands);
        var prefix = CreateCommand(context, "SELECT N'ordinary prefix';\n");
        var destination = new List<MigrationCommand> { prefix };
        IReadOnlyDictionary<string, object?> parameters = new Dictionary<string, object?> { ["sentinel"] = 7 };
        var sourceOffset = prepend ? 0 : 1;
        var prefixOffset = prepend ? commandCount : 0;

        // Act
        if (prepend)
        {
            SqlServerSafeMigrationsSqlGenerator.PrependGuardedCommands(
                destination, source, dependencies, analyzer, metadata);
        }
        else
        {
            SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
                destination, source, dependencies, analyzer, metadata);
        }

        var results = destination.Skip(sourceOffset).Take(commandCount)
            .Select(command => command.ExecuteNonQuery(connection, parameters)).ToArray();

        // Assert
        Assert.Same(prefix, destination[prefixOffset]);
        Assert.Equal(commandCount + 1, destination.Count);
        Assert.Equal(Enumerable.Range(7, commandCount), results);
        for (var index = 0; index < commandCount; index++)
        {
            var wrapped = Assert.IsType<SqlServerSafeMigrationGuardedCommand>(destination[index + sourceOffset]);
            Assert.Equal(commands[index].CommandText, wrapped.CommandText);
            Assert.Equal(commands[index].TransactionSuppressed, wrapped.TransactionSuppressed);
            Assert.Same(commands[index].CommandLogger, wrapped.CommandLogger);
            Assert.Equal(1, commands[index].Executions);
            Assert.Same(parameters, commands[index].ReceivedParameters);
        }
    }

    /// <summary>Rejects cached indexed wrappers after quarantine without invoking their provider command.</summary>
    [Fact]
    public void CommandWrapping_QuarantineStillPreventsDelegatedExecution()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var command = new RecordingCommand(dependencies.CommandBuilderFactory.Create().Append("SELECT 7;").Build(),
            context, dependencies, 7, false);

        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var connection = context.GetService<IRelationalConnection>();
        var destination = new List<MigrationCommand>();
        SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            destination, new IndexedOnlyCommands([command]), dependencies, analyzer, metadata);

        analyzer.QuarantineConnection();

        // Act
        var failure = Record.Exception(() => destination[0].ExecuteNonQuery(connection));

        // Assert
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Equal(0, command.Executions);
        Assert.Equal(ConnectionState.Closed, connection.DbConnection.State);
    }

    /// <summary>Counts prepared wrapping against the former captured selector without measuring list growth.</summary>
    [Fact]
    public void CommandWrapping_AllocatesLessThanOriginalCapturedSelector()
    {
        // Arrange
        const int renderCount = 128;
        using var context = new SafeMigrationDbContext(ConnectionString);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var metadata = dependencies.CommandBuilderFactory.Create().Build();
        var source = Array.AsReadOnly(new[] { CreateCommand(context, "SELECT 7;\n") });
        var currentDestinations = Enumerable.Range(0, renderCount)
            .Select(_ => new List<MigrationCommand>(source.Count)).ToArray();

        var originalDestinations = Enumerable.Range(0, renderCount)
            .Select(_ => new List<MigrationCommand>(source.Count)).ToArray();

        Action<int> current = index => SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            currentDestinations[index], source, dependencies, analyzer, metadata);

        Action<int> original = index => originalDestinations[index].AddRange(source.Select(command =>
            new SqlServerSafeMigrationGuardedCommand(command, dependencies, analyzer, metadata)));

        // WHY: Prepare both destination capacities and warm wrapper creation
        // before counting. The former per-operation selector is the only extra
        // allocation work under comparison; neither path executes SQL.
        SqlServerSafeMigrationsSqlGenerator.AppendGuardedCommands(
            new List<MigrationCommand>(source.Count), source, dependencies, analyzer, metadata);

        new List<MigrationCommand>(source.Count).AddRange(source.Select(command =>
            new SqlServerSafeMigrationGuardedCommand(command, dependencies, analyzer, metadata)));

        // Act
        var currentBytes = MeasureWrappingAllocations(current, renderCount);
        var originalBytes = MeasureWrappingAllocations(original, renderCount);

        // Assert
        Assert.InRange(currentBytes, 1, originalBytes - 1);
        Assert.All(currentDestinations.Concat(originalDestinations), destination =>
        {
            var wrapped = Assert.IsType<SqlServerSafeMigrationGuardedCommand>(Assert.Single(destination));
            Assert.Equal(source[0].CommandText, wrapped.CommandText);
            Assert.Equal(source[0].TransactionSuppressed, wrapped.TransactionSuppressed);
            Assert.Same(source[0].CommandLogger, wrapped.CommandLogger);
        });
    }
}
