namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves common helpers and nested EF model identities without inherited runnable tests.</summary>
public abstract partial class MySqlSafeMigrationIntegrationTests : MySqlIntegrationTestBase
{
    protected MySqlSafeMigrationIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    protected static SafeMigrationSqlExpression SqlColumn(
        string name
    ) => SafeMigrationSql.Identifier(name);

    protected static SafeMigrationSqlExpression SqlBinary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    ) => SafeMigrationSql.Binary(left, @operator, right);

    protected static SafeMigrationSqlExpression SqlColumnAndColumn(
        string left,
        SafeMigrationSqlBinaryOperator @operator,
        string right
    ) => SqlBinary(SqlColumn(left), @operator, SqlColumn(right));

    protected static SafeMigrationSqlExpression SqlColumnAndInt(
        string column,
        SafeMigrationSqlBinaryOperator @operator,
        int value
    ) => SqlBinary(SqlColumn(column), @operator, SafeMigrationSql.Literal(value));

    protected static SafeMigrationSqlExpression SqlFunction(
        string name,
        string column
    ) => SafeMigrationSql.Function(name, SqlColumn(column));

    protected sealed class UnmappedValue;

    protected static string MySqlMaximumIdentifier(
        string prefix
    ) => prefix + new string('x', 64 - prefix.Length);

    /// <summary>Requires an owned guard diagnostic while preserving its exact native duplicate-key cause.</summary>
    /// <param name="exception">The failure captured from the generated safe migration command.</param>
    protected static MySqlException AssertOwnedGuardFailure(
        Exception? exception
    )
    {
        // WHY: Native application, setup and cleanup failures must not satisfy an owned-guard assertion.
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(exception);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);
        Assert.Equal(1062, native.Number);
        Assert.Equal("23000", native.SqlState);
        Assert.Contains(diagnostic.GuardCode, native.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(diagnostic.ReasonCode));
        Assert.NotNull(diagnostic.OperationOrdinal);

        return native;
    }

    /// <summary>Requires the owned guard wrapper on the asynchronous provider execution path.</summary>
    /// <param name="action">The generated safe migration command to execute.</param>
    protected static async Task<MySqlException> AssertOwnedGuardFailureAsync(
        Func<Task> action
    )
    {
        var exception = await Assert.ThrowsAsync<SafeMigrationExecutionException>(action);

        return AssertOwnedGuardFailure(exception);
    }

    /// <summary>Requires the owned atomic-add CHECK, not ordinary constraint or MODIFY errors.</summary>
    /// <param name="exception">The failure from the admitted staged required-column migration body.</param>
    protected MySqlException AssertOwnedAtomicCheckFailure(Exception? exception)
    {
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(exception);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);
        Assert.Equal("doka_sm_data_blocked", diagnostic.GuardCode);
        Assert.Equal("column_atomic_add_check_failed", diagnostic.ReasonCode);
        Assert.Equal(Fixture.IsMariaDb ? 4025 : 3819, native.Number);
        Assert.Equal(Fixture.IsMariaDb ? "23000" : "HY000", native.SqlState);
        Assert.NotNull(diagnostic.OperationOrdinal);

        return native;
    }

    protected sealed class ConflictingSafeMigrationHandler : IMySqlMigrationOperationHandler
    {
        public string HandlerId => "safe_migrations_tests.conflict";

        public Type OperationType => typeof(SafeMigrationOperation);

        public MySqlMigrationOperationResult Generate(
            MySqlMigrationOperationContext context
        ) => throw new InvalidOperationException("A conflicting handler must never execute.");
    }

    protected sealed class CleanupFailureOperation : MigrationOperation;

    protected sealed class CleanupFailureHandler : IMySqlMigrationOperationHandler
    {
        public string HandlerId => "safe_migrations_tests.cleanup_failure";

        public Type OperationType => typeof(CleanupFailureOperation);

        public MySqlMigrationOperationResult Generate(
            MySqlMigrationOperationContext context
        ) => MySqlMigrationOperationResult.Generated(
            [
                MySqlMigrationCommandSpec.CreateScoped(
                    ["SET @safe_migrations_cleanup_probe = CONNECTION_ID();"],
                    "DO 0;",
                    ["SELECT * FROM `__doka_missing_cleanup_table`;"],
                    transactionSuppressed: true)
            ],
            "cleanup_failure_probe");
    }

    protected sealed class SchemaChangingDerivedContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : SafeMigrationDbContext(connectionString, serverVersion)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    private sealed class DerivedEntity
    {
        public int Id { get; set; }
    }
}
