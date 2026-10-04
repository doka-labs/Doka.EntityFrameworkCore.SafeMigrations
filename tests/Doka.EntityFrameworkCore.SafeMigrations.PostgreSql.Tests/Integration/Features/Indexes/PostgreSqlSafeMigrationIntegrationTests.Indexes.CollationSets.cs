namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Explicit key collation remains exact between two collapsed plain-key positions.</summary>
    /// <param name="alias">Whether the requested name differs from the matching physical index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedKeyExplicitCollationMatchIsNoOp(
        bool alias
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateMixedCollationIndexAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = MixedCollationBuilder(context, alias, new SafeMigrationCollationIdentifier("C", "pg_catalog"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("mixed-key-collation-match"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>A different or unspecified key collation must not become a physical or semantic match.</summary>
    /// <param name="alias">Whether the operation may create a distinct index under another name.</param>
    /// <param name="collation">The conflicting expected collation, or null for the column default.</param>
    /// <param name="explicitOperatorClass">Whether the default-collation key uses the per-key catalog path.</param>
    [Theory]
    [InlineData(false, "POSIX", false)]
    [InlineData(true, "POSIX", false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    public async Task MixedKeyExplicitCollationMismatchDoesNotMatch(
        bool alias,
        string? collation,
        bool explicitOperatorClass
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateMixedCollationIndexAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var expected = collation is null ? null : new SafeMigrationCollationIdentifier(collation, "pg_catalog");
        var builder = MixedCollationBuilder(context, alias, expected, explicitOperatorClass);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("mixed-key-collation-mismatch"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(alias ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            alias ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(alias ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>The physical column-default collation matches in collapsed and per-key catalog paths.</summary>
    /// <param name="alias">Whether the requested name differs from the physical index.</param>
    /// <param name="explicitOperatorClass">Whether the middle key uses the per-key catalog path.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MixedKeyDefaultCollationMatchIsNoOp(
        bool alias,
        bool explicitOperatorClass
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateMixedCollationIndexAsync(connectionString, collationName: "POSIX");
        await using var context = CreateContext(connectionString);
        var builder = MixedCollationBuilder(context, alias, collation: null, explicitOperatorClass);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("mixed-key-default-collation-match"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Collations with the same name in different schemas retain distinct physical identities.</summary>
    /// <param name="alias">Whether the requested name differs from the existing index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedKeyExplicitCollationNamespaceMismatchDoesNotMatch(
        bool alias
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA custom; CREATE COLLATION custom.\"C\" FROM pg_catalog.\"C\";");

        await CreateMixedCollationIndexAsync(connectionString, "custom");
        await using var context = CreateContext(connectionString);
        var builder = MixedCollationBuilder(context, alias, new SafeMigrationCollationIdentifier("C", "pg_catalog"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("mixed-key-collation-namespace"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(
            alias ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(alias ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>The generated guard rejects key-collation drift without replacing the existing index.</summary>
    /// <param name="collation">The conflicting explicit collation, or null for the column default.</param>
    /// <param name="explicitOperatorClass">Whether the default-collation key uses the per-key catalog path.</param>
    [Theory]
    [InlineData("POSIX", false)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task MixedKeyExplicitCollationDriftExecutionPreservesIndex(
        string? collation,
        bool explicitOperatorClass
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateMixedCollationIndexAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var expected = collation is null ? null : new SafeMigrationCollationIdentifier(collation, "pg_catalog");
        var builder = MixedCollationBuilder(context, alias: false, expected, explicitOperatorClass);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal("P1001", postgres.SqlState);
        Assert.Equal("doka_sm_different", postgres.MessageText);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
        Assert.Equal(
            "C",
            await ScalarStringAsync(
                connectionString,
                "SELECT coll.collname FROM pg_catalog.pg_index i "
                + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
                + "JOIN pg_catalog.pg_collation coll ON coll.oid = i.indcollation[1] "
                + "WHERE idx.relname = 'ix_collation_set';"));
    }

    /// <summary>Expression-key identity includes explicit or derived collation even on an empty table.</summary>
    /// <param name="expectedCollation">The explicit expression-key collation, or null for its derived default.</param>
    /// <param name="alias">Whether the requested name differs from the physical index.</param>
    /// <param name="explicitOperatorClass">Whether an explicit operator class is expected.</param>
    [Theory]
    [InlineData("C", false, false)]
    [InlineData("C", true, false)]
    [InlineData("C", false, true)]
    [InlineData("C", true, true)]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData("POSIX", false, false)]
    [InlineData("POSIX", true, false)]
    public async Task ExpressionKeyCollationRemainsPhysical(
        string? expectedCollation,
        bool alias,
        bool explicitOperatorClass
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\"); "
            + "CREATE INDEX ix_collation_set ON collation_sets (lower(value) COLLATE pg_catalog.\"C\");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                alias ? "ix_collation_alias" : "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    collation: expectedCollation is null
                        ? null
                        : new SafeMigrationCollationIdentifier(expectedCollation, "pg_catalog"),
                    operatorClass: explicitOperatorClass ? "text_ops" : null)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-collation"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        var matching = expectedCollation == "C";
        Assert.Equal(
            matching ? SafeMigrationObservedState.Matching
                : alias ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(
            matching ? SafeMigrationAction.NoOp
                : alias ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Derived expression collation matches without needing even one physical data row.</summary>
    /// <param name="populated">Whether the test table contains a row.</param>
    /// <param name="explicitOperatorClass">Whether the default operator class is explicitly requested.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExpressionKeyDefaultCollationMatchesWithoutRowProof(
        bool populated,
        bool explicitOperatorClass
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\"); "
            + (populated ? "INSERT INTO collation_sets VALUES ('entry'); " : string.Empty)
            + "CREATE INDEX ix_collation_set ON collation_sets (lower(value));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    operatorClass: explicitOperatorClass ? "text_ops" : null)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-default-collation"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Integer expression keys keep their identity without a collation-function type error.</summary>
    [Fact]
    public async Task ExpressionKeyNoncollatableDefaultIsNoOp()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text); "
            + "CREATE INDEX ix_collation_set ON collation_sets (length(value));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("length", "value"))]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-noncollatable"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
    }

    /// <summary>Structured key collations preserve nested nodes and PostgreSQL's stored top-level operand.</summary>
    /// <param name="nested">Whether the collate node is nested inside the function.</param>
    /// <param name="columnOnly">Whether the top-level collate operand is a column rather than a function.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExpressionKeyStructuredCollationMatchesStoredShape(
        bool nested,
        bool columnOnly
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storedExpression = nested
            ? "lower(value COLLATE pg_catalog.\"C\")"
            : columnOnly ? "value COLLATE pg_catalog.\"C\"" : "lower(value) COLLATE pg_catalog.\"C\"";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\"); "
            + "CREATE INDEX ix_collation_set ON collation_sets (" + storedExpression + ");");

        await using var context = CreateContext(connectionString);
        var expression = nested
            ? SafeMigrationSql.Function("lower", SafeMigrationSql.Collate(SqlColumn("value"), "C", "pg_catalog"))
            : SafeMigrationSql.Collate(
                columnOnly ? SqlColumn("value") : SqlFunction("lower", "value"),
                "C",
                "pg_catalog");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(structuredExpression: expression)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-structured-collation"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Expression-key runtime checks distinguish explicit and derived collation before any mutation.</summary>
    /// <param name="expectedCollation">The requested collation, or null for the expression's default.</param>
    [Theory]
    [InlineData("C")]
    [InlineData("POSIX")]
    [InlineData(null)]
    public async Task ExpressionKeyRuntimeCollationPreservesExistingIndex(
        string? expectedCollation
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\"); "
            + "CREATE INDEX ix_collation_set ON collation_sets (lower(value) COLLATE pg_catalog.\"C\");");

        await using var context = CreateContext(connectionString);
        var builder = ExpressionCollationBuilder(context, expectedCollation);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        if (expectedCollation == "C")
        {
            Assert.Null(exception);
        }
        else
        {
            var postgres = Assert.IsType<PostgresException>(exception);
            Assert.Equal("P1001", postgres.SqlState);
            Assert.Equal("doka_sm_different", postgres.MessageText);
        }

        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
        Assert.Equal(
            "C",
            await ScalarStringAsync(
                connectionString,
                "SELECT coll.collname FROM pg_catalog.pg_index i "
                + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
                + "JOIN pg_catalog.pg_collation coll ON coll.oid = i.indcollation[0] "
                + "WHERE idx.relname = 'ix_collation_set';"));
    }

    /// <summary>New expression indexes verify their explicit or derived collation after guarded creation.</summary>
    /// <param name="expectedCollation">The requested explicit collation, or null for its derived default.</param>
    [Theory]
    [InlineData("C")]
    [InlineData(null)]
    public async Task ExpressionKeyCollationVerifiesAfterCreation(
        string? expectedCollation
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\");");

        await using var context = CreateContext(connectionString);
        var builder = ExpressionCollationBuilder(context, expectedCollation);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-collation-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.True(Assert.Single(report.Assessments).PostconditionSatisfied);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Top-level and nested collations cannot match a same-named collation from another namespace.</summary>
    /// <param name="nested">Whether the collate node remains within a function rather than its key decoration.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpressionKeyStructuredCollationRejectsNamespaceDrift(
        bool nested
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storedExpression = nested
            ? "lower(value COLLATE pg_catalog.\"C\")"
            : "lower(value) COLLATE pg_catalog.\"C\"";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA custom; CREATE COLLATION custom.\"C\" FROM pg_catalog.\"C\"; "
            + "CREATE TABLE collation_sets (value text COLLATE pg_catalog.\"POSIX\"); "
            + "CREATE INDEX ix_collation_set ON collation_sets (" + storedExpression + ");");

        await using var context = CreateContext(connectionString);
        var expression = nested
            ? SafeMigrationSql.Function("lower", SafeMigrationSql.Collate(SqlColumn("value"), "C", "custom"))
            : SafeMigrationSql.Collate(SqlFunction("lower", "value"), "C", "custom");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(structuredExpression: expression)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("expression-key-collation-namespace-drift"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(1, await MixedCollationIndexCountAsync(connectionString));
    }

    /// <summary>Builds the expression-key contract without changing its authored collation or operator class.</summary>
    /// <param name="context">The registered PostgreSQL context.</param>
    /// <param name="collation">The explicit key collation, or null for the expression's derived default.</param>
    /// <returns>A migration builder containing the immutable expected expression-index contract.</returns>
    private static MigrationBuilder ExpressionCollationBuilder(
        DbContext context,
        string? collation
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_collation_set",
                "collation_sets",
                [new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    collation: collation is null
                        ? null
                        : new SafeMigrationCollationIdentifier(collation, "pg_catalog"))]),
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }

    /// <summary>Creates interleaved plain/explicit/plain keys with observable ordinal and ordering facets.</summary>
    /// <param name="connectionString">The test-owned database connection.</param>
    /// <param name="collationSchema">The explicit physical collation namespace.</param>
    /// <param name="collationName">The explicit physical collation name.</param>
    /// <returns>The asynchronous fixture setup.</returns>
    private static Task CreateMixedCollationIndexAsync(
        string connectionString,
        string collationSchema = "pg_catalog",
        string collationName = "C"
    ) => ExecuteSqlAsync(
        connectionString,
        "CREATE TABLE collation_sets (first_key integer NOT NULL, "
        + "value text COLLATE pg_catalog.\"POSIX\", last_key integer NOT NULL);"
        + "INSERT INTO collation_sets VALUES (1, 'entry', 2);"
        + "CREATE INDEX ix_collation_set ON collation_sets "
        + "(first_key DESC NULLS LAST, value COLLATE " + PostgreSqlIdentifier(collationSchema)
        + "." + PostgreSqlIdentifier(collationName) + " ASC NULLS LAST, last_key DESC NULLS FIRST);");

    /// <summary>Builds the mixed contract without moving explicit collations into the plain-key set.</summary>
    /// <param name="context">The registered PostgreSQL context.</param>
    /// <param name="alias">Whether the requested index name differs from the physical name.</param>
    /// <param name="collation">The explicit middle-key collation, or null for its column default.</param>
    /// <param name="explicitOperatorClass">Whether the middle key uses the explicit operator-class path.</param>
    /// <returns>A migration builder containing the immutable expected mixed-key contract.</returns>
    private static MigrationBuilder MixedCollationBuilder(
        DbContext context,
        bool alias,
        SafeMigrationCollationIdentifier? collation,
        bool explicitOperatorClass = false
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                alias ? "ix_collation_alias" : "ix_collation_set",
                "collation_sets",
                [
                    new ExpectedIndexKeyDefinition(
                        "first_key",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        nullOrder: SafeMigrationIndexNullOrder.Last),
                    new ExpectedIndexKeyDefinition(
                        "value",
                        sortOrder: SafeMigrationIndexSortOrder.Ascending,
                        nullOrder: SafeMigrationIndexNullOrder.Last,
                        collation: collation,
                        operatorClass: explicitOperatorClass ? "text_ops" : null),
                    new ExpectedIndexKeyDefinition(
                        "last_key",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        nullOrder: SafeMigrationIndexNullOrder.First),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }

    /// <summary>Counts only indexes on the test-owned table, including any unexpected duplicate alias.</summary>
    /// <param name="connectionString">The test-owned database connection.</param>
    /// <returns>The asynchronous physical-index count.</returns>
    private static Task<int> MixedCollationIndexCountAsync(
        string connectionString
    ) => ScalarIntAsync(
        connectionString,
        "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'public' AND tablename = 'collation_sets';");
}
