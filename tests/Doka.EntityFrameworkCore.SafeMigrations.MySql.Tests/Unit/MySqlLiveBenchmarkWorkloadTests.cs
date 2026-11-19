namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects the real native benchmark fixture, ownership guards and deterministic result oracles.</summary>
public sealed class MySqlLiveBenchmarkWorkloadTests
{
    /// <summary>Retains exactly the authored managed or unrelated table population and native defaults.</summary>
    /// <param name="foreign">Whether the fixture is the unrelated indexed catalog population.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogSqlPreservesExactPopulationAndNativeDefaults(
        bool foreign
    )
    {
        // Arrange
        var prefix = foreign
            ? MySqlLiveBenchmarkWorkload.ForeignTablePrefix
            : MySqlLiveBenchmarkWorkload.ManagedTablePrefix;

        var count = foreign
            ? MySqlLiveBenchmarkWorkload.ForeignTableCount
            : MySqlLiveBenchmarkWorkload.ManagedTableCount;

        // Act
        var sql = foreign
            ? MySqlLiveBenchmarkWorkload.BuildForeignTables()
            : MySqlLiveBenchmarkWorkload.BuildManagedTables();

        // Assert
        var statements = sql.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count, statements.Length);
        for (var ordinal = 0; ordinal < statements.Length; ordinal++)
        {
            var table = prefix + ordinal.ToString("D4", CultureInfo.InvariantCulture);
            Assert.Equal(
                foreign
                    ? $"CREATE TABLE `{table}` (`id` int NOT NULL, `payload` int NULL, INDEX `ix_payload` (`payload`));"
                    : $"CREATE TABLE `{table}` (`id` int NOT NULL);",
                statements[ordinal]);
        }

        Assert.DoesNotContain("ENGINE=", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retains ordered strict table operations instead of replacing native analysis with generation.</summary>
    [Fact]
    public void CatalogOperationsPreserveEveryAuthoredIdentity()
    {
        // Arrange
        const string providerName = "Doka.EntityFrameworkCore.MySql";

        // Act
        var operations = MySqlLiveBenchmarkWorkload.CreateCatalogOperations(providerName);

        // Assert
        Assert.Equal(100, operations.Count);
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = Assert.IsType<SafeMigrationOperation>(operations[ordinal]);
            var intent = Assert.IsType<EnsureTableIntent>(operation.Intent);
            Assert.Equal(
                "expected_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
                intent.Definition.Table);

            Assert.Equal(SafeMigrationTableMode.StrictDefinition, intent.Mode);
            Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, operation.Policy);
            var column = Assert.Single(intent.Definition.Columns);
            Assert.Equal("id", column.Name);
            Assert.Equal("int", column.StoreType);
            Assert.False(column.IsNullable);
        }
    }

    /// <summary>Retains each real legacy starting state without modifying database durability settings.</summary>
    /// <param name="state">The independently authored legacy starting state.</param>
    /// <param name="width">The initial Code character domain.</param>
    /// <param name="values">The count of preexisting generic value columns.</param>
    /// <param name="hasKeys">Whether keys and the parent chain already exist.</param>
    [Theory]
    [InlineData("empty", 0, 0, false)]
    [InlineData("matching", 80, 9, true)]
    [InlineData("partial", 80, 3, false)]
    [InlineData("widening", 20, 9, false)]
    public void RuntimeInitialStatesPreserveLegacySql(
        string state,
        int width,
        int values,
        bool hasKeys
    )
    {
        // Arrange
        const int ordinal = 1;

        // Act
        var sql = MySqlLiveBenchmarkWorkload.BuildLegacyRuntimeTable(state, ordinal);

        // Assert
        if (state == "empty")
        {
            Assert.Empty(sql);

            return;
        }

        Assert.StartsWith("CREATE TABLE `runtime_records_01`", sql, StringComparison.Ordinal);
        Assert.Contains($"`Code` varchar({width}) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"`Value{values:D2}` varchar(40) NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"`Value{values + 1:D2}`", sql, StringComparison.Ordinal);
        Assert.Equal(hasKeys, sql.Contains("PRIMARY KEY", StringComparison.Ordinal));
        Assert.Equal(hasKeys, sql.Contains("UNIQUE", StringComparison.Ordinal));
        Assert.Equal(hasKeys, sql.Contains("REFERENCES `runtime_records_00`", StringComparison.Ordinal));
        Assert.Contains("VALUES (1, 'preserved', 1, 'original');", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SET ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENGINE=", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects an invalid state before constructing or executing native fixture SQL.</summary>
    /// <param name="state">The unsupported or injection-shaped state selector.</param>
    [Theory]
    [InlineData("")]
    [InlineData("Matching")]
    [InlineData("matching; DROP DATABASE inherited")]
    public void UnsupportedRuntimeStateIsRejectedBeforeSql(
        string state
    )
    {
        // Arrange
        const int ordinal = 0;

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.BuildLegacyRuntimeTable(state, ordinal));

        // Assert
        Assert.Equal("initialState",
            Assert.IsType<ArgumentException>(failure)
                .ParamName);
    }

    /// <summary>Accepts only a newly generated GUID-shaped diagnostic database identifier.</summary>
    [Fact]
    public void GeneratedDatabaseNamePassesTheDestructiveOwnershipGuard()
    {
        // Arrange
        var database = MySqlLiveBenchmarkWorkload.CreateDatabaseName();

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.RequireOwnedDatabaseName(database));

        // Assert
        Assert.Null(failure);
        Assert.StartsWith("sm_bdn_", database, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(database.AsSpan(7), "N", out _));
    }

    /// <summary>Rejects inherited, malformed and SQL-shaped database names before reset or DROP.</summary>
    /// <param name="database">A name that was not generated by the diagnostic owner.</param>
    [Theory]
    [InlineData("inherited_database")]
    [InlineData("sm_bdn_existing")]
    [InlineData("sm_bdn_00000000000000000000000000000000`; DROP DATABASE inherited")]
    public void DestructiveOwnershipGuardRejectsForeignNames(
        string database
    )
    {
        // Arrange
        var candidate = database;

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.RequireOwnedDatabaseName(candidate));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Allows the first actual migration invocation after the framework resets its iteration state.</summary>
    [Fact]
    public void RuntimeIterationAdmitsItsFirstInvocation()
    {
        // Arrange
        var consumed = false;

        // Act
        MySqlLiveBenchmarkWorkload.ConsumeRuntimeIteration(ref consumed);

        // Assert
        Assert.True(consumed);
    }

    /// <summary>Rejects a second invocation instead of silently mixing initial application and replay.</summary>
    [Fact]
    public void RuntimeIterationRejectsAnAdditionalInvocation()
    {
        // Arrange
        var consumed = true;

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.ConsumeRuntimeIteration(ref consumed));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.True(consumed);
    }

    /// <summary>Accepts both complete deterministic native catalog populations.</summary>
    /// <param name="foreignCount">Zero or the exact authored unrelated-table count.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void CatalogOracleAcceptsCompleteResults(
        int foreignCount
    )
    {
        // Arrange
        var report = CreateReport(100, foreignCount);

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.VerifyCatalogReport(report, foreignCount));

        // Assert
        Assert.Null(failure);
    }

    /// <summary>Rejects missing, shortened and oversized assessment streams before per-item checks.</summary>
    /// <param name="count">The incorrect actual managed assessment count.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(101)]
    public void CatalogOracleRejectsIncompleteManagedStreams(
        int count
    )
    {
        // Arrange
        var report = CreateReport(count, 0);

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.VerifyCatalogReport(report, 0));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Rejects each changed managed assessment facet while all other rows remain valid.</summary>
    /// <param name="facet">The independently corrupted result field.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("operation_type")]
    [InlineData("safe_flag")]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("state")]
    [InlineData("action")]
    [InlineData("postcondition")]
    public void CatalogOracleRejectsChangedManagedIdentity(
        string facet
    )
    {
        // Arrange
        var report = CreateReport(100, 0, facet);

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.VerifyCatalogReport(report, 0));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Rejects incomplete or child-bearing unrelated inventories without relaxing managed checks.</summary>
    /// <param name="count">The actual unrelated-object count.</param>
    /// <param name="hasChild">Whether one unrelated object is a column rather than a table.</param>
    [Theory]
    [InlineData(999, false)]
    [InlineData(1001, false)]
    [InlineData(1000, true)]
    public void CatalogOracleRejectsChangedForeignInventory(
        int count,
        bool hasChild
    )
    {
        // Arrange
        var report = CreateReport(100, count, child: hasChild);

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.VerifyCatalogReport(report, 1000));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Redacts external failure text without exporting its private exception graph.</summary>
    [Fact]
    public void NativeFailureRedactionDoesNotPublishPrivateDetails()
    {
        // Arrange
        const string canary = "do-not-log-connection-secret";
        var original = new IOException("Server=example;Password=" + canary)
        {
            Data = { ["private"] = canary },
        };

        // Act
        var safe = MySqlLiveBenchmarkWorkload.ProtectFailure(original);

        // Assert
        Assert.DoesNotContain(canary, safe.ToString(), StringComparison.Ordinal);
        Assert.Null(safe.InnerException);
        Assert.Empty(safe.Data);
    }

    /// <summary>Finds external failures inside provider wrappers without emitting either private message.</summary>
    [Fact]
    public void NativeFailureRedactionChecksWrappedTransportFailures()
    {
        // Arrange
        const string canary = "do-not-log-connection-secret";
        var original = new InvalidOperationException(canary, new IOException(canary));

        // Act
        var safe = MySqlLiveBenchmarkWorkload.ProtectFailure(original);

        // Assert
        Assert.DoesNotContain(canary, safe.ToString(), StringComparison.Ordinal);
        Assert.Null(safe.InnerException);
        Assert.Empty(safe.Data);
    }

    /// <summary>Preserves cooperative cancellation and its token without exporting private error text.</summary>
    [Fact]
    public void NativeFailureRedactionPreservesCancellationIdentity()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("do-not-log-secret", cancellation.Token);

        // Act
        var safe = MySqlLiveBenchmarkWorkload.ProtectFailure(original);

        // Assert
        Assert.Equal(cancellation.Token,
            Assert.IsType<OperationCanceledException>(safe)
                .CancellationToken);

        Assert.DoesNotContain("do-not-log-secret", safe.ToString(), StringComparison.Ordinal);
        Assert.Empty(safe.Data);
        Assert.Null(safe.InnerException);
    }

    /// <summary>Retains readable authored invariant diagnostics without publishing attached native failures.</summary>
    [Fact]
    public void NativeFailureRedactionPreservesInvariantExplanation()
    {
        // Arrange
        const string explanation = "The live runtime workload changed its deterministic fixture contract.";
        var original = new InvalidOperationException(explanation)
        {
            Data = { ["cleanup"] = new IOException("do-not-log-secret") },
        };

        // Act
        var safe = MySqlLiveBenchmarkWorkload.ProtectFailure(original);

        // Assert
        Assert.Equal(explanation, safe.Message);
        Assert.Empty(safe.Data);
        Assert.Null(safe.InnerException);
        Assert.DoesNotContain("do-not-log-secret", safe.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Parses an explicit private connection without involving native IO.</summary>
    [Fact]
    public void ConnectionConfigurationPreservesItsNativeOptions()
    {
        // Arrange
        const string connection = "Server=localhost;Database=inherited;User ID=user;Password=private;Port=3307";

        // Act
        var settings = MySqlLiveBenchmarkWorkload.ParseConnectionSettings(connection);

        // Assert
        Assert.Equal("localhost", settings.Server);
        Assert.Equal("inherited", settings.Database);
        Assert.Equal("private", settings.Password);
        Assert.Equal(3307u, settings.Port);
    }

    /// <summary>Rejects malformed options without printing their private values or exception graph.</summary>
    /// <param name="connection">The invalid option or value containing a diagnostic canary.</param>
    [Theory]
    [InlineData("do-not-log-secret=private")]
    [InlineData("Port=do-not-log-secret")]
    [InlineData("SslMode=do-not-log-secret")]
    public void ConnectionConfigurationRejectsPrivateInvalidValues(
        string connection
    )
    {
        // Arrange
        var candidate = connection;

        // Act
        var failure = Record.Exception(() => MySqlLiveBenchmarkWorkload.ParseConnectionSettings(candidate));

        // Assert
        var safe = Assert.IsType<InvalidOperationException>(failure);
        Assert.DoesNotContain("do-not-log-secret", safe.ToString(), StringComparison.Ordinal);
        Assert.Empty(safe.Data);
        Assert.Null(safe.InnerException);
    }

    private static SafeMigrationRunReport CreateReport(
        int managedCount,
        int foreignCount,
        string? facet = null,
        bool child = false
    ) => new(
        SafeMigrationReportMode.Preflight,
        SafeMigrationReportStatus.Ready,
        DateTimeOffset.UnixEpoch,
        "mysql-live-benchmark-controls",
        new SafeMigrationProviderEnvironment("doka_mysql", "mysql", "8.4.13"),
        null,
        "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:" + new string('a', 64),
        new string('b', 64),
        Enumerable
            .Range(0, managedCount)
            .Select(ordinal => CreateAssessment(
                ordinal,
                ordinal == 0
                    ? facet
                    : null)),
        Enumerable
            .Range(0, foreignCount)
            .Select(ordinal => new SafeMigrationUnexpectedObject(
                child && ordinal == 0
                    ? SafeMigrationDatabaseObjectKind.Column
                    : SafeMigrationDatabaseObjectKind.Table,
                null,
                null,
                "foreign_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
                "unexpected_object")));

    private static SafeMigrationAssessment CreateAssessment(
        int ordinal,
        string? facet
    ) => new(
        facet == "ordinal"
            ? ordinal + 1
            : ordinal,
        facet == "operation_type"
            ? typeof(SqlOperation).FullName!
            : typeof(SafeMigrationOperation).FullName!,
        facet != "safe_flag",
        facet == "kind"
            ? SafeMigrationOperationKind.EnsureColumn
            : SafeMigrationOperationKind.EnsureTable,
        facet == "name"
            ? "different"
            : "expected_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
        facet == "state"
            ? SafeMigrationObservedState.Different
            : SafeMigrationObservedState.Matching,
        facet == "action"
            ? SafeMigrationAction.Repair
            : SafeMigrationAction.NoOp,
        facet != "postcondition",
        "matching");
}
