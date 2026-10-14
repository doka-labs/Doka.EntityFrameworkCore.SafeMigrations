namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedUnknownLiterals PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedUnknownLiteralsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedUnknownLiteralsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>A failed SMALLINT unknown-input conversion cannot borrow a different native TEXT operator.</summary>
    [Fact]
    public async Task MatchingCheckDoesNotBorrowTextOperatorForOutOfRangeIntegerInput()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE FUNCTION metadata_equal(smallint, text) RETURNS boolean LANGUAGE sql IMMUTABLE "
            + "AS 'SELECT TRUE'; CREATE OPERATOR public.= (LEFTARG = smallint, RIGHTARG = text, "
            + "FUNCTION = metadata_equal); CREATE TABLE mixed_columns (id smallint NOT NULL, "
            + "CONSTRAINT ck_integer_input CHECK (id = CAST('40000' AS text)));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_integer_input",
            "mixed_columns", SqlBinary(SafeMigrationSql.Identifier("id"), SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("40000"))), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-integer-range-boundary"), CancellationToken.None));

        // Assert
        // WHY: PostgreSQL independently binds the existing validation probe;
        // the authored unknown SMALLINT input fails before a Matching decision.
        // This is a negative control, not evidence that a catalog match bypassed binding.
        Assert.Equal("22003", exception.SqlState);
    }

    /// <summary>Unknown SQL string inputs cannot erase their native value-conversion requirement.</summary>
    /// <param name="literal">The unknown string compared against the accepted new integer column.</param>
    /// <param name="approved">Whether the bounded builtin integer proof can certify the exact input.</param>
    /// <param name="useIn">Whether the same conversion occurs through the native IN comparison family.</param>
    /// <param name="typedCast">Whether the literal explicitly carries TEXT storage before an integer CAST.</param>
    /// <param name="typedLiteral">Whether the literal directly requests integer input storage.</param>
    [Theory]
    [InlineData("not-an-int", false, false)]
    [InlineData("2147483648", false, false)]
    [InlineData("1", true, false)]
    [InlineData("not-an-int", false, true)]
    [InlineData("2147483648", false, true)]
    [InlineData("1", true, true)]
    [InlineData("not-an-int", false, false, true)]
    [InlineData("2147483648", false, false, true)]
    [InlineData("1", true, false, true)]
    [InlineData("not-an-int", false, false, false, true)]
    [InlineData("2147483648", false, false, false, true)]
    [InlineData("1", true, false, false, true)]
    public async Task EmptyMixedCheckPreservesUnknownIntegerLiteralProvenance(
        string literal,
        bool approved,
        bool useIn,
        bool typedCast = false,
        bool typedLiteral = false)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (old_int integer NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("new_int", "mixed_columns", type: "integer", nullable: false);
        var operand = typedCast
            ? SafeMigrationSql.Cast(SafeMigrationSql.Literal(literal, "text"), "integer")
            : SafeMigrationSql.Literal(literal, typedLiteral ? "integer" : null);

        var comparison = useIn
            ? SafeMigrationSql.In(SafeMigrationSql.Identifier("new_int"), [SafeMigrationSql.Literal(literal)])
            : SqlBinary(SafeMigrationSql.Identifier("new_int"), SafeMigrationSqlBinaryOperator.Equal,
                operand);

        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_integer_input",
            "mixed_columns", SqlBinary(SqlColumnAndInt("old_int", SafeMigrationSqlBinaryOperator.GreaterThan, 0),
                SafeMigrationSqlBinaryOperator.And, comparison)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-unknown-integer-input"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;

        if (approved)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-unknown-integer-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-unknown-integer-replay"), CancellationToken.None);
        }

        // Assert
        Assert.Equal(approved ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(approved ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[1].Action);

        if (!approved)
        {
            Assert.Equal("projected_check_binding_unproven", report.Assessments[1].AnalysisCode);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
    }

}
