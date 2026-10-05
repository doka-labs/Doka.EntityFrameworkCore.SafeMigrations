namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Reads bounded target occupancy without constructing full authored table matchers.</summary>
    internal static async Task<bool[]> ReadAbsentTableTargetsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int start,
        int count,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, operations.Count - count);
        cancellationToken.ThrowIfCancellationRequested();

        var absent = new bool[count];
        var ordinals = SafeMigrationCatalogWorkOrder.Create(count,
            index => operations[start + index].Intent is EnsureTableIntent, cancellationToken);

        if (ordinals.Length == 0)
        {
            return absent;
        }

        using var activity = SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", ordinals.Length);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout, transaction);
        var command = batch.CreateCommand();
        // WHY: SqlClient's native SqlBatchCommand does not implement CreateParameter.
        // Borrow the sequential command or use one non-executing provider factory per probe;
        // parameters belong to the catalog command, never to the factory's collection.
        using var parameterFactory = command.SequentialCommand is null ? connection.CreateCommand() : null;
        var parameterHost = command.SequentialCommand ?? parameterFactory!;
        var values = new string[ordinals.Length];
        for (var index = 0; index < ordinals.Length; index++)
        {
            var definition = ((EnsureTableIntent)operations[start + ordinals[index]].Intent).Definition;
            var schema = "@schema" + index.ToString(CultureInfo.InvariantCulture);
            var table = "@table" + index.ToString(CultureInfo.InvariantCulture);
            AddTargetParameter(command, parameterHost, schema, definition.Schema ?? "dbo");
            AddTargetParameter(command, parameterHost, table, definition.Table);
            values[index] = "(" + ordinals[index].ToString(CultureInfo.InvariantCulture)
                + ", " + schema + ", " + table + ")";
        }

        // WHY: sys.objects, rather than only sys.tables, preserves occupied view/sequence/synonym
        // boundaries. Server-side catalog collation owns name comparison; no CLR case folding is inferred.
        command.CommandText = "SELECT requested.ordinal, CASE WHEN target_schema.schema_id IS NOT NULL "
            + "AND NOT EXISTS (SELECT 1 FROM sys.objects occupied "
            + "WHERE occupied.schema_id = target_schema.schema_id AND occupied.name = requested.table_name) "
            + "THEN 1 ELSE 0 END FROM (VALUES " + string.Join(", ", values)
            + ") requested(ordinal, schema_name, table_name) "
            + "LEFT JOIN sys.schemas target_schema ON target_schema.name = requested.schema_name ORDER BY requested.ordinal;";

        activity?.SetTag("safe_migrations.catalog.statement_count", 1);
        var consumed = 0;
        await batch.ForEachResultSetAsync(async (reader, token) =>
        {
            while (await reader.ReadAsync(token))
            {
                var ordinal = reader.GetInt32(0);
                SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(ordinal, ordinals, ref consumed);
                absent[ordinal] = reader.GetInt32(1) == 1;
            }
        }, cancellationToken);

        SafeMigrationCatalogWorkOrder.ValidateCompletion(consumed, ordinals);

        return absent;
    }

    private static void AddTargetParameter(
        SafeMigrationCatalogCommand command,
        DbCommand parameterHost,
        string name,
        string value
    )
    {
        var parameter = parameterHost.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Size = 128;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
