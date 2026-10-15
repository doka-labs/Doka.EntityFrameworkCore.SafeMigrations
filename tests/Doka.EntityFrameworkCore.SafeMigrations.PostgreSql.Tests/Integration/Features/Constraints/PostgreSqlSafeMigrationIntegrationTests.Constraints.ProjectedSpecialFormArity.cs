namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedSpecialFormArity PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedSpecialFormArityIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedSpecialFormArityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Native common-type forms require inputs but are not ordinary function argument-limit consumers.
    /// </summary>
    /// <param name="function">The native special form, not a pg_proc callable.</param>
    /// <param name="empty">Whether its required input collection is empty.</param>
    [Theory]
    [InlineData("COALESCE", true)]
    [InlineData("GREATEST", true)]
    [InlineData("LEAST", true)]
    [InlineData("COALESCE", false)]
    [InlineData("GREATEST", false)]
    [InlineData("LEAST", false)]
    public async Task EmptyMixedCheckPreservesNativeSpecialFormArityContract(string function, bool empty)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var limit = await ScalarIntAsync(connectionString, "SELECT current_setting('max_function_args')::integer;");
        var count = empty ? 0 : limit + 1;
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var arguments = Enumerable.Range(0, count).Select(_ => SafeMigrationSql.Identifier("id")).ToArray();
        var comparison = empty ? SafeMigrationSql.Literal(string.Empty) : SafeMigrationSql.Literal(0);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_special_limit",
            "mixed_columns", SqlBinary(SqlBinary(SqlBinary(SafeMigrationSql.Function(function, arguments),
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, comparison),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-special-form-arity"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (!empty)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-special-form-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-special-form-replay"), CancellationToken.None);
        }
        else
        {
            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                $"ALTER TABLE mixed_columns ADD CONSTRAINT native_probe CHECK ({function}() >= 0);"));
        }

        // Assert
        Assert.Equal(empty ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        if (!empty)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else
        {
            Assert.Equal("42601", nativeError!.SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        }
    }

}
