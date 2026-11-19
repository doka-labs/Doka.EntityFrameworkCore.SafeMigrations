namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ColumnsScaffolding PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlColumnsScaffoldingIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlColumnsScaffoldingIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Scaffolded Identity Table Preserves Generation And Is Idempotent.</summary>
    [Theory]
    [InlineData(NpgsqlValueGenerationStrategy.IdentityAlwaysColumn, "a")]
    [InlineData(NpgsqlValueGenerationStrategy.IdentityByDefaultColumn, "d")]
    public async Task ScaffoldedIdentityTable_PreservesGenerationAndIsIdempotent(
        NpgsqlValueGenerationStrategy strategy,
        string expectedIdentity
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(builder, "scaffolded_identity", strategy);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO scaffolded_identity (display_name) VALUES ('first');",
            cancellationToken: CancellationToken.None);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("scaffolded-identity"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(
            expectedIdentity,
            await ScalarStringAsync(
                connectionString,
                """
                SELECT a.attidentity FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname = 'scaffolded_identity'
                AND a.attname = 'id';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT id FROM scaffolded_identity;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Scaffolded Identity Table Rejects Existing Non Generated Column.</summary>
    [Fact]
    public async Task ScaffoldedIdentityTable_RejectsExistingNonGeneratedColumn()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE scaffolded_identity_drift (id integer NOT NULL, display_name
            character varying(80) NULL,
            CONSTRAINT pk_scaffolded_identity_drift PRIMARY KEY (id));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(builder, "scaffolded_identity_drift");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("scaffolded-identity-drift"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal(
            "none",
            await ScalarStringAsync(
                connectionString,
                """
                SELECT CASE WHEN a.attidentity = ''
                THEN 'none' ELSE a.attidentity::text END
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname = 'scaffolded_identity_drift'
                AND a.attname = 'id';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Scaffolded Operation Level Annotation Is Rejected Before Ddl.</summary>
    [Fact]
    public async Task ScaffoldedOperationLevelAnnotation_IsRejectedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var operation = AddScaffoldedIdentityTable(builder, "unsupported_scaffolded_annotation");
        operation["Test:UnsupportedOperationFacet"] = true;

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-operation-facet"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname = 'unsupported_scaffolded_annotation';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Unmodeled Serial Strategy Is Rejected Before Ddl.</summary>
    [Fact]
    public async Task UnmodeledSerialStrategy_IsRejectedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(
            builder,
            "unsupported_serial_strategy",
            NpgsqlValueGenerationStrategy.SerialColumn);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-serial"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname = 'unsupported_serial_strategy';
                """,
                cancellationToken: CancellationToken.None));
    }

    private static SafeMigrationOperation AddScaffoldedIdentityTable(
        MigrationBuilder builder,
        string tableName,
        NpgsqlValueGenerationStrategy strategy = NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
    )
    {
        _ = builder.CreateTableIfNotExists(
            name: tableName,
            columns: table => new
            {
                id = table
                    .Column<int>(type: "integer", nullable: false)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        strategy),
                display_name = table
                    .Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{tableName}", value => value.id));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }
}
