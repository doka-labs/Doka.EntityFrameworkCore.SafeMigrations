namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Retains observable physical host ordinals through provider-owned safe rebuilds.</summary>
public sealed class SqliteRetainedColumnOrderTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Shares only provider services, never database state.</summary>
    /// <param name="services">The existing class-owned SQLite services.</param>
    public SqliteRetainedColumnOrderTests(
        SqliteConstraintShapeServiceFixture services
    )
    {
        _internalServices = services.Provider;
    }

    /// <summary>Host key changes cannot reorder retained data columns to incidental model enumeration order.</summary>
    /// <param name="add">Whether the accepted operation adds rather than removes the independent host key.</param>
    /// <param name="safe">Whether SafeMigrations owns the reconstruction rather than an ordinary EF operation.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task SafeConstraintRebuildPreservesNativeColumnOrdinalsAndFacets(
        bool add,
        bool safe
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var constraint = add
            ? string.Empty
            : ", CONSTRAINT host_unique UNIQUE (GraphId, Source, Target)";

        await ExecuteSqlAsync(connection,
            "CREATE TABLE host (Id INTEGER NOT NULL CONSTRAINT host_pk PRIMARY KEY, GraphId INTEGER NOT NULL, "
            + "Source INTEGER NOT NULL, Target INTEGER NOT NULL, Payload TEXT NOT NULL DEFAULT 'retained'"
            + constraint
            + "); INSERT INTO host VALUES (1, 2, 3, 4, 'payload');",
            CancellationToken.None);

        await using HostContext context = add
            ? new WithUniqueContext(connection, _internalServices)
            : new WithoutUniqueContext(connection, _internalServices);

        var before = await ReadColumnsAsync(connection, CancellationToken.None);
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        if (!safe)
        {
            builder.Operations.Add(add
                ? new AddUniqueConstraintOperation
                {
                    Name = "host_unique",
                    Table = "host",
                    Columns = ["GraphId", "Source", "Target"],
                }
                : new DropUniqueConstraintOperation
                {
                    Name = "host_unique",
                    Table = "host",
                });
        }
        else if (add)
        {
            builder.AddUniqueConstraintIfNotExists("host_unique", "host", ["GraphId", "Source", "Target"]);
        }
        else
        {
            builder.DropUniqueConstraintIfExists("host_unique", "host");
        }

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var after = await ReadColumnsAsync(connection, CancellationToken.None);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM host WHERE Id = 1 AND GraphId = 2 AND Source = 3 "
            + "AND Target = 4 AND Payload = 'payload';",
            CancellationToken.None);

        // Assert
        Assert.Equal(safe
                ? before
                :
                [
                    before[0], before[1], "2:Payload:TEXT:1:'retained':0",
                    "3:Source:INTEGER:1:<none>:0", "4:Target:INTEGER:1:<none>:0",
                ],
            after);

        Assert.Equal(1, rows);
    }

    /// <summary>An explicit destination order remains authoritative over inherited native ordinals.</summary>
    /// <param name="mainSchema">
    /// Whether the authored model and operation explicitly target SQLite's main schema.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitDestinationOrderIsNotOverwrittenByNativePreservation(
        bool mainSchema
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection,
            "CREATE TABLE host (Id INTEGER NOT NULL CONSTRAINT host_pk PRIMARY KEY, GraphId INTEGER NOT NULL, "
            + "Source INTEGER NOT NULL, Target INTEGER NOT NULL, Payload TEXT NOT NULL DEFAULT 'retained', "
            + "CONSTRAINT host_unique UNIQUE (GraphId, Source, Target)); "
            + "INSERT INTO host VALUES (1, 2, 3, 4, 'payload');",
            CancellationToken.None);

        await using HostContext context = mainSchema
            ? new OrderedMainContext(connection, _internalServices)
            : new OrderedContext(connection, _internalServices);

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropUniqueConstraintIfExists("host_unique",
            "host",
            schema: mainSchema
                ? "main"
                : null);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var after = await ReadColumnsAsync(connection, CancellationToken.None);

        // Assert
        Assert.Equal([
                "0:Id:INTEGER:1:<none>:1", "1:Payload:TEXT:1:'retained':0",
                "2:Target:INTEGER:1:<none>:0", "3:Source:INTEGER:1:<none>:0", "4:GraphId:INTEGER:1:<none>:0",
            ],
            after);
    }

    /// <summary>A later native failure rolls back the reconstructed host, its exact ordinals, and its key.</summary>
    [Fact]
    public async Task FailedOwnedWindowRestoresNativeHostOrderAndConstraint()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection,
            "CREATE TABLE host (Id INTEGER NOT NULL CONSTRAINT host_pk PRIMARY KEY, GraphId INTEGER NOT NULL, "
            + "Source INTEGER NOT NULL, Target INTEGER NOT NULL, Payload TEXT NOT NULL DEFAULT 'retained', "
            + "CONSTRAINT host_unique UNIQUE (GraphId, Source, Target)); "
            + "INSERT INTO host VALUES (1, 2, 3, 4, 'payload'); "
            + "CREATE TABLE other (id INTEGER); CREATE INDEX duplicate_index ON other(id);",
            CancellationToken.None);

        await using var context = new WithoutUniqueContext(connection, _internalServices);
        var before = await ReadColumnsAsync(connection, CancellationToken.None);
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropUniqueConstraintIfExists("host_unique", "host");
        builder.CreateIndex("duplicate_index", "other", "id");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            CancellationToken.None));

        var after = await ReadColumnsAsync(connection, CancellationToken.None);
        var key = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('host') WHERE origin = 'u';",
            CancellationToken.None);

        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM host WHERE Id = 1 AND GraphId = 2 AND Source = 3 "
            + "AND Target = 4 AND Payload = 'payload';",
            CancellationToken.None);

        // Assert
        Assert.IsType<SqliteException>(exception);
        Assert.Equal(before, after);
        Assert.Equal(1, key);
        Assert.Equal(1, rows);
    }

    /// <summary>Reads independent native order, type, nullability, default and key evidence.</summary>
    private static async Task<string[]> ReadColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cid || ':' || name || ':' || type || ':' || \"notnull\" || ':' "
            + "|| COALESCE(dflt_value, '<none>') || ':' || pk FROM pragma_table_xinfo('host') ORDER BY cid;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns.ToArray();
    }

    /// <summary>Supplies an intentionally differently enumerated target model.</summary>
    private abstract class HostContext : DbContext
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _services;

        protected HostContext(
            SqliteConnection connection,
            IServiceProvider services
        )
        {
            _connection = connection;
            _services = services;
        }

        protected abstract bool Unique { get; }

        protected virtual bool ExplicitOrder => false;

        protected virtual string? Schema => null;

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder
            .UseSqlite(_connection)
            .UseSqliteSafeMigrations()
            .UseInternalServiceProvider(_services);

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity("Host",
                entity =>
                {
                    entity.ToTable("host", Schema);
                    entity
                        .Property<int>("Id")
                        .ValueGeneratedNever();

                    entity.Property<int>("GraphId");
                    entity.Property<int>("Source");
                    entity.Property<int>("Target");
                    entity
                        .Property<string>("Payload")
                        .IsRequired()
                        .HasDefaultValue("retained");

                    entity
                        .HasKey("Id")
                        .HasName("host_pk");

                    if (Unique)
                    {
                        entity
                            .HasAlternateKey("GraphId", "Source", "Target")
                            .HasName("host_unique");
                    }

                    if (ExplicitOrder)
                    {
                        entity
                            .Property<int>("Id")
                            .HasColumnOrder(0);

                        entity
                            .Property<string>("Payload")
                            .HasColumnOrder(1);

                        entity
                            .Property<int>("Target")
                            .HasColumnOrder(2);

                        entity
                            .Property<int>("Source")
                            .HasColumnOrder(3);

                        entity
                            .Property<int>("GraphId")
                            .HasColumnOrder(4);
                    }
                });
        }
    }

    /// <summary>Provides the post-add model without changing EF model-cache identity dynamically.</summary>
    private sealed class WithUniqueContext : HostContext
    {
        public WithUniqueContext(
            SqliteConnection connection,
            IServiceProvider services
        ) : base(connection, services) { }

        protected override bool Unique => true;
    }

    /// <summary>Provides the post-drop model without changing EF model-cache identity dynamically.</summary>
    private sealed class WithoutUniqueContext : HostContext
    {
        public WithoutUniqueContext(
            SqliteConnection connection,
            IServiceProvider services
        ) : base(connection, services) { }

        protected override bool Unique => false;
    }

    /// <summary>Provides an explicit destination order with its own immutable model-cache identity.</summary>
    private sealed class OrderedContext : HostContext
    {
        public OrderedContext(
            SqliteConnection connection,
            IServiceProvider services
        ) : base(connection, services) { }

        protected override bool Unique => false;

        protected override bool ExplicitOrder => true;
    }

    /// <summary>Uses SQLite's explicit main-schema identity rather than its unqualified model alias.</summary>
    private sealed class OrderedMainContext : HostContext
    {
        public OrderedMainContext(
            SqliteConnection connection,
            IServiceProvider services
        ) : base(connection, services) { }

        protected override bool Unique => false;

        protected override bool ExplicitOrder => true;

        protected override string Schema => "main";
    }
}
