namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the Identifiers PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlIdentifiersIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlIdentifiersIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Server Quoted Expression Identifiers Converge Across Every Structured Facet.</summary>
    [Fact]
    public async Task ServerQuotedExpressionIdentifiers_ConvergeAcrossEveryStructuredFacet()
    {
        // Arrange
        const string table = "expression_identifiers";
        var sum = SqlBinary(
            SqlBinary(SqlColumn("user"), SafeMigrationSqlBinaryOperator.Add, SqlColumn("a$b")),
            SafeMigrationSqlBinaryOperator.Add,
            SqlColumn("ordinary"));

        var definition = new ExpectedTableDefinition(
            table,
            [
                new ExpectedColumnDefinition("user", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("a$b", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("ordinary", typeof(int), false, "integer"),
                new ExpectedColumnDefinition(
                    "default$value",
                    typeof(string),
                    false,
                    "text",
                    defaultValue: SafeMigrationDefaultValue.Sql(
                        SafeMigrationSql.Collate(SafeMigrationSql.Literal("seed", "text"), "C"))),
                new ExpectedColumnDefinition(
                    "select",
                    typeof(int),
                    true,
                    "integer",
                    isStored: true,
                    computedExpression: sum),
            ],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_expression_identifiers",
                    table,
                    SqlBinary(sum, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0))),
            ]);

        var index = new ExpectedIndexDefinition(
            "ix_expression_identifiers",
            table,
            [new ExpectedIndexKeyDefinition(structuredExpression: sum)],
            structuredFilter: SqlBinary(
                SqlColumn("a$b"),
                SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)));

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);

        // Act
        // WHY: Retain the exact failing operation ordinal in the native identifier diagnostic.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < builder.Operations.Count; ordinal++)
        {
            try
            {
                await ExecuteOperationsAsync(context, [builder.Operations[ordinal]], CancellationToken.None);
            }
            catch (Exception exception)
            {
                var operation = Assert.IsType<SafeMigrationOperation>(builder.Operations[ordinal]);

                throw new InvalidOperationException(
                    $"Server-quoted identifier convergence failed at {ordinal}:{operation.Intent.Kind}.",
                    exception);
            }
        }

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE INDEX ix_expression_identifiers ON expression_identifiers "
            + "(((\"user\" + \"a$b\") + ordinary)) WHERE \"a$b\" > 0;",
            cancellationToken: CancellationToken.None);

        var indexBuilder = new MigrationBuilder(context.Database.ProviderName!);
        indexBuilder.EnsureIndex(index, SafeMigrationPolicy.ThrowIfDifferent);
        var indexReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                indexBuilder.Operations,
                new SafeMigrationRunOptions("server-quoted-index"),
                CancellationToken.None);

        var indexCatalog = await ScalarStringAsync(
            connectionString,
            """
            SELECT pg_catalog.pg_get_indexdef(i.indexrelid, 1, TRUE) || ' | ' || pg_catalog.pg_get_expr(i.indpred,
            i.indrelid) FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_expression_identifiers';
            """,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, indexBuilder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, indexBuilder.Operations, CancellationToken.None);
        var operations = builder
            .Operations
            .Concat(indexBuilder.Operations)
            .ToArray();

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("server-quoted-identifiers"),
                CancellationToken.None);

        // Assert
        Assert.True(
            indexReport.Status == SafeMigrationReportStatus.Ready,
            $"Index catalog form did not converge: {indexCatalog}");

        _ = Assert.Single(indexReport.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.NotEmpty(operations);
        Assert.Equal(operations.Length, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
    }

    /// <summary>Maximum Quoted Mixed Case Unicode Identifiers Converge Across Owned Objects.</summary>
    [Fact]
    public async Task MaximumQuotedMixedCaseUnicodeIdentifiers_ConvergeAcrossOwnedObjects()
    {
        // Arrange
        var table = PostgreSqlMaximumIdentifier("Ta\"b\\le'_\u00fc_");
        var id = PostgreSqlMaximumIdentifier("I\"D\\'_\u00fc_");
        var code = PostgreSqlMaximumIdentifier("Co\"de\\'_\u00fc_");
        var parent = PostgreSqlMaximumIdentifier("Pa\"rent\\'_\u00fc_");
        var primaryKey = PostgreSqlMaximumIdentifier("PK\"\\'_\u00fc_");
        var unique = PostgreSqlMaximumIdentifier("UQ\"\\'_\u00fc_");
        var check = PostgreSqlMaximumIdentifier("CK\"\\'_\u00fc_");
        var foreignKey = PostgreSqlMaximumIdentifier("FK\"\\'_\u00fc_");
        var index = PostgreSqlMaximumIdentifier("IX\"\\'_\u00fc_");
        var definition = new ExpectedTableDefinition(
            table,
            [
                new ExpectedColumnDefinition(id, typeof(int), false, "integer"),
                new ExpectedColumnDefinition(code, typeof(string), true, "character varying(40)", maxLength: 40),
                new ExpectedColumnDefinition(parent, typeof(int), true, "integer"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition(primaryKey, table, [id]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition(unique, table, [code])],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    check,
                    table,
                    SqlBinary(
                        SafeMigrationSql.IsNull(SqlColumn(code)),
                        SafeMigrationSqlBinaryOperator.Or,
                        SqlBinary(
                            SafeMigrationSql.Cast(SqlColumn(code), "text"),
                            SafeMigrationSqlBinaryOperator.NotEqual,
                            SafeMigrationSql.Literal(string.Empty, "text")))),
            ],
            foreignKeys: [new ExpectedForeignKeyDefinition(foreignKey, table, [parent], table, [id])]);

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            definition,
            [new ExpectedIndexDefinition(index, table, [new ExpectedIndexKeyDefinition(column: parent)])]);

        // Act
        // WHY: Retain the exact failing operation ordinal in the native identifier diagnostic.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < builder.Operations.Count; ordinal++)
        {
            try
            {
                await ExecuteOperationsAsync(context, [builder.Operations[ordinal]], CancellationToken.None);
            }
            catch (Exception exception)
            {
                var operation = Assert.IsType<SafeMigrationOperation>(builder.Operations[ordinal]);
                var detail = operation.Intent.Kind == SafeMigrationOperationKind.EnsureCheckConstraint
                    ? await ReadCheckExpressionAsync(connectionString, check, cancellationToken: CancellationToken.None)
                    : "not_applicable";

                throw new InvalidOperationException(
                    $"Identifier convergence failed at {ordinal}:{operation.Intent.Kind}. "
                    + $"Catalog expression: {detail}",
                    exception);
            }
        }

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("identifier-instance"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.NotEmpty(builder.Operations);
        Assert.Equal(builder.Operations.Count, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
    }

    /// <summary>Completes an ASCII-prefixed identifier to PostgreSQL's UTF-8 byte limit.</summary>
    /// <param name="prefix">The descriptive identifier prefix.</param>
    /// <returns>The identifier padded to exactly 63 UTF-8 bytes.</returns>
    private static string PostgreSqlMaximumIdentifier(
        string prefix
    )
    {
        var remainingBytes = 63 - Encoding.UTF8.GetByteCount(prefix);

        return prefix + new string('x', remainingBytes);
    }
}
