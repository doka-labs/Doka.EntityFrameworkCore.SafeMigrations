namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Resolves only explicit collations needed for ordered type-transition backfill proofs.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="operations">The immutable operation stream.</param>
    /// <param name="maximumPayloadBytes">The packet-qualified transport bound.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Resolved catalog facts shared by all table environments.</returns>
    internal static async Task<IReadOnlyDictionary<string, string>> ReadProjectedAlterCollationCharacterSetsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var names = operations.Select(static operation => operation.Intent)
            .OfType<AlterColumnIntent>()
            .Where(static intent => intent.OldDefinition is { IsNullable: true, Collation.Schema: null }
                && intent.OldDefinition.Collation is not null
                && !intent.Definition.IsNullable
                && (!StringComparer.OrdinalIgnoreCase.Equals(
                        intent.OldDefinition.StoreType,
                        intent.Definition.StoreType)
                    || intent.OldDefinition.MaxLength != intent.Definition.MaxLength)
                && SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(intent.Definition.DefaultValue))
            .Select(static intent => intent.OldDefinition!.Collation!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names.Length == 0)
        {
            return result;
        }

        // WHY: Collation names are provider identities, not a guaranteed charset
        // parser. One bounded catalog lookup resolves all required explicit
        // identities; ordinary index/column streams perform no new probe.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            (names.Length + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues,
            maximumPayloadBytes,
            commandTimeout,
            (command, slot) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, names.Length - offset);
                var parameters = new string[count];
                var valueBytes = 0;
                for (var index = 0; index < count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@alter_collation_{index.ToString(CultureInfo.InvariantCulture)}";
                    parameter.Value = names[offset + index];
                    _ = command.Parameters.Add(parameter);
                    parameters[index] = parameter.ParameterName;
                    valueBytes += Encoding.UTF8.GetByteCount(names[offset + index]) + 32;
                }

                command.CommandText = "SELECT COLLATION_NAME, CHARACTER_SET_NAME FROM INFORMATION_SCHEMA.COLLATIONS "
                    + $"WHERE COLLATION_NAME IN ({string.Join(", ", parameters)});";

                return new SafeMigrationCatalogProbeStatement(1, valueBytes, offset);
            },
            async (reader, slot, _, token) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, names.Length - offset);
                while (await reader.ReadAsync(token))
                {
                    var name = reader.GetString(0);
                    if (Array.BinarySearch(names, offset, count, name, StringComparer.OrdinalIgnoreCase) < 0
                        || !result.TryAdd(name, reader.GetString(1)))
                    {
                        throw new InvalidOperationException("MySQL returned invalid alter collation ownership.");
                    }
                }
            },
            cancellationToken);

        return result;
    }
}
