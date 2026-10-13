namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>A late committed NULL cannot be coerced after an earlier runtime NULL-free proof.</summary>
    /// <param name="ensure">Whether the target is EnsureColumn rather than an explicit AlterColumn.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullabilityTighteningLateNullWriterFailsWithoutInventingValues(bool ensure)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, NullabilityConcurrencyFixtureSql());
        var observer = new NullabilityConcurrencyWriter(connectionString, insertNull: true);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES';");
        var operation = NullabilityConcurrencyOperation(ensure);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var nullable = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'nullability_race_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'YES';");
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `nullability_race_items` WHERE `id` = 2 AND `value` IS NULL;");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `nullability_race_items` WHERE `id` = 1 "
            + "AND `value` = '00000000-0000-0000-0000-000000000001';");
        var invented = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `nullability_race_items` WHERE `value` = '' "
            + "OR `value` = '00000000-0000-0000-0000-000000000000';");

        // Assert
        var providerException = Assert.IsType<MySqlException>(exception);

        Assert.True(observer.ProvenRepairCheckpointObserved);
        Assert.True(observer.WasInjected);
        Assert.Equal(1, observer.CommittedRows);
        Assert.True(Fixture.IsMariaDb ? providerException.Number == 1265 : providerException.Number == 1138,
            $"Unexpected native NULL failure {providerException.Number}: {providerException.Message}");
        Assert.DoesNotContain("doka_sm_", providerException.Message, StringComparison.Ordinal);
        Assert.Equal(1, nullable);
        Assert.Equal(1, nullRows);
        Assert.Equal(1, preserved);
        Assert.Equal(0, invented);
    }

    /// <summary>
    /// A late explicit non-NULL row remains valid and survives the same guarded required-column repair.
    /// </summary>
    /// <param name="ensure">Whether the target is EnsureColumn rather than an explicit AlterColumn.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullabilityTighteningLateExplicitWriterSurvivesRequiredRepair(bool ensure)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, NullabilityConcurrencyFixtureSql());
        var observer = new NullabilityConcurrencyWriter(connectionString, insertNull: false);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES';");
        var operation = NullabilityConcurrencyOperation(ensure);

        // Act
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'nullability_race_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `nullability_race_items` WHERE "
            + "(`id` = 1 AND `value` = '00000000-0000-0000-0000-000000000001') "
            + "OR (`id` = 2 AND `value` = '00000000-0000-0000-0000-000000000002');");

        // Assert
        Assert.True(observer.ProvenRepairCheckpointObserved);
        Assert.True(observer.WasInjected);
        Assert.Equal(1, observer.CommittedRows);
        Assert.Equal(1, required);
        Assert.Equal(2, preserved);
    }

    /// <summary>Creates a populated nullable source whose initial row domain contains no NULL values.</summary>
    /// <returns>The anonymous isolated fixture with one explicit GUID sentinel.</returns>
    private static string NullabilityConcurrencyFixtureSql()
        => "CREATE TABLE `nullability_race_items` (`id` int NOT NULL PRIMARY KEY, `value` char(36) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `nullability_race_items` (`id`, `value`) "
            + "VALUES (1, '00000000-0000-0000-0000-000000000001');";

    /// <summary>
    /// Captures both supported required-column APIs without declaring a replacement or default value.
    /// </summary>
    /// <param name="ensure">Whether to capture an Ensure rather than an explicit Alter intent.</param>
    /// <returns>The immutable no-default required-column request.</returns>
    private static SafeMigrationOperation NullabilityConcurrencyOperation(bool ensure)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        if (ensure)
        {
            builder.AddColumnIfNotExistsFromModel(
                operationFactory: static operations => operations.AddColumn<Guid>(
                        name: "value", table: "nullability_race_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<Guid>(
                        name: "value", table: "nullability_race_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: false,
                        oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true,
                        oldMaxLength: 36, oldNullable: true)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                    .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Commits an external row exactly after runtime proof setup and before the real guarded body.</summary>
    /// <remarks>
    /// WHY: A before-preflight or before-setup writer only tests ordinary data
    /// classification. This synchronous checkpoint narrows the interleaving to
    /// the actual proof-to-MODIFY window without sleeps or production hooks.
    /// </remarks>
    private sealed class NullabilityConcurrencyWriter : DbCommandInterceptor
    {
        private readonly string _connectionString;
        private readonly bool _insertNull;

        /// <summary>Initializes an independent writer with one caller-controlled late row.</summary>
        /// <param name="connectionString">The isolated fixture database connection.</param>
        /// <param name="insertNull">Whether the late value violates the requested NOT NULL contract.</param>
        public NullabilityConcurrencyWriter(string connectionString, bool insertNull)
        {
            _connectionString = connectionString;
            _insertNull = insertNull;
        }

        /// <summary>Gets whether the actual runtime decision and NULL-free proof preceded the injection.</summary>
        public bool ProvenRepairCheckpointObserved { get; private set; }

        /// <summary>Gets whether the external committed write occurred before body execution.</summary>
        public bool WasInjected { get; private set; }

        /// <summary>Gets the affected external row count, proving the writer was not vacuous.</summary>
        public int CommittedRows { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (WasInjected
                || !command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal)
                || !command.CommandText.Contains("EXECUTE doka_sm_statement;", StringComparison.Ordinal))
            {
                return result;
            }

            var migrationConnection = command.Connection
                ?? throw new InvalidOperationException("The guarded body omitted its migration connection.");

            await using var checkpoint = migrationConnection.CreateCommand();
            checkpoint.CommandText = "SELECT COALESCE(@doka_sm_state = 'different', FALSE) "
                + "AND COALESCE(@doka_sm_action = 'repair', FALSE) "
                + "AND COALESCE(@doka_sm_repair_ok, FALSE) "
                + "AND NOT COALESCE(@doka_sm_nullability_blocked, TRUE);";
            ProvenRepairCheckpointObserved = Convert.ToBoolean(
                await checkpoint.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!ProvenRepairCheckpointObserved)
            {
                throw new InvalidOperationException("The writer did not reach a proven runtime NULL-free repair.");
            }

            await using var writer = new MySqlConnection(_connectionString);
            await writer.OpenAsync(cancellationToken);
            await using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO `nullability_race_items` (`id`, `value`) VALUES (2, "
                + (_insertNull ? "NULL" : "'00000000-0000-0000-0000-000000000002'") + ");";
            CommittedRows = await insert.ExecuteNonQueryAsync(cancellationToken);
            WasInjected = true;

            return result;
        }
    }
}
