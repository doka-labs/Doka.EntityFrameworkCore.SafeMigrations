namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static partial class SqlServerColumnAttribution
{
    /// <summary>Includes matching replays and independent facet/data/dependency refusals.</summary>
    private static IEnumerable<Scenario> CreateScenarios()
    {
        foreach (var count in new[] { 1, 16, 64 })
        {
            yield return new Scenario("matching_alter_" + count, count, 128, "", false,
                SafeMigrationObservedState.Matching, false);
            yield return new Scenario("matching_ensure_" + count, count, 128, "", true,
                SafeMigrationObservedState.Matching, false);
        }

        yield return new Scenario("safe_repair_then_replay", 1, 64, "", false,
            SafeMigrationObservedState.Matching, false);
        yield return new Scenario("wrong_source_length", 1, 32, "", false,
            SafeMigrationObservedState.Different, true);
        yield return new Scenario("unexpected_default", 1, 64, " DEFAULT N'other'", false,
            SafeMigrationObservedState.Different, true);
        yield return new Scenario("unexpected_collation", 1, 64, " COLLATE Latin1_General_100_BIN2", false,
            SafeMigrationObservedState.Different, true);
        yield return new Scenario("nullability_data_blocked", 1, 64, "", false,
            SafeMigrationObservedState.DataBlocked, true);
        yield return new Scenario("caller_owned_index", 1, 64, "", false,
            SafeMigrationObservedState.Different, true);
    }

    /// <summary>Builds only predeclared synthetic identifiers and keeps all data/metadata deterministic.</summary>
    private static string BuildFixture(
        Scenario scenario,
        int rows
    )
    {
        var definitions = string.Join(", ", Enumerable.Range(0, scenario.Columns).Select(index =>
            $"[value_{index}] nvarchar({scenario.ActualLength})"
            + (scenario.Name == "unexpected_collation" ? scenario.Extra : "")
            + " NULL" + (scenario.Name == "unexpected_default" ? scenario.Extra : "")));

        var values = string.Join(", ", Enumerable.Range(0, scenario.Columns).Select(_ =>
            scenario.Name == "nullability_data_blocked" ? "NULL" : "N'short'"));

        var fixture = $"CREATE TABLE dbo.attribution_items ([Id] int NOT NULL, {definitions});\n"
            + ";WITH numbers AS (SELECT 1 AS value UNION ALL SELECT value + 1 FROM numbers "
            + $"WHERE value < {rows.ToString(CultureInfo.InvariantCulture)})\n"
            + $"INSERT dbo.attribution_items SELECT value, {values} FROM numbers OPTION (MAXRECURSION 0);\n";

        if (scenario.Name == "caller_owned_index")
        {
            fixture += "CREATE INDEX IX_attribution_value ON dbo.attribution_items(value_0);\n";
        }

        return fixture;
    }

    /// <summary>Uses the same old/target definition contract as the normal text-repair generation benchmark.</summary>
    private static List<SafeMigrationOperation> CreateOperations(
        Scenario scenario
    )
    {
        var operations = new List<SafeMigrationOperation>(scenario.Columns);
        for (var index = 0; index < scenario.Columns; index++)
        {
            var name = "value_" + index.ToString(CultureInfo.InvariantCulture);
            var target = new ExpectedColumnDefinition(name, typeof(string),
                scenario.Name != "nullability_data_blocked", "nvarchar(128)", maxLength: 128);

            var source = new ExpectedColumnDefinition(name, typeof(string), true, "nvarchar(64)", maxLength: 64);
            SafeMigrationIntent intent = scenario.Ensure
                ? new EnsureColumnIntent("attribution_items", target, "dbo")
                : new AlterColumnIntent("attribution_items", target, source, "dbo");

            operations.Add(new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe));
        }

        return operations;
    }

    /// <summary>Hashes all ordered fixture data and the relevant physical column/default/index catalog.</summary>
    private static string Snapshot(
        SqlConnection connection,
        Scenario scenario
    )
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', "
            + "(SELECT * FROM dbo.attribution_items ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES)), 2), "
            + "CONVERT(varchar(64), HASHBYTES('SHA2_256', "
            + "(SELECT c.name, c.column_id, c.system_type_id, c.max_length, c.precision, c.scale, "
            + "c.is_nullable, c.collation_name, c.is_identity, c.is_computed, dc.definition "
            + "FROM sys.columns c LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id "
            + "WHERE c.object_id = OBJECT_ID(N'dbo.attribution_items') ORDER BY c.column_id "
            + "FOR JSON PATH, INCLUDE_NULL_VALUES)), 2), "
            + "CONVERT(varchar(64), HASHBYTES('SHA2_256', "
            + "(SELECT i.name, i.type, i.is_unique, ic.column_id, ic.key_ordinal, ic.is_included_column "
            + "FROM sys.indexes i LEFT JOIN sys.index_columns ic "
            + "ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "WHERE i.object_id = OBJECT_ID(N'dbo.attribution_items') ORDER BY i.index_id, ic.index_column_id "
            + "FOR JSON PATH, INCLUDE_NULL_VALUES)), 2), COUNT_BIG(*) FROM dbo.attribution_items;";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"{scenario.Name}: fixture snapshot is missing.");
        }

        var snapshot = reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2)
            + ":" + reader.GetInt64(3).ToString(CultureInfo.InvariantCulture);

        return snapshot;
    }

    /// <summary>Defines an immutable fixture and its exact analyzer/runtime acceptance contract.</summary>
    private sealed record Scenario(
        string Name,
        int Columns,
        int ActualLength,
        string Extra,
        bool Ensure,
        SafeMigrationObservedState ExpectedState,
        bool RejectRuntime
    );
}
