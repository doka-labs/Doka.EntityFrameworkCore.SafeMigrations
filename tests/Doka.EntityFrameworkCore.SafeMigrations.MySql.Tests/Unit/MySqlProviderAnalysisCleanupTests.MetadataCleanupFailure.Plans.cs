namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Creates exact per-invocation controls for a native metadata resource boundary.</summary>
    /// <param name="stage">The selected command or reader stage.</param>
    /// <param name="primary">The actual work failure, when selected.</param>
    /// <param name="secondary">The selected actual resource disposal failure.</param>
    /// <param name="tertiary">The subsequent command disposal failure after a reader failure.</param>
    /// <param name="beforeWork">The native work callback requesting caller cancellation.</param>
    /// <returns>The explicit failure state used by the real analyzer's ADO wrappers.</returns>
    private static ProbeCleanupFailureState CreateMetadataCleanupState(
        string stage,
        Exception? primary,
        Exception? secondary,
        Exception? tertiary,
        Action<CancellationToken>? beforeWork
    ) => new(stage)
    {
        EnvironmentMarker = MetadataMarker(stage),
        WorkFailure = primary,
        DisposalFailure = secondary,
        CommandAfterReaderFailure = tertiary,
        BeforeWork = beforeWork,
    };

    /// <summary>Selects an actual native environment SQL fragment, not a fixture-only substitute.</summary>
    /// <param name="stage">The metadata family whose acquisition is observed.</param>
    /// <returns>The closed exact query fragment emitted by that family.</returns>
    private static string MetadataMarker(
        string stage
    ) => stage.StartsWith("index_", StringComparison.Ordinal)
        ? "@@innodb_default_row_format"
        : "@@SESSION.explicit_defaults_for_timestamp";

    /// <summary>Creates an owning or borrowed open connection without a live server.</summary>
    /// <param name="commands">The exact statement-aware native result producer.</param>
    /// <param name="state">The selected resource boundary.</param>
    /// <param name="initiallyOpen">Whether the caller already owns the open connection.</param>
    /// <returns>The actual analyzer connection owning only its command wrappers.</returns>
    private static EnvironmentCleanupFailureConnection CreateMetadataCleanupConnection(
        CatalogStatementTestConnection commands,
        ProbeCleanupFailureState state,
        bool initiallyOpen
    )
    {
        var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
        };

        if (initiallyOpen)
        {
            connection.Open();
        }

        return connection;
    }

    /// <summary>Selects actual index or nullable enum acquisition without an unrelated required-add probe.</summary>
    /// <param name="stage">The metadata family to reach through ordinary AnalyzeAsync.</param>
    /// <returns>One immutable operation whose real environment acquisition is required.</returns>
    private static SafeMigrationOperation[] CreateMetadataCleanupOperations(
        string stage
    ) =>
    [
        new(stage.StartsWith("index_", StringComparison.Ordinal)
                ? new EnsureIndexIntent(new ExpectedIndexDefinition(
                    "ix_value",
                    "items",
                    [new ExpectedIndexKeyDefinition("value")]))
                : new EnsureColumnIntent("items",
                    new ExpectedColumnDefinition(
                        "value",
                        typeof(string),
                        true,
                        "enum('a\\\\b','c')",
                        comment: "native\\comment")),
            SafeMigrationPolicy.ThrowIfDifferent),
    ];

    /// <summary>Supplies the exact transport and six/four-field native metadata layouts.</summary>
    /// <param name="stage">The native environment family under test.</param>
    /// <param name="laterFailure">An independently selected later catalog failure for positive controls.</param>
    /// <returns>The deterministic actual ADO statement producer.</returns>
    private static CatalogStatementTestConnection CreateMetadataCleanupCommands(
        string stage,
        Exception? laterFailure = null
    ) => new(native: false,
        (sql, _) =>
        {
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (sql == "SELECT @@max_allowed_packet, @@lower_case_table_names;")
            {
                table.Columns.Add("packet", typeof(long));
                table.Columns.Add("ownerMode", typeof(int));
                table.Rows.Add(16L * 1024 * 1024, 0);

                return table;
            }

            if (!sql.Contains(MetadataMarker(stage), StringComparison.Ordinal))
            {
                throw laterFailure ?? new InvalidOperationException("Unexpected query beyond metadata cleanup.");
            }

            table.Columns.Add("engine", typeof(string));
            if (stage.StartsWith("index_", StringComparison.Ordinal))
            {
                table.Columns.Add("rowFormat", typeof(string));
                table.Columns.Add("pageSize", typeof(int));
                table.Columns.Add("characterSet", typeof(string));
                table.Columns.Add("sqlMode", typeof(string));
                table.Columns.Add("version", typeof(string));
                table.Rows.Add("InnoDB", "Dynamic", 16384, "utf8mb4", "STRICT_TRANS_TABLES", "8.4.13");
            }
            else
            {
                table.Columns.Add("sqlMode", typeof(string));
                table.Columns.Add("explicitDefaults", typeof(int));
                table.Columns.Add("checks", typeof(int));
                table.Rows.Add("InnoDB", "STRICT_TRANS_TABLES", 1, 1);
            }

            return table;
        });
}
