namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Shares helpers and preserves nested model identities without declaring test cases.</summary>
/// <remarks>
/// WHY: Models stay on their original CLR owner so EF model-cache and fixture identities do not drift.
/// </remarks>
public abstract class PostgreSqlSafeMigrationIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Provides the NullProofSentinelSql contract shared by PostgreSQL feature tests.</summary>
    protected const string NullProofSentinelSql = $"""
        CREATE FUNCTION null_proof_sentinel() RETURNS boolean LANGUAGE plpgsql AS
        {"$null_proof$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P1901', "
            + "MESSAGE = 'null_proof_branch_executed'; END $null_proof$;"}
        """;

    /// <summary>Bounds setup commands for the large performance fixtures.</summary>
    protected const int PerformanceFixtureCommandTimeoutSeconds = 180;

    /// <summary>Uses the assembly-owned fixture without taking container ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    protected PostgreSqlSafeMigrationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Waits for the named narrowing command to reach its server-observed table lock.</summary>
    /// <param name="connectionString">The independently owned observer connection.</param>
    /// <param name="table">The bounded fixture table identifier.</param>
    /// <param name="pending">The actual producer whose completion makes a missing lock proof terminal.</param>
    /// <param name="cancellationToken">Cancels forward observation without abandoning the producer.</param>
    protected static async Task WaitForBlockedPostgreSqlNarrowingCommandAsync(
        string connectionString,
        string table,
        Task pending,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // WHY: Slow scheduling is not a failed lock contract. The actual producer
        // and caller token bound observation, not a fixed number of polling sleeps.
        while (!pending.IsCompleted)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid <> pg_backend_pid()
            AND query LIKE @probe AND wait_event_type = 'Lock');
            """;

            // WHY: PostgreSQL truncates the recorded query text to
            // track_activity_query_size. The target table occurs at the start
            // of the guarded command and remains a stable lock identifier.
            command.Parameters.AddWithValue("probe", $"%{table}%");

            var blocked = Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (blocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new InvalidOperationException("The PostgreSQL operation completed before its blocked table lock.");
    }

    /// <summary>Builds real catalog predicates without retaining a live catalog snapshot.</summary>
    private protected static PostgreSqlSafeMigrationRuntimePlan BuildNullProofEligibilityPlan(
        DbContext context,
        SafeMigrationOperation operation
    ) => new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>())
        .Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true);

    /// <summary>Builds a source-frozen standalone filtered-index operation through the public authoring path.</summary>
    protected static MigrationBuilder SimpleFilterBuilder(
        DbContext context,
        string filter,
        bool unique = false,
        string name = "simple_filter_lookup"
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateCompositeIndexIfNotExistsFromModel(
            name,
            "simple_filters",
            ["id"],
            schema: "hierarchy",
            unique: unique,
            filter: filter);

        return builder;
    }

    /// <summary>Creates only test-owned schema and mixed null/non-null rows for the predicate boundary.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="column">The fixture column whose physical contract is inspected.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static Task CreateSimpleFilterTableAsync(
        string connectionString,
        string column,
        CancellationToken cancellationToken
    ) => ExecuteSqlAsync(
        connectionString,
        $"""
        CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters (id integer
        NOT NULL,
        {PostgreSqlIdentifier(column)} integer NULL);INSERT INTO hierarchy.simple_filters
        VALUES (1,
        NULL), (2, 1), (3, 2);
        """,
        cancellationToken: cancellationToken);

    /// <summary>Counts the independently managed filtered index in the exact test-owned schema.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static Task<int> SimpleFilterIndexCountAsync(
        string connectionString,
        CancellationToken cancellationToken
    ) => ScalarIntAsync(
        connectionString,
        "SELECT count(*) FROM pg_catalog.pg_indexes "
        + "WHERE schemaname = 'hierarchy' AND indexname = 'simple_filter_lookup';",
        cancellationToken: cancellationToken);

    /// <summary>Provides the AppendSideEffectWrite contract shared by PostgreSQL feature tests.</summary>
    protected static void AppendSideEffectWrite(
        MigrationBuilder builder,
        string writeKind
    )
    {
        switch (writeKind)
        {
            case "INSERT":
                builder.EnsureModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    ["id", "managed_value"],
                    ["integer", "character varying(64)"],
                    new object?[,] { { 1, "target" } });

                break;
            case "UPDATE":
                builder.UpdateModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    new object?[,] { { 1 } },
                    ["managed_value"],
                    ["character varying(64)"],
                    new object?[,] { { "source" } },
                    new object?[,] { { "target" } });

                break;
            case "DELETE":
                builder.DeleteModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    new object?[,] { { 1 } },
                    ["id", "managed_value"],
                    ["integer", "character varying(64)"],
                    new object?[,] { { 1, "source" } });

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writeKind));
        }
    }

    /// <summary>Provides the PostgreSqlIdentifier contract shared by PostgreSQL feature tests.</summary>
    protected static string PostgreSqlIdentifier(
        string value
    ) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Provides the SqlColumn contract shared by PostgreSQL feature tests.</summary>
    protected static SafeMigrationSqlExpression SqlColumn(
        string name
    ) => SafeMigrationSql.Identifier(name);

    /// <summary>Provides the SqlBinary contract shared by PostgreSQL feature tests.</summary>
    protected static SafeMigrationSqlExpression SqlBinary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    ) => SafeMigrationSql.Binary(left, @operator, right);

    /// <summary>Provides the SqlColumnAndColumn contract shared by PostgreSQL feature tests.</summary>
    protected static SafeMigrationSqlExpression SqlColumnAndColumn(
        string left,
        SafeMigrationSqlBinaryOperator @operator,
        string right
    ) => SqlBinary(SqlColumn(left), @operator, SqlColumn(right));

    /// <summary>Provides the SqlColumnAndInt contract shared by PostgreSQL feature tests.</summary>
    protected static SafeMigrationSqlExpression SqlColumnAndInt(
        string column,
        SafeMigrationSqlBinaryOperator @operator,
        int value
    ) => SqlBinary(SqlColumn(column), @operator, SafeMigrationSql.Literal(value));

    /// <summary>Provides the SqlFunction contract shared by PostgreSQL feature tests.</summary>
    protected static SafeMigrationSqlExpression SqlFunction(
        string name,
        string column
    ) => SafeMigrationSql.Function(name, SqlColumn(column));

    /// <summary>Retains the CountingDbConnection CLR identity while feature classes share its model.</summary>
    protected sealed class CountingDbConnection : DbConnection
    {
        private readonly NpgsqlConnection _connection;

        private int _commandCount;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public CountingDbConnection(
            NpgsqlConnection connection
        )
        {
            _connection = connection;
        }

        /// <summary>Gets the number of native commands created by this connection wrapper.</summary>
        public int CommandCount => Volatile.Read(ref _commandCount);

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString
        {
            get => _connection.ConnectionString;
            set => _connection.ConnectionString = value ?? string.Empty;
        }

        /// <inheritdoc />
        public override string Database => _connection.Database;

        /// <inheritdoc />
        public override string DataSource => _connection.DataSource;

        /// <inheritdoc />
        public override string ServerVersion => _connection.ServerVersion;

        /// <inheritdoc />
        public override ConnectionState State => _connection.State;

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => _connection.ChangeDatabase(databaseName);

        /// <inheritdoc />
        public override void Close() => _connection.Close();

        /// <inheritdoc />
        public override void Open() => _connection.Open();

        /// <inheritdoc />
        public override Task OpenAsync(
            CancellationToken cancellationToken
        ) => _connection.OpenAsync(cancellationToken);

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => _connection.BeginTransaction(isolationLevel);

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand()
        {
            _ = Interlocked.Increment(ref _commandCount);

            return _connection.CreateCommand();
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing)
            {
                _connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Retains the MatchingDerivedContext CLR identity while feature classes share its model.</summary>
    protected sealed class MatchingDerivedContext : SafeMigrationDbContext
    {
        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public MatchingDerivedContext(
            string connectionString
        ) : base(connectionString) { }
    }

    /// <summary>Retains the BlockingProviderAnalyzer CLR identity while feature classes share its model.</summary>
    protected sealed class BlockingProviderAnalyzer : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public BlockingProviderAnalyzer(
            string providerId
        )
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ProviderId = providerId;
        }

        /// <summary>Completes only after the provider analyzer has admitted the actual operation.</summary>
        public Task Started => _started.Task;

        /// <summary>Identifies the provider contract exercised by this analyzer fixture.</summary>
        public string ProviderId { get; }

        /// <summary>Validate Context.</summary>
        public void ValidateContext(
            DbContext context
        ) => ArgumentNullException.ThrowIfNull(context);

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Analysis scope acquisition must not be reached.");

        /// <inheritdoc />
        public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken
        )
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException("The blocking analyzer completed without cancellation.");
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Operation analysis must not be reached.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Unexpected-object analysis must not be reached.");
    }

    /// <summary>Retains the SnapshotFreeContext CLR identity while feature classes share its model.</summary>
    protected sealed class SnapshotFreeContext : DbContext
    {
        /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
        public SnapshotFreeContext(
            DbContextOptions<SnapshotFreeContext> options
        ) : base(options) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<SnapshotFreeEntry>(entity =>
            {
                entity.HasNoKey();
                entity.ToTable("snapshotless_items", table => table.HasComment("Snapshot-free target."));
                entity
                    .Property(entry => entry.Id)
                    .HasColumnName("id")
                    .HasColumnType("integer");
            });
        }
    }

    /// <summary>Retains the SnapshotFreeEntry CLR identity while feature classes share its model.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    protected sealed class SnapshotFreeEntry
    {
        /// <summary>Gets or sets the mapped id fixture value.</summary>
        public int Id { get; set; }
    }

    /// <summary>Retains the StandaloneForeignKeyContext CLR identity while feature classes share its model.</summary>
    protected sealed class StandaloneForeignKeyContext : DbContext
    {
        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public StandaloneForeignKeyContext(
            DbContextOptions<StandaloneForeignKeyContext> options
        ) : base(options) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<TransitionParent>(entity =>
            {
                entity.ToTable("transition_parents");
                entity
                    .HasKey(value => value.Id)
                    .HasName("pk_transition_parents");

                entity
                    .Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("integer")
                    .ValueGeneratedNever();
            });

            modelBuilder.Entity<TransitionChild>(entity =>
            {
                entity.ToTable("transition_children");
                entity
                    .HasKey(value => value.Id)
                    .HasName("pk_transition_children");

                entity
                    .Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("integer")
                    .ValueGeneratedNever();

                entity
                    .Property(value => value.ParentId)
                    .HasColumnName("parent_id")
                    .HasColumnType("integer");

                entity
                    .HasOne<TransitionParent>()
                    .WithMany()
                    .HasForeignKey(value => value.ParentId)
                    .OnDelete(DeleteBehavior.SetNull)
                    .HasConstraintName("fk_transition_children_parent");
            });
        }
    }

    /// <summary>Retains the TransitionParent CLR identity while feature classes share its model.</summary>
    protected sealed class TransitionParent
    {
        /// <summary>Gets or sets the mapped id fixture value.</summary>
        public int Id { get; init; }
    }

    /// <summary>Retains the TransitionChild CLR identity while feature classes share its model.</summary>
    protected sealed class TransitionChild
    {
        /// <summary>Gets or sets the mapped id fixture value.</summary>
        public int Id { get; init; }

        /// <summary>Gets or sets the mapped parent id fixture value.</summary>
        public int? ParentId { get; init; }
    }

    /// <summary>Retains the UnmappedValue CLR identity while feature classes share its model.</summary>
    protected sealed class UnmappedValue;

    /// <summary>Retains the SchemaChangingDerivedContext CLR identity while feature classes share its model.</summary>
    protected sealed class SchemaChangingDerivedContext
        : SafeMigrationDbContext
    {
        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public SchemaChangingDerivedContext(
            string connectionString
        ) : base(connectionString) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    /// <summary>Retains the DerivedEntity CLR identity while feature classes share its model.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    protected sealed class DerivedEntity
    {
        /// <summary>Gets or sets the mapped id fixture value.</summary>
        public int Id { get; set; }
    }
}
