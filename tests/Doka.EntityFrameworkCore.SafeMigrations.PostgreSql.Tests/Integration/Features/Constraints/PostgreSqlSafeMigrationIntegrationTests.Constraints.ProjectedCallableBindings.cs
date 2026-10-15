namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedCallableBindings PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedCallableBindingsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedCallableBindingsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>An exact custom VARCHAR overload cannot inherit a builtin LOWER-to-TEXT match certificate.</summary>
    /// <param name="defaultSuffix">Whether the exact supplied VARCHAR prefix has an omitted default argument.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingCheckDoesNotBorrowBuiltinLowerCastAcrossCustomOverload(bool defaultSuffix)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var signature = defaultSuffix ? "varchar, boolean DEFAULT false" : "varchar";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id varchar(32) NOT NULL, added integer NOT NULL, "
            + "CONSTRAINT ck_lower_binding CHECK (LOWER(id) >= '' AND added >= 0)); "
            + $"CREATE FUNCTION public.lower({signature}) RETURNS text LANGUAGE sql IMMUTABLE "
            + "AS 'SELECT upper($1::text)';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_lower_binding",
            "mixed_columns", SqlBinary(SqlBinary(SafeMigrationSql.Function("LOWER",
                    SafeMigrationSql.Identifier("id")), SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                SafeMigrationSql.Literal(string.Empty)), SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-lower-overload-boundary"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.Equal("P1001", exception.SqlState);
    }

    /// <summary>Equal function result types cannot certify an ambiguous native input overload.</summary>
    /// <param name="exact">Whether the live argument has an exact independently captured native signature.</param>
    /// <param name="defaultPrefix">Whether two functions share the same supplied prefix but different defaults.</param>
    /// <param name="variadicCompetitor">
    /// Whether a same-schema variadic signature competes with the exact ordinary one.
    /// </param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, false, true)]
    public async Task EmptyMixedCheckRequiresUnambiguousCallableArgumentBinding(
        bool exact,
        bool defaultPrefix,
        bool variadicCompetitor = false)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var type = exact || defaultPrefix ? "integer" : "smallint";
        var firstSignature = defaultPrefix ? "integer, boolean DEFAULT false" : "integer";
        var secondSignature = defaultPrefix ? "integer, text DEFAULT ''"
            : variadicCompetitor ? "VARIADIC integer[]" : "bigint";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE mixed_columns (id {type} NOT NULL); CREATE TABLE callback_audit (id integer); "
            + $"CREATE FUNCTION metadata_ambiguous({firstSignature}) RETURNS boolean LANGUAGE plpgsql AS "
            + "'BEGIN INSERT INTO callback_audit VALUES (1); RETURN TRUE; END'; "
            + $"CREATE FUNCTION metadata_ambiguous({secondSignature}) RETURNS boolean LANGUAGE plpgsql AS "
            + "'BEGIN INSERT INTO callback_audit VALUES (2); RETURN TRUE; END';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_callable_binding",
            "mixed_columns", SqlBinary(SafeMigrationSql.Function("metadata_ambiguous",
                    SafeMigrationSql.Identifier("id")), SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-callable-arguments"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (exact)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-callable-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sparse-check-callable-replay"), CancellationToken.None);
        }
        else
        {
            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                "ALTER TABLE mixed_columns ADD CONSTRAINT native_probe CHECK (metadata_ambiguous(id));"));
        }

        var callbacks = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM callback_audit;");

        // Assert
        Assert.Equal(0, callbacks);
        Assert.Equal(exact ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        if (exact)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else
        {
            Assert.Equal("42725", nativeError!.SqlState);
            Assert.Equal("projected_check_binding_unproven", report.Assessments[1].AnalysisCode);
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        }
    }

}
