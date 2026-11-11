namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies native key, predicate and collation identities without broad SQL normalization.</summary>
public sealed class PostgreSqlIndexExpressionIdentityIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses an assembly-owned PostgreSQL server with per-test database ownership.</summary>
    /// <param name="fixture">The native PostgreSQL fixture.</param>
    public PostgreSqlIndexExpressionIdentityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Finite native spellings match while changed literals or Boolean grouping remain different.</summary>
    /// <param name="shape">The independently authored native expression family.</param>
    /// <param name="matching">Whether the expected expression retains its native meaning.</param>
    [Theory]
    [InlineData("coalesce", false)]
    [InlineData("coalesce", true)]
    [InlineData("boolean", false)]
    [InlineData("boolean", true)]
    [InlineData("string", false)]
    [InlineData("string", true)]
    public async Task FunctionalKeyNativeIdentityPreservesSemanticDrift(
        string shape,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var native = shape switch
        {
            "coalesce" => "COALESCE(a, 0)",
            "boolean" => "a IS NULL AND (b IS NULL AND c IS NULL)",
            "string" => "COALESCE(value, 'ACTIVE'::text)",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE index_identity(a integer, b integer, c integer, value text); "
            + $"CREATE INDEX ix_identity ON index_identity (({native})); "
            + "INSERT INTO index_identity VALUES (42, NULL, NULL, 'preserved');",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_identity",
                "index_identity",
                [new ExpectedIndexKeyDefinition(structuredExpression: Expression(shape, matching))]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM index_identity WHERE a=42 AND value='preserved';",
            CancellationToken.None);

        var nativeExpression = await ScalarStringAsync(connectionString,
            "SELECT pg_get_indexdef('ix_identity'::regclass,1,true);",
            CancellationToken.None);

        // Assert
        Assert.True(report.Status
            == (matching
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked),
            nativeExpression);

        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        AssertFailure(matching, failure);
        Assert.Equal(1, rows);
    }

    /// <summary>Source-aware text coercions are admitted without case-folding the predicate's string value.</summary>
    /// <param name="varchar">Whether the source needs PostgreSQL's implicit varchar-to-text coercion.</param>
    /// <param name="matching">Whether the expected literal exactly matches the existing predicate.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartialPredicateRetainsSourceTypeAndLiteralIdentity(
        bool varchar,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var storeType = varchar
            ? "varchar(40)"
            : "text";

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE index_identity(a integer, value {storeType}); "
            + "CREATE INDEX ix_identity ON index_identity(a) WHERE value='ACTIVE'; "
            + "INSERT INTO index_identity VALUES (42, 'preserved');",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_identity",
                "index_identity",
                [new ExpectedIndexKeyDefinition("a")],
                structuredFilter: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("value"),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal(matching
                        ? "ACTIVE"
                        : "active"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM index_identity WHERE a=42 AND value='preserved';",
            CancellationToken.None);

        // Assert
        Assert.Equal(matching
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            report.Status);

        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        AssertFailure(matching, failure);
        Assert.Equal(1, rows);
    }

    /// <summary>The unqualified name resolves one visible collation rather than any equal-spelling namespace.</summary>
    /// <param name="actualSchema">The schema owning the physical key collation.</param>
    /// <param name="expectedSchema">The explicitly requested schema, or null for search-path resolution.</param>
    /// <param name="customVisible">Whether the custom name is the first visible unqualified binding.</param>
    /// <param name="matching">Whether requested and stored collation OIDs must match.</param>
    [Theory]
    [InlineData("custom", null, false, false)]
    [InlineData("pg_catalog", null, false, true)]
    [InlineData("custom", "custom", false, true)]
    [InlineData("custom", "pg_catalog", false, false)]
    [InlineData("custom", null, true, true)]
    public async Task ExplicitKeyCollationRetainsResolvedNamespaceIdentity(
        string actualSchema,
        string? expectedSchema,
        bool customVisible,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA custom; "
            + "CREATE COLLATION custom.\"C\" (provider=icu, locale='en-US-u-ks-level1', deterministic=false); "
            + "CREATE TABLE public.index_identity(value text); "
            + $"CREATE INDEX ix_identity ON public.index_identity(value COLLATE {actualSchema}.\"C\"); "
            + "INSERT INTO public.index_identity VALUES ('preserved');",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        if (customVisible)
        {
            await context.Database.ExecuteSqlRawAsync("SET search_path=custom,pg_catalog,public;",
                CancellationToken.None);
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_identity",
                "index_identity",
                [
                    new ExpectedIndexKeyDefinition("value",
                        collation: new SafeMigrationCollationIdentifier("C", expectedSchema)),
                ],
                schema: "public"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var caseEqual = await ScalarIntAsync(connectionString,
            $"SELECT CASE WHEN 'a' COLLATE {actualSchema}.\"C\" = 'A' THEN 1 ELSE 0 END;",
            CancellationToken.None);

        // Assert
        Assert.Equal(matching
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            report.Status);

        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        AssertFailure(matching, failure);
        Assert.Equal(actualSchema == "custom"
                ? 1
                : 0,
            caseEqual);
    }

    /// <summary>New expression and partial indexes retain their native catalog contract on replay.</summary>
    /// <param name="partial">Whether the contract contains a source-aware partial-index predicate.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexCreationAndReplayRetainNativeExpressionIdentity(
        bool partial
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE index_identity(a integer, value varchar(40)); "
            + "INSERT INTO index_identity VALUES (42, 'preserved');",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        ExpectedIndexKeyDefinition[] keys = partial
            ? [new ExpectedIndexKeyDefinition("a")]
            : [new ExpectedIndexKeyDefinition(structuredExpression: Expression("coalesce", true))];

        var predicate = partial
            ? SafeMigrationSql.Binary(SafeMigrationSql.Identifier("value"),
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("ACTIVE"))
            : null;

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_identity",
                "index_identity",
                keys,
                structuredFilter: predicate),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM index_identity WHERE a=42 AND value='preserved';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(1, rows);
    }

    /// <summary>Builds typed contracts independently from the native fixture SQL.</summary>
    private static SafeMigrationSqlExpression Expression(
        string shape,
        bool matching
    ) => shape switch
    {
        "coalesce" => SafeMigrationSql.Function("COALESCE",
            SafeMigrationSql.Identifier("a"),
            SafeMigrationSql.Literal(matching
                ? 0
                : 1)),
        "boolean" => SafeMigrationSql.Binary(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("a")),
            matching
                ? SafeMigrationSqlBinaryOperator.And
                : SafeMigrationSqlBinaryOperator.Or,
            SafeMigrationSql.Binary(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("b")),
                SafeMigrationSqlBinaryOperator.And,
                SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("c")))),
        "string" => SafeMigrationSql.Function("COALESCE",
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal(matching
                    ? "ACTIVE"
                    : "active",
                "text")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>Retains explicit guard rejection rather than accepting an unrelated native failure.</summary>
    private static void AssertFailure(
        bool matching,
        Exception? failure
    )
    {
        if (matching)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.NotNull(failure);
            Assert.Contains("doka_sm_different", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Exercises the same public preflight runner used by applications.</summary>
    private static Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        MigrationBuilder builder,
        CancellationToken cancellationToken
    ) => context
        .GetService<ISafeMigrationRunner>()
        .AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("index-expression-identity"),
            cancellationToken);
}
