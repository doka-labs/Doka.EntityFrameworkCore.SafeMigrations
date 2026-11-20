namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves common helpers and nested EF model identities without inherited runnable tests.</summary>
public abstract partial class MySqlSafeMigrationIntegrationTests : MySqlIntegrationTestBase
{
    /// <summary>Binds this test owner to its explicitly owned provider resources.</summary>
    /// <param name="fixture">The initialized shared native engine without transferring its lifetime.</param>
    protected MySqlSafeMigrationIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Creates a structured column identifier without introducing opaque SQL.</summary>
    /// <param name="name">The authored object, function, or identifier name.</param>
    protected static SafeMigrationSqlExpression SqlColumn(
        string name
    ) => SafeMigrationSql.Identifier(name);

    /// <summary>Creates an explicitly grouped structured binary expression from the supplied operands.</summary>
    /// <param name="left">The structured left operand or column identity.</param>
    /// <param name="operator">The structured binary operator, not an arbitrary SQL fragment.</param>
    /// <param name="right">The structured right operand or column identity.</param>
    protected static SafeMigrationSqlExpression SqlBinary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    ) => SafeMigrationSql.Binary(left, @operator, right);

    /// <summary>Creates a structured comparison or operation over two column identities.</summary>
    /// <param name="left">The structured left operand or column identity.</param>
    /// <param name="operator">The structured binary operator, not an arbitrary SQL fragment.</param>
    /// <param name="right">The structured right operand or column identity.</param>
    protected static SafeMigrationSqlExpression SqlColumnAndColumn(
        string left,
        SafeMigrationSqlBinaryOperator @operator,
        string right
    ) => SqlBinary(SqlColumn(left), @operator, SqlColumn(right));

    /// <summary>Creates a structured column operation with a typed integer literal.</summary>
    /// <param name="column">The source or target column identity selected by the case.</param>
    /// <param name="operator">The structured binary operator, not an arbitrary SQL fragment.</param>
    /// <param name="value">The authored typed literal whose value and storage semantics must be preserved.</param>
    protected static SafeMigrationSqlExpression SqlColumnAndInt(
        string column,
        SafeMigrationSqlBinaryOperator @operator,
        int value
    ) => SqlBinary(SqlColumn(column), @operator, SafeMigrationSql.Literal(value));

    /// <summary>Creates a structured function invocation over the selected column.</summary>
    /// <param name="name">The authored object, function, or identifier name.</param>
    /// <param name="column">The source or target column identity selected by the case.</param>
    protected static SafeMigrationSqlExpression SqlFunction(
        string name,
        string column
    ) => SafeMigrationSql.Function(name, SqlColumn(column));

    /// <summary>Creates an exact-limit identifier while retaining the supplied distinguishing prefix.</summary>
    /// <param name="prefix">The distinguishing prefix retained in the exact-limit identifier.</param>
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
    /// <param name="action">The generated safe migration command receiving the caller's cancellation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<MySqlException> AssertOwnedGuardFailureAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exception = await Assert.ThrowsAsync<SafeMigrationExecutionException>(() => action(cancellationToken));

        return AssertOwnedGuardFailure(exception);
    }

    /// <summary>Requires the owned atomic-add CHECK, not ordinary constraint or MODIFY errors.</summary>
    /// <param name="exception">The failure from the admitted staged required-column migration body.</param>
    protected MySqlException AssertOwnedAtomicCheckFailure(
        Exception? exception
    )
    {
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(exception);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);
        Assert.Equal("doka_sm_data_blocked", diagnostic.GuardCode);
        Assert.Equal("column_atomic_add_check_failed", diagnostic.ReasonCode);
        Assert.Equal(
            Fixture.IsMariaDb
                ? 4025
                : 3819,
            native.Number);

        Assert.Equal(
            Fixture.IsMariaDb
                ? "23000"
                : "HY000",
            native.SqlState);

        Assert.NotNull(diagnostic.OperationOrdinal);

        return native;
    }

    /// <summary>Supplies a CLR value with no native mapping for fail-closed type probes.</summary>
    protected sealed class UnmappedValue;

    /// <summary>Intentionally competes for operation ownership to exercise duplicate-handler rejection.</summary>
    protected sealed class ConflictingSafeMigrationHandler : IMySqlMigrationOperationHandler
    {
        /// <inheritdoc />
        public string HandlerId => "safe_migrations_tests.conflict";

        /// <inheritdoc />
        public Type OperationType => typeof(SafeMigrationOperation);

        /// <inheritdoc />
        public MySqlMigrationOperationResult Generate(
            MySqlMigrationOperationContext context
        ) => throw new InvalidOperationException("A conflicting handler must never execute.");
    }

    /// <summary>Selects the test handler whose native scoped cleanup deliberately fails.</summary>
    protected sealed class CleanupFailureOperation : MigrationOperation;

    /// <summary>Generates a scoped native cleanup failure without changing ordinary operation ownership.</summary>
    protected sealed class CleanupFailureHandler : IMySqlMigrationOperationHandler
    {
        /// <inheritdoc />
        public string HandlerId => "safe_migrations_tests.cleanup_failure";

        /// <inheritdoc />
        public Type OperationType => typeof(CleanupFailureOperation);

        /// <inheritdoc />
        public MySqlMigrationOperationResult Generate(
            MySqlMigrationOperationContext context
        ) => MySqlMigrationOperationResult.Generated(
            [
                MySqlMigrationCommandSpec.CreateScoped(
                    ["SET @safe_migrations_cleanup_probe = CONNECTION_ID();"],
                    "DO 0;",
                    ["SELECT * FROM `__doka_missing_cleanup_table`;"],
                    transactionSuppressed: true),
            ],
            "cleanup_failure_probe");
    }

    /// <summary>Changes the derived schema to exercise rejection against the canonical snapshot.</summary>
    protected sealed class SchemaChangingDerivedContext : SafeMigrationDbContext
    {
        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="connectionString">The uniquely owned native test database.</param>
        /// <param name="serverVersion">The native engine contract selected by the fixture.</param>
        public SchemaChangingDerivedContext(
            string connectionString,
            MySqlServerVersion serverVersion
        ) : base(connectionString, serverVersion) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class DerivedEntity
    {
        public int Id { get; set; }
    }
}
