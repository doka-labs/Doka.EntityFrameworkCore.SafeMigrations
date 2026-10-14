namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Native callable limits cannot be bypassed by expanded variadic metadata signatures.</summary>
    /// <param name="variadic">Whether the actual callable uses a variadic INTEGER input array.</param>
    /// <param name="overLimit">Whether the authored arguments exceed the actual native preset limit.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task EmptyMixedCheckHonorsNativeFunctionArgumentLimit(bool variadic, bool overLimit)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var limit = await ScalarIntAsync(connectionString, "SELECT current_setting('max_function_args')::integer;");
        var count = limit + (overLimit ? 1 : 0);
        var signature = variadic ? "VARIADIC integer[]" : string.Join(", ", Enumerable.Repeat("integer", limit));
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id integer NOT NULL); CREATE TABLE callback_audit (id integer); "
            + $"CREATE FUNCTION metadata_many({signature}) RETURNS boolean LANGUAGE plpgsql AS "
            + "'BEGIN INSERT INTO callback_audit VALUES (1); RETURN TRUE; END';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var arguments = Enumerable.Range(0, count)
            .Select(ordinal => SafeMigrationSql.Identifier(ordinal == 0 ? "id" : "added")).ToArray();

        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_callable_limit",
            "mixed_columns", SqlBinary(SafeMigrationSql.Function("metadata_many", arguments),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var approved = !variadic && !overLimit;

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-callable-limit"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (approved)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-callable-limit-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-callable-limit-replay"), CancellationToken.None);
        }
        else if (overLimit)
        {
            var nativeArguments = string.Join(", ", Enumerable.Repeat("id", count));
            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                $"ALTER TABLE mixed_columns ADD CONSTRAINT native_probe CHECK (metadata_many({nativeArguments}));"));
        }
        else
        {
            var nativeArguments = string.Join(", ", Enumerable.Repeat("id", count));
            await ExecuteSqlAsync(connectionString,
                $"ALTER TABLE mixed_columns ADD CONSTRAINT native_probe CHECK (metadata_many({nativeArguments}));");
        }

        var callbacks = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM callback_audit;");

        // Assert
        Assert.Equal(0, callbacks);
        Assert.Equal(approved ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        if (approved)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else if (overLimit)
        {
            Assert.Equal("54023", nativeError!.SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        }
        else
        {
            // WHY: Native parsing is valid, but its VARIADIC ARRAY form does
            // not yet have the provider's exact structured postcondition proof.
            // New sparse promotion cannot turn that boundary into Ready.
            Assert.Equal("projected_check_binding_unproven", report.Assessments[1].AnalysisCode);
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        }
    }
}
