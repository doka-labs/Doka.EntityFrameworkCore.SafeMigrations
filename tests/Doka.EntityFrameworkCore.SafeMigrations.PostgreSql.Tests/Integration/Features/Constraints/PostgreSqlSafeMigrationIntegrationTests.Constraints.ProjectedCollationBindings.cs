namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedCollationBindings PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedCollationBindingsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedCollationBindingsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>A COLLATE node must resolve both its operand's native collatability and the exact collation.</summary>
    /// <param name="text">Whether the unchanged live operand is a collatable native type.</param>
    /// <param name="exists">Whether the exact explicitly requested native collation exists.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EmptyMixedCheckRequiresNativeCollatableOperandAndKnownCollation(bool text, bool exists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var type = text ? "text" : "integer";
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE mixed_columns (id {type} NOT NULL);");
        await using var context = CreateContext(connectionString);
        var collation = exists ? "C" : "absent_collation";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var comparison = text ? SafeMigrationSql.Literal(string.Empty) : SafeMigrationSql.Literal(0);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_collation_binding",
            "mixed_columns", SqlBinary(SqlBinary(SafeMigrationSql.Collate(SafeMigrationSql.Identifier("id"),
                    collation), SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, comparison),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-collation-binding"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (text && exists)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-collation-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-collation-replay"), CancellationToken.None);
        }
        else
        {
            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                $"ALTER TABLE mixed_columns ADD CONSTRAINT native_probe CHECK ((id COLLATE \"{collation}\") "
                + (text ? ">= ''" : ">= 0") + ");"));
        }

        // Assert
        Assert.Equal(text && exists ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);

        if (text && exists)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else
        {
            Assert.Equal(text ? "42704" : "42804", nativeError!.SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        }
    }

}
