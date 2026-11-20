namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Shares the exact native catalog and legacy-runtime fixtures without a test-framework dependency.</summary>
internal static class MySqlLiveBenchmarkWorkload
{
    internal const int ManagedTableCount = 100;
    internal const int ForeignTableCount = 1000;
    internal const string ManagedTablePrefix = "expected_perf_";
    internal const string ForeignTablePrefix = "foreign_perf_";
    private const string DatabasePrefix = "sm_bdn_";

    /// <summary>Reads the unchanged complete ordered column, index and key shape without GROUP_CONCAT.</summary>
    internal const string RuntimeSchemaSql = """
        SELECT CONCAT(TABLE_NAME, ':', COLUMN_NAME, ':', ORDINAL_POSITION, ':',
        COLUMN_TYPE, ':', IS_NULLABLE, ':', COALESCE(COLLATION_NAME, ''))
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME LIKE 'runtime_records_%' UNION ALL SELECT CONCAT(TABLE_NAME,
        ':index:', INDEX_NAME, ':', NON_UNIQUE, ':', SEQ_IN_INDEX, ':', COLUMN_NAME,
        ':', COALESCE(SUB_PART, '')) FROM INFORMATION_SCHEMA.STATISTICS
        WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME LIKE 'runtime_records_%' UNION ALL SELECT CONCAT(TABLE_NAME,
        ':key:', CONSTRAINT_NAME, ':', COLUMN_NAME, ':', ORDINAL_POSITION, ':',
        COALESCE(REFERENCED_TABLE_NAME, ''), ':', COALESCE(REFERENCED_COLUMN_NAME,
        '')) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
        WHERE CONSTRAINT_SCHEMA = DATABASE()
        AND TABLE_NAME LIKE 'runtime_records_%' UNION ALL SELECT CONCAT(TABLE_NAME,
        ':fk:', CONSTRAINT_NAME, ':', REFERENCED_TABLE_NAME, ':', UPDATE_RULE,
        ':', DELETE_RULE) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
        WHERE CONSTRAINT_SCHEMA = DATABASE()
        AND TABLE_NAME LIKE 'runtime_records_%' ORDER BY 1;
        """;

    /// <summary>Builds only the one-column tables covered by the native analysis workload.</summary>
    internal static string BuildManagedTables() => BuildCatalogTables(ManagedTablePrefix, ManagedTableCount, false);

    /// <summary>Builds the unrelated tables and their nonunique payload indexes.</summary>
    internal static string BuildForeignTables() => BuildCatalogTables(ForeignTablePrefix, ForeignTableCount, true);

    /// <summary>Creates the unchanged ordered strict EnsureTable operations used by native analysis.</summary>
    /// <param name="providerName">The real provider identifier obtained from the configured context.</param>
    internal static List<MigrationOperation> CreateCatalogOperations(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        for (var ordinal = 0; ordinal < ManagedTableCount; ordinal++)
        {
            var table = ManagedTablePrefix + ordinal.ToString("D4", CultureInfo.InvariantCulture);
            builder.EnsureTable(
                new ExpectedTableDefinition(
                    table,
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        // WHY: The benchmark and test each own a stable operation snapshot, not the mutable builder collection.
        // ReSharper disable once UseCollectionExpression
        return builder.Operations.ToList();
    }

    /// <summary>Requires every managed assessment and the complete unrelated-table inventory.</summary>
    /// <param name="report">The actual runner result observed outside the timed benchmark method.</param>
    /// <param name="foreignTableCount">Zero for the clean catalog or the exact noisy fixture size.</param>
    internal static void VerifyCatalogReport(
        SafeMigrationRunReport report,
        int foreignTableCount
    )
    {
        if (foreignTableCount is not (0 or ForeignTableCount)
            || report.Status != SafeMigrationReportStatus.Ready
            || report.Assessments.Count != ManagedTableCount
            || report.UnexpectedObjects.Count != foreignTableCount)
        {
            throw new InvalidOperationException("The live catalog workload returned an incomplete inventory.");
        }

        for (var ordinal = 0; ordinal < ManagedTableCount; ordinal++)
        {
            var assessment = report.Assessments[ordinal];
            if (assessment.Ordinal != ordinal
                || assessment.OperationType != typeof(SafeMigrationOperation).FullName
                || !assessment.IsSafeOperation
                || assessment.OperationKind != SafeMigrationOperationKind.EnsureTable
                || assessment.ObjectName != ManagedTablePrefix + ordinal.ToString("D4", CultureInfo.InvariantCulture)
                || assessment.ObservedState != SafeMigrationObservedState.Matching
                || assessment.Action != SafeMigrationAction.NoOp
                || assessment.PostconditionSatisfied != true)
            {
                throw new InvalidOperationException("The live catalog workload changed an ordered managed contract.");
            }
        }

        if (report.UnexpectedObjects.Any(static value => value.ObjectKind != SafeMigrationDatabaseObjectKind.Table))
        {
            throw new InvalidOperationException("The live catalog workload reported an unexpected child object.");
        }
    }

    /// <summary>Creates an unguessable, identifier-safe database name owned by this diagnostic invocation.</summary>
    internal static string CreateDatabaseName() => DatabasePrefix
        + Guid
            .NewGuid()
            .ToString("N");

    /// <summary>Rejects inherited database names before any destructive fixture statement is constructed.</summary>
    /// <param name="database">The generated name retained by the database owner.</param>
    internal static void RequireOwnedDatabaseName(
        string database
    )
    {
        if (!database.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(database.AsSpan(DatabasePrefix.Length), "N", out _))
        {
            throw new InvalidOperationException("Only a generated live-benchmark database may be reset or removed.");
        }
    }

    /// <summary>Constructs one unchanged legacy table without changing server or durability defaults.</summary>
    /// <param name="initialState">One of the four authored runtime starting states.</param>
    /// <param name="ordinal">The table position in the existing runtime migration.</param>
    internal static string BuildLegacyRuntimeTable(
        string initialState,
        int ordinal
    )
    {
        RequireInitialState(initialState);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, MySqlRuntimeConvergenceMigration.TableCount);
        if (initialState == "empty")
        {
            return string.Empty;
        }

        var table = MySqlRuntimeConvergenceMigration.TableName(ordinal);
        var width = initialState == "widening"
            ? 20
            : 80;

        var columnCount = initialState == "partial"
            ? 3
            : 9;

        var sql = new StringBuilder($"CREATE TABLE `{table}` (`Id` int NOT NULL, ");
        sql.Append(CultureInfo.InvariantCulture, $"`Code` varchar({width}) NOT NULL, `ParentId` int NULL");
        for (var column = 1; column <= columnCount; column++)
        {
            sql.Append(CultureInfo.InvariantCulture, $", `Value{column:D2}` varchar(40) NULL");
        }

        if (initialState == "matching")
        {
            sql.Append(CultureInfo.InvariantCulture,
                $", PRIMARY KEY (`Id`), CONSTRAINT `UQ_{table}_Code` UNIQUE (`Code`), ");

            sql.Append(CultureInfo.InvariantCulture, $"KEY `IX_{table}_ParentId` (`ParentId`)");
            if (ordinal > 0)
            {
                var parent = MySqlRuntimeConvergenceMigration.TableName(ordinal - 1);
                sql.Append(CultureInfo.InvariantCulture,
                    $", CONSTRAINT `FK_{table}_Parent` FOREIGN KEY (`ParentId`) ");

                sql.Append(CultureInfo.InvariantCulture, $"REFERENCES `{parent}` (`Id`) ON DELETE RESTRICT");
            }
        }

        sql.Append("); ");
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO `{table}` (`Id`, `Code`, `ParentId`, `Value01`) ");
        sql.Append(CultureInfo.InvariantCulture,
            $"VALUES (1, 'preserved', {(ordinal == 0 ? "NULL" : "1")}, 'original');");

        return sql.ToString();
    }

    /// <summary>Rejects misspelled states rather than silently measuring a different native fixture.</summary>
    /// <param name="initialState">The nonsecret benchmark parameter or test input.</param>
    internal static void RequireInitialState(
        string initialState
    )
    {
        if (initialState is not ("empty" or "matching" or "partial" or "widening"))
        {
            throw new ArgumentException("The runtime fixture state is not supported.", nameof(initialState));
        }
    }

    /// <summary>Prevents an overridden BDN invocation count from mixing first application with replay.</summary>
    /// <param name="consumed">The fixture flag reset only by the real iteration-setup callback.</param>
    internal static void ConsumeRuntimeIteration(
        ref bool consumed
    )
    {
        if (consumed)
        {
            throw new InvalidOperationException(
                "A mutating live runtime benchmark requires one invocation per iteration.");
        }

        consumed = true;
    }

    /// <summary>Parses private configuration without exposing rejected option names or values.</summary>
    /// <param name="connectionString">The inherited private server configuration, never a benchmark parameter.</param>
    internal static MySqlConnectionStringBuilder ParseConnectionSettings(
        string connectionString
    )
    {
        try
        {
            return new MySqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("The live MySQL benchmark connection configuration is invalid.");
        }
    }

    /// <summary>Redacts external failure text while preserving readable authored invariant diagnostics.</summary>
    /// <param name="exception">The original failure retained privately until owned cleanup has finished.</param>
    internal static Exception ProtectFailure(
        Exception exception
    )
    {
        if (exception is OperationCanceledException canceled)
        {
            return new OperationCanceledException("The live MySQL benchmark was canceled.", canceled.CancellationToken);
        }

        // WHY: Provider wrappers can carry authentication details in either their message or inner chain.
        // Original failures stay private for cleanup priority; emitted errors contain neither InnerException nor Data.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException native)
            {
                return new InvalidOperationException("The live MySQL benchmark failed with native error "
                    + native.Number.ToString(CultureInfo.InvariantCulture)
                    + ".");
            }

            if (current is DbException or IOException)
            {
                return new InvalidOperationException("The live MySQL benchmark native IO failed.");
            }
        }

        return new InvalidOperationException(exception.Message);
    }

    private static string BuildCatalogTables(
        string prefix,
        int count,
        bool includeIndex
    ) => string.Join(
        Environment.NewLine,
        Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var table = prefix + index.ToString("D4", CultureInfo.InvariantCulture);
                var indexDdl = includeIndex
                    ? ", INDEX `ix_payload` (`payload`)"
                    : string.Empty;

                var payloadColumn = includeIndex
                    ? ", `payload` int NULL"
                    : string.Empty;

                return $"CREATE TABLE `{table}` (`id` int NOT NULL{payloadColumn}{indexDdl});";
            }));
}
