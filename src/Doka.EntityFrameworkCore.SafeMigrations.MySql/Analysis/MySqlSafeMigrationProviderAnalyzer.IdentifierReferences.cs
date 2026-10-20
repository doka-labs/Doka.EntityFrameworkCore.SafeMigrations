namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Acquires native source-head syntax once per distinct spelling in a bounded analysis window.</summary>
    private static async Task FindIdentifierReferenceProofsAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, (MySqlSafeMigrationIdentifierReferenceProof Proof, int OperationOrdinal)>? unique = null;
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            if (states[ordinal] is { State: not SafeMigrationObservedState.PrerequisiteMissing }
                || plans[ordinal].IsStaticallyUnsupported)
            {
                continue;
            }

            foreach (var proof in plans[ordinal].IdentifierReferenceProofs)
            {
                (unique ??=
                    new Dictionary<string, (MySqlSafeMigrationIdentifierReferenceProof Proof, int OperationOrdinal)>(
                        StringComparer.Ordinal)).TryAdd(proof.RootIdentifier, (proof, ordinal));
            }
        }

        if (unique is null)
        {
            return;
        }

        var pending = unique.Values.ToArray();
        var facts = new Dictionary<string, bool>(pending.Length, StringComparer.Ordinal);
        var offset = 0;
        while (offset < pending.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = new List<MySqlSafeMigrationIdentifierReferenceProof>();
            var setup = new StringBuilder();
            var cleanup = new StringBuilder();
            var selections = new List<string>();
            var payloadBytes = 0;
            while (offset < pending.Length
                   && selected.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            {
                var proof = pending[offset].Proof;

                // WHY: These synthetic names need no application owner. Core
                // can establish initially missing tables later in the stream;
                // their source grammar must already be independently proven.
                var evaluation = MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(proof, "TRUE", "TRUE");
                var release = MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof);
                var selection = $"SELECT {offset.ToString(CultureInfo.InvariantCulture)}, {proof.ResultVariable}";
                var nextBytes = checked(payloadBytes
                    + Encoding.UTF8.GetByteCount(evaluation)
                    + Encoding.UTF8.GetByteCount(release)
                    + Encoding.UTF8.GetByteCount(selection)
                    + SafeMigrationCatalogQueryLimits.Separator.Length
                    + 64);

                if (SafeMigrationCatalogQueryLimits.Exceeded(0, nextBytes, maximumPayloadBytes))
                {
                    if (selected.Count == 0)
                    {
                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                            operationOffset + pending[offset].OperationOrdinal,
                            0,
                            nextBytes);
                    }

                    break;
                }

                setup.Append(evaluation);
                cleanup.Append(release);
                selections.Add(selection);
                selected.Add(proof);
                payloadBytes = nextBytes;
                offset++;
            }

            await using var command = connection.CreateCommand();
            if (commandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            command.CommandText = setup
                .Append(string.Join(SafeMigrationCatalogQueryLimits.Separator, selections))
                .Append(';')
                .Append(cleanup)
                .ToString();

            var completed = false;
            Exception? failure = null;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (reader.FieldCount == 0
                       && await reader.NextResultAsync(cancellationToken)) { }

                if (reader.FieldCount != 2)
                {
                    throw new InvalidOperationException(
                        "The MariaDB identifier proof returned an invalid result shape.");
                }

                var expected = offset - selected.Count;
                foreach (var proof in selected)
                {
                    if (!await reader.ReadAsync(cancellationToken)
                        || reader.GetInt32(0) != expected++
                        || reader.IsDBNull(1))
                    {
                        throw new InvalidOperationException("The MariaDB identifier proof returned an invalid row.");
                    }

                    facts.Add(proof.RootIdentifier, reader.GetBoolean(1));
                }

                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The MariaDB identifier proof returned extra rows.");
                }

                while (await reader.NextResultAsync(cancellationToken))
                {
                    if (reader.FieldCount > 0)
                    {
                        throw new InvalidOperationException("The MariaDB identifier proof returned extra result sets.");
                    }
                }

                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;

                throw;
            }
            finally
            {
                // WHY: Each acquired PREPARE and value belongs to a unique
                // invocation scope. An independently token cleanup cannot
                // overwrite caller statements or let cancellation cache TRUE.
                if (!completed
                    && connection.State == System.Data.ConnectionState.Open)
                {
                    try
                    {
                        await using var release = connection.CreateCommand();
                        if (commandTimeout is { } cleanupTimeout)
                        {
                            release.CommandTimeout = cleanupTimeout;
                        }

                        release.CommandText = cleanup.ToString();
                        await release.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (Exception exception) when (failure is not null)
                    {
                        failure.Data["SafeMigrations.IdentifierReferenceProofCleanupException"] = exception;
                    }
                }
            }
        }

        for (var planIndex = 0; planIndex < plans.Length; planIndex++)
        {
            var plan = plans[planIndex];
            if (states[planIndex] is { State: not SafeMigrationObservedState.PrerequisiteMissing }
                || plan.IsStaticallyUnsupported
                || plan.IdentifierReferenceProofs.Count == 0)
            {
                continue;
            }

            var bound = new MySqlSafeMigrationIdentifierReferenceProof[plan.IdentifierReferenceProofs.Count];
            var valid = true;
            for (var index = 0; index < bound.Length; index++)
            {
                var proof = plan.IdentifierReferenceProofs[index];
                var proven = facts[proof.RootIdentifier];
                bound[index] = proof with { Binding = proven ? "TRUE" : "FALSE" };
                valid &= proven;
            }

            plans[planIndex] = plan with { IdentifierReferenceProofs = bound };
            if (!valid)
            {
                states[planIndex] = new MySqlShortCircuitState(
                    SafeMigrationObservedState.Unsupported,
                    "captured_expression_identifier_keyword_unverified");
            }
        }
    }
}
