namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks generated identity qualification without relying on tuple-member nullability correlations.</summary>
public sealed class MySqlGeneratedIdentityQualificationTests
{
    /// <summary>Both generated column intents retain MySQL's exact source-owner guard.</summary>
    /// <param name="mariaDb">Whether the native engine permits an identity source.</param>
    /// <param name="alter">Whether the generated definition replaces an existing column.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GeneratedColumnGuardsRemainEngineAndOwnerSpecific(bool mariaDb, bool alter)
    {
        // Arrange
        using var context = Context(mariaDb);
        var definition = new ExpectedColumnDefinition("derived", typeof(int), true, "int",
            computedExpression: SafeMigrationSql.Identifier("source"), isStored: false);

        SafeMigrationIntent intent = alter
            ? new AlterColumnIntent("identity_owner", definition)
            : new EnsureColumnIntent("identity_owner", definition);

        var mappings = context.GetService<IRelationalTypeMappingSource>();

        // Act
        var plan = Capture(context, intent);
        var guard = plan.RenderStateEvaluationGuardExpression(value =>
            (mappings.FindMapping(value.Value?.GetType() ?? typeof(string))
                ?? throw new InvalidOperationException("The guard fixture requires a native literal mapping."))
            .GenerateSqlLiteral(value.Value));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal(!mariaDb, plan.StateEvaluationGuardExpression.Contains(
            "identity_source.TABLE_NAME", StringComparison.Ordinal));
        Assert.Equal(!mariaDb, plan.ClassificationCodeExpression?.Contains(
            "generated_column_identity_dependency", StringComparison.Ordinal) == true);
        if (!mariaDb)
        {
            Assert.Contains("identity_source.TABLE_NAME = 'identity_owner'", guard, StringComparison.Ordinal);
            Assert.Contains("identity_source.COLUMN_NAME IN ('source')", guard, StringComparison.Ordinal);
        }

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Missing tuple targets and ordinary columns do not acquire generated identity guards.</summary>
    /// <param name="mariaDb">Whether the provider uses MariaDB's native generated-column contract.</param>
    /// <param name="drop">Whether the operation has no generated column target.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonGeneratedTargetsDoNotAcquireIdentityQualification(bool mariaDb, bool drop)
    {
        // Arrange
        using var context = Context(mariaDb);
        SafeMigrationIntent intent = drop
            ? new DropColumnIntent("ordinary", "identity_owner")
            : new EnsureColumnIntent("identity_owner",
                new ExpectedColumnDefinition("ordinary", typeof(int), true, "int"));

        // Act
        var plan = Capture(context, intent);

        // Assert
        Assert.DoesNotContain("identity_source", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("generated_column_identity_dependency",
            plan.ClassificationCodeExpression ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Captures the actual SQL generator's catalog plan without opening a database connection.</summary>
    private static MySqlSafeMigrationRuntimePlan Capture(DbContext context, SafeMigrationIntent intent)
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return Assert.Single(lease.Complete());
    }

    /// <summary>Uses the active provider mappings with a deliberately unreachable database endpoint.</summary>
    private static DbContext Context(bool mariaDb)
    {
        var version = mariaDb
            ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
            : MySqlServerVersion.MySql(new Version(8, 4, 11));

        return new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=127.0.0.1;Port=1;Database=identity_qualification;User ID=test;Password=test", version)
            .UseMySqlSafeMigrations().Options);
    }
}
