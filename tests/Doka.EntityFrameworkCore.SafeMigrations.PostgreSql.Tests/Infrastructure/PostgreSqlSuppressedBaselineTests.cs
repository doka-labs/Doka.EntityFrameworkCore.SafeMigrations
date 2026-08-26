namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlSuppressedBaselineTests
{
    private const string AddColumnSql = "ALTER TABLE \"items\" ADD COLUMN \"value\" integer;";
    private const string CommentColumnSql = "COMMENT ON COLUMN \"items\".\"value\" IS 'baseline marker';";

    [Theory]
    [InlineData(false, MigrationsSqlGenerationOptions.Default)]
    [InlineData(true, MigrationsSqlGenerationOptions.Default)]
    [InlineData(false, MigrationsSqlGenerationOptions.NoTransactions)]
    [InlineData(true, MigrationsSqlGenerationOptions.NoTransactions)]
    public void SuppressedColumnBaselineRejectsBeforeDelegatingAGuard(
        bool hasPrecedingUnsuppressedCommand,
        MigrationsSqlGenerationOptions options
    )
    {
        using var context = CreateContext();
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = CreateColumnMigration(context);
        baseline.ColumnBaselineCommands = hasPrecedingUnsuppressedCommand
            ? [baseline.CreateCommand(AddColumnSql, false), baseline.CreateCommand(CommentColumnSql, true)]
            : [baseline.CreateCommand(AddColumnSql, true)];

        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate(
            migrationBuilder.Operations,
            context.Model,
            options));

        Assert.Equal(
            "A transaction-suppressed PostgreSQL baseline cannot be guarded inside a DO block.",
            exception.Message);
        var call = AssertRejectedBeforeGuardDelegation(baseline, context.Model, options);
        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(call.Operations));

        Assert.Equal("items", operation.Table);
        Assert.Equal("value", operation.Name);
        Assert.Same(baseline.ColumnBaselineCommands, call.Commands);
        Assert.True(call.Commands[call.Commands.Count - 1].TransactionSuppressed);
        if (hasPrecedingUnsuppressedCommand)
        {
            Assert.False(call.Commands[0].TransactionSuppressed);
        }
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.NoTransactions)]
    public void SuppressedQualifiedCollationBaselineRejectsBeforeDelegatingAGuard(
        MigrationsSqlGenerationOptions options
    )
    {
        const string addTextColumnSql = "ALTER TABLE \"items\" ADD COLUMN \"value\" text;";
        const string collationSql =
            "ALTER TABLE \"items\" ALTER COLUMN \"value\" TYPE text COLLATE \"pg_catalog\".\"C\";";

        using var context = CreateContext();
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "text",
                collation: new SafeMigrationCollationIdentifier("C", "pg_catalog")),
            SafeMigrationPolicy.ThrowIfDifferent);
        baseline.ColumnBaselineCommands =
        [
            baseline.CreateCommand(addTextColumnSql, false), baseline.CreateCommand(collationSql, true),
        ];

        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate(
            migrationBuilder.Operations,
            context.Model,
            options));

        Assert.Equal(
            "A transaction-suppressed PostgreSQL baseline cannot be guarded inside a DO block.",
            exception.Message);
        var call = AssertRejectedBeforeGuardDelegation(baseline, context.Model, options);

        Assert.Equal(2, call.Operations.Count);
        var column = Assert.IsType<AddColumnOperation>(call.Operations[0]);
        var qualifiedCollation = Assert.IsType<SqlOperation>(call.Operations[1]);
        var qualifiedIdentifier = context
            .GetService<ISqlGenerationHelper>()
            .DelimitIdentifier("C", "pg_catalog");

        Assert.Null(column.Collation);
        Assert.Contains(" COLLATE " + qualifiedIdentifier, qualifiedCollation.Sql, StringComparison.Ordinal);
        Assert.False(qualifiedCollation.SuppressTransaction);
        Assert.Same(baseline.ColumnBaselineCommands, call.Commands);
        Assert.False(call.Commands[0].TransactionSuppressed);
        Assert.True(call.Commands[1].TransactionSuppressed);
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.NoTransactions)]
    public void UnsuppressedColumnBaselineKeepsEveryCommandInsideOneGuard(
        MigrationsSqlGenerationOptions options
    )
    {
        using var context = CreateContext();
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = CreateColumnMigration(context);
        baseline.ColumnBaselineCommands =
        [
            baseline.CreateCommand(AddColumnSql, false), baseline.CreateCommand(CommentColumnSql, false),
        ];

        var commands = generator.Generate(migrationBuilder.Operations, context.Model, options);

        var command = Assert.Single(commands);
        Assert.Equal(2, baseline.Calls.Count);
        Assert.Same(baseline.ColumnBaselineCommands, baseline.Calls[0].Commands);
        Assert.IsType<AddColumnOperation>(Assert.Single(baseline.Calls[0].Operations));
        var guardedOperation = Assert.IsType<SqlOperation>(Assert.Single(baseline.Calls[1].Operations));

        Assert.StartsWith("DO $doka_", guardedOperation.Sql, StringComparison.Ordinal);
        Assert.Contains(AddColumnSql, guardedOperation.Sql, StringComparison.Ordinal);
        Assert.Contains(CommentColumnSql, guardedOperation.Sql, StringComparison.Ordinal);
        Assert.True(
            guardedOperation.Sql.IndexOf(AddColumnSql, StringComparison.Ordinal)
            < guardedOperation.Sql.IndexOf(CommentColumnSql, StringComparison.Ordinal));
        Assert.False(guardedOperation.SuppressTransaction);
        Assert.Same(Assert.Single(baseline.Calls[1].Commands), command);
        Assert.Equal(guardedOperation.Sql, command.CommandText);
        Assert.False(command.TransactionSuppressed);
        Assert.All(
            baseline.Calls,
            call =>
            {
                Assert.Same(context.Model, call.Model);
                Assert.Equal(options, call.Options);
            });
    }

    [Fact]
    public void OrdinarySuppressedSqlPreservesItsCommandAndOrderBeforeASafeOperation()
    {
        const string ordinarySql = "VACUUM \"items\";";

        using var context = CreateContext();
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.Sql(ordinarySql, suppressTransaction: true);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "integer"),
            SafeMigrationPolicy.ThrowIfDifferent);
        baseline.ColumnBaselineCommands = [baseline.CreateCommand(AddColumnSql, false)];

        var commands = generator.Generate(migrationBuilder.Operations, context.Model);

        Assert.Equal(2, commands.Count);
        Assert.Equal(3, baseline.Calls.Count);
        Assert.Same(migrationBuilder.Operations[0], Assert.Single(baseline.Calls[0].Operations));
        Assert.Same(Assert.Single(baseline.Calls[0].Commands), commands[0]);
        Assert.Equal(ordinarySql, commands[0].CommandText);
        Assert.True(commands[0].TransactionSuppressed);
        Assert.IsType<AddColumnOperation>(Assert.Single(baseline.Calls[1].Operations));
        Assert.Same(Assert.Single(baseline.Calls[2].Commands), commands[1]);
        Assert.StartsWith("DO $doka_", commands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains(AddColumnSql, commands[1].CommandText, StringComparison.Ordinal);
        Assert.False(commands[1].TransactionSuppressed);
        Assert.All(
            baseline.Calls,
            call =>
            {
                Assert.Same(context.Model, call.Model);
                Assert.Equal(MigrationsSqlGenerationOptions.Default, call.Options);
            });
    }

    private static SafeMigrationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=suppression_contract;Username=test;Password=test");
        ((DbContextOptionsBuilder)options)
            .UsePostgreSqlSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();

        // SQL generation is synchronous and never opens the configured connection.
        return new SafeMigrationDbContext(options.Options);
    }

    private static MigrationBuilder CreateColumnMigration(
        DbContext context
    )
    {
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(int),
                isNullable: true,
                storeType: "integer",
                comment: "baseline marker"),
            SafeMigrationPolicy.ThrowIfDifferent);

        return migrationBuilder;
    }

    private static BaselineCall AssertRejectedBeforeGuardDelegation(
        RecordingBaselineGenerator baseline,
        IModel model,
        MigrationsSqlGenerationOptions options
    )
    {
        var call = Assert.Single(baseline.Calls);

        Assert.Same(model, call.Model);
        Assert.Equal(options, call.Options);
        Assert.DoesNotContain(
            call.Operations,
            static operation => operation is SqlOperation sql
                && sql.Sql.StartsWith("DO $doka_", StringComparison.Ordinal));

        return call;
    }

    private sealed record BaselineCall(
        IReadOnlyList<MigrationOperation> Operations,
        IModel? Model,
        MigrationsSqlGenerationOptions Options,
        IReadOnlyList<MigrationCommand> Commands
    );

    private sealed class RecordingBaselineGenerator : IMigrationsSqlGenerator
    {
        private readonly MigrationsSqlGeneratorDependencies _dependencies;

        public RecordingBaselineGenerator(
            MigrationsSqlGeneratorDependencies dependencies
        )
        {
            _dependencies = dependencies;
        }

        public IReadOnlyList<MigrationCommand> ColumnBaselineCommands { get; set; } = [];

        public List<BaselineCall> Calls { get; } = [];

        public MigrationCommand CreateCommand(
            string sql,
            bool transactionSuppressed
        )
        {
            var command = _dependencies
                .CommandBuilderFactory
                .Create()
                .Append(sql)
                .Build();

            return new MigrationCommand(
                command,
                _dependencies.CurrentContext.Context,
                _dependencies.Logger,
                transactionSuppressed);
        }

        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
        )
        {
            // Only the column baseline is replaced. Ordinary SQL and generated guards
            // retain their actual text and suppression flag, so rejection cannot pass
            // merely because the test double fails on an unexpected guard call.
            var commands = operations.Any(static operation => operation is AddColumnOperation)
                ? ColumnBaselineCommands
                : operations
                    .Select(operation =>
                    {
                        var sqlOperation = Assert.IsType<SqlOperation>(operation);
                        return CreateCommand(sqlOperation.Sql, sqlOperation.SuppressTransaction);
                    })
                    .ToArray();

            Calls.Add(new BaselineCall(operations.ToArray(), model, options, commands));
            return commands;
        }
    }
}
