namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Qualifies expected physical table keys before inventory scopes consume native catalog names.</summary>
    /// <param name="connection">The open connection whose selected database was already verified.</param>
    /// <param name="expected">The authored expected inventory, retaining its raw definitions.</param>
    /// <param name="maximumPayloadBytes">The current qualified aggregate transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction, transport and reading.</param>
    /// <returns>The expected inventory keyed by live physical identity, or its authored key when missing.</returns>
    private async Task<Dictionary<string, SafeMigrationExpectedTableInventory>>
        CanonicalizeUnexpectedInventoryOwnersAsync(
            DbConnection connection,
            Dictionary<string, SafeMigrationExpectedTableInventory> expected,
            int maximumPayloadBytes,
            int? commandTimeout,
            CancellationToken cancellationToken
        )
    {
        if (expected.Count == 0)
        {
            return expected;
        }

        var authored = expected.Keys.Select(static (owner, ordinal) => (Owner: owner, Ordinal: ordinal)).ToArray();
        var bindings = await ReadNativeInventoryOwnersAsync(
            connection, authored, maximumPayloadBytes, commandTimeout, cancellationToken);

        return BindUnexpectedInventoryOwners(expected, bindings);
    }

    /// <summary>Rekeys only inventory lookup identities while preserving the authored object contracts.</summary>
    /// <param name="expected">The authored expected inventory with exact ordinal keys.</param>
    /// <param name="bindings">The complete invocation-local server bindings, null for a missing owner.</param>
    /// <returns>The inventory whose keys match the physical names the child reader receives.</returns>
    /// <exception cref="InvalidOperationException">
    /// A binding is missing or distinct definitions share one owner.
    /// </exception>
    internal static Dictionary<string, SafeMigrationExpectedTableInventory> BindUnexpectedInventoryOwners(
        IReadOnlyDictionary<string, SafeMigrationExpectedTableInventory> expected,
        IReadOnlyDictionary<string, string?> bindings
    )
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(bindings);

        var result = new Dictionary<string, SafeMigrationExpectedTableInventory>(
            expected.Count, StringComparer.Ordinal);

        foreach (var (authored, definition) in expected)
        {
            if (!bindings.TryGetValue(authored, out var physical))
            {
                throw new InvalidOperationException("The MySQL inventory did not resolve every expected owner.");
            }

            // WHY: A live native name qualifies only lookup identity. Keeping
            // the same definition preserves raw guards/fingerprints; a missing
            // table keeps its authored key and does not invent a physical fact.
            if (!result.TryAdd(physical ?? authored, definition))
            {
                throw new InvalidOperationException(
                    "The migration contains multiple expected table definitions for one MySQL database object.");
            }
        }

        return result;
    }
}
