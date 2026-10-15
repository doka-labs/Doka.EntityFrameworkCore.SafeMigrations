namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Shares helpers and preserves nested model identities without declaring test cases.</summary>
/// <remarks>
/// WHY: Models stay on their original CLR owner so EF model-cache and fixture identities do not drift.
/// </remarks>
public abstract partial class PostgreSqlSafeMigrationIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned fixture without taking container ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    protected PostgreSqlSafeMigrationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Waits for the named narrowing command to reach its server-observed table lock.</summary>
    protected static async Task WaitForBlockedPostgreSqlNarrowingCommandAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity "
                + "WHERE pid <> pg_backend_pid() AND query LIKE @probe AND wait_event_type = 'Lock');";

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

        throw new TimeoutException("The PostgreSQL narrowing repair did not reach its blocked table lock.");
    }

    /// <summary>Retains the CountingDbConnection CLR identity while feature classes share its model.</summary>
    protected sealed class CountingDbConnection(
        NpgsqlConnection connection
    ) : DbConnection
    {
        private int _commandCount;

        public int CommandCount => Volatile.Read(ref _commandCount);

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => connection.ConnectionString;
            set => connection.ConnectionString = value ?? string.Empty;
        }

        public override string Database => connection.Database;

        public override string DataSource => connection.DataSource;

        public override string ServerVersion => connection.ServerVersion;

        public override ConnectionState State => connection.State;

        public override void ChangeDatabase(
            string databaseName
        ) => connection.ChangeDatabase(databaseName);

        public override void Close() => connection.Close();

        public override void Open() => connection.Open();

        public override Task OpenAsync(
            CancellationToken cancellationToken
        ) => connection.OpenAsync(cancellationToken);

        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => connection.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand()
        {
            _ = Interlocked.Increment(ref _commandCount);

            return connection.CreateCommand();
        }

        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing)
            {
                connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Provides the NullProofSentinelSql contract shared by PostgreSQL feature tests.</summary>
    protected const string NullProofSentinelSql = "CREATE FUNCTION null_proof_sentinel() RETURNS boolean "
        + "LANGUAGE plpgsql AS $null_proof$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P1901', "
        + "MESSAGE = 'null_proof_branch_executed'; END $null_proof$;";

    /// <summary>Builds real catalog predicates without retaining a live catalog snapshot.</summary>
    private protected static PostgreSqlSafeMigrationRuntimePlan BuildNullProofEligibilityPlan(
        DbContext context,
        SafeMigrationOperation operation
    ) => new PostgreSqlSafeMigrationCatalogSqlBuilder(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>())
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
    protected static Task CreateSimpleFilterTableAsync(
        string connectionString,
        string column = "parent"
    ) => ExecuteSqlAsync(
        connectionString,
        "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters "
        + $"(id integer NOT NULL, {PostgreSqlIdentifier(column)} integer NULL);"
        + "INSERT INTO hierarchy.simple_filters VALUES (1, NULL), (2, 1), (3, 2);");

    /// <summary>Counts the independently managed filtered index in the exact test-owned schema.</summary>
    protected static Task<int> SimpleFilterIndexCountAsync(
        string connectionString
    ) => ScalarIntAsync(
        connectionString,
        "SELECT count(*) FROM pg_catalog.pg_indexes "
        + "WHERE schemaname = 'hierarchy' AND indexname = 'simple_filter_lookup';");

    /// <summary>Retains the MatchingDerivedContext CLR identity while feature classes share its model.</summary>
    protected sealed class MatchingDerivedContext(string connectionString) : SafeMigrationDbContext(connectionString);

    /// <summary>Retains the BlockingProviderAnalyzer CLR identity while feature classes share its model.</summary>
    protected sealed class BlockingProviderAnalyzer(string providerId) : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public Task Started => _started.Task;

        public void ValidateContext(
            DbContext context
        )
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Analysis scope acquisition must not be reached.");

        public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        )
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException("The blocking analyzer completed without cancellation.");
        }

        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Operation analysis must not be reached.");

        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Unexpected-object analysis must not be reached.");
    }

    /// <summary>Retains the SnapshotFreeContext CLR identity while feature classes share its model.</summary>
    protected sealed class SnapshotFreeContext : DbContext
    {
        public SnapshotFreeContext(
            DbContextOptions<SnapshotFreeContext> options
        ) : base(options) { }

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
    protected sealed class SnapshotFreeEntry
    {
        public int Id { get; set; }
    }

    /// <summary>Provides the AppendSideEffectWrite contract shared by PostgreSQL feature tests.</summary>
    protected static void AppendSideEffectWrite(MigrationBuilder builder, string writeKind)
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

    /// <summary>Retains the StandaloneForeignKeyContext CLR identity while feature classes share its model.</summary>
    protected sealed class StandaloneForeignKeyContext(
        DbContextOptions<StandaloneForeignKeyContext> options
    ) : DbContext(options)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<TransitionParent>(entity =>
            {
                entity.ToTable("transition_parents");
                entity.HasKey(value => value.Id)
                    .HasName("pk_transition_parents");
                entity.Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("integer")
                    .ValueGeneratedNever();
            });

            modelBuilder.Entity<TransitionChild>(entity =>
            {
                entity.ToTable("transition_children");
                entity.HasKey(value => value.Id)
                    .HasName("pk_transition_children");
                entity.Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("integer")
                    .ValueGeneratedNever();
                entity.Property(value => value.ParentId)
                    .HasColumnName("parent_id")
                    .HasColumnType("integer");
                entity.HasOne<TransitionParent>()
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
        public int Id { get; init; }
    }

    /// <summary>Retains the TransitionChild CLR identity while feature classes share its model.</summary>
    protected sealed class TransitionChild
    {
        public int Id { get; init; }

        public int? ParentId { get; init; }
    }

    /// <summary>Bounds setup commands for the large performance fixtures.</summary>
    protected const int PerformanceFixtureCommandTimeoutSeconds = 180;

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

    /// <summary>Retains the UnmappedValue CLR identity while feature classes share its model.</summary>
    protected sealed class UnmappedValue;

    /// <summary>Retains the SchemaChangingDerivedContext CLR identity while feature classes share its model.</summary>
    protected sealed class SchemaChangingDerivedContext(string connectionString)
        : SafeMigrationDbContext(connectionString)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    /// <summary>Retains the DerivedEntity CLR identity while feature classes share its model.</summary>
    protected sealed class DerivedEntity
    {
        public int Id { get; set; }
    }

}
