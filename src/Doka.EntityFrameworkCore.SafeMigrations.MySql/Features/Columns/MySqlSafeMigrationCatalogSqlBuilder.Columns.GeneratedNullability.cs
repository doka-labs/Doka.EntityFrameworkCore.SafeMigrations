namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Identifies the MariaDB-only logical contract that cannot use a physical NOT NULL flag.</summary>
    private static bool RequiresMariaDbGeneratedNonNullProof(
        ExpectedColumnDefinition definition,
        bool isMariaDb
    ) => isMariaDb && definition is { IsNullable: false, ComputedExpression: not null };

    /// <summary>Retains physical nullability comparison unless exact generated semantics prove more.</summary>
    private string BuildColumnNullabilityMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb
    )
    {
        if (!RequiresMariaDbGeneratedNonNullProof(definition, isMariaDb))
        {
            return definition.IsNullable ? "c.IS_NULLABLE = 'YES'" : "c.IS_NULLABLE = 'NO'";
        }

        return "(c.IS_NULLABLE = 'NO' OR (c.IS_NULLABLE = 'YES' AND ("
            + BuildGeneratedNonNullDependenciesMatch(table, definition)
            + ")))";
    }

    /// <summary>Revalidates ordinary required signed integer sources in the executing native catalog.</summary>
    private string BuildGeneratedNonNullDependenciesMatch(
        string table,
        ExpectedColumnDefinition definition
    )
    {
        if (!MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources))
        {
            return "FALSE";
        }

        if (sources.Count == 0)
        {
            return "TRUE";
        }

        var builder = new StringBuilder();
        foreach (var source in sources)
        {
            if (builder.Length > 0)
            {
                builder.Append(" AND ");
            }

            // WHY: Row contents do not certify future generated results.
            // Required ordinary integer metadata excludes nullable, derived,
            // floating/decimal and unsigned coercion domains without scans.
            builder
                .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS generated_source ")
                .Append("WHERE generated_source.TABLE_SCHEMA = DATABASE() AND generated_source.TABLE_NAME = ")
                .Append(Literal(table))
                .Append(" AND generated_source.COLUMN_NAME = ")
                .Append(Literal(source))
                .Append(" AND ")
                .Append(BuildRequiredColumnAddOwnerMatches("generated_source.TABLE_SCHEMA", "DATABASE()"))
                .Append(" AND ")
                .Append(BuildRequiredColumnAddOwnerMatches("generated_source.TABLE_NAME", Literal(table)))
                .Append(" AND CAST(LOWER(generated_source.COLUMN_NAME) AS BINARY) = CAST(LOWER(")
                .Append(Literal(source))
                .Append(") AS BINARY)")
                .Append(" AND generated_source.IS_NULLABLE = 'NO' ")
                .Append("AND LOWER(generated_source.DATA_TYPE) IN ")
                .Append("('tinyint', 'smallint', 'mediumint', 'int', 'bigint') ")
                .Append("AND LOCATE('unsigned', LOWER(generated_source.COLUMN_TYPE)) = 0 ")
                .Append("AND COALESCE(generated_source.GENERATION_EXPRESSION, '') = '')");
        }

        return builder.ToString();
    }

    /// <summary>Blocks required to be computed ADD when physical dependencies cannot certify NULL absence.</summary>
    private MySqlSafeMigrationRuntimePlan QualifyMariaDbGeneratedColumnAdd(
        EnsureColumnIntent intent,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var qualification = BuildGeneratedNonNullDependenciesMatch(intent.Table, intent.Definition);
        var exists = ColumnExists(intent.Table, intent.Definition.Name);

        return plan with
        {
            StateExpression = $"CASE WHEN {BaseTableExists(intent.Table)} AND NOT ({exists}) "
            + $"AND NOT ({qualification}) THEN 'unsupported' ELSE ({plan.StateExpression}) END",
            UnsupportedCode = "generated_column_nullability",
        };
    }

    /// <summary>Requires all referenced members, including nullable CASE conditions, before computed ADD.</summary>
    private MySqlSafeMigrationRuntimePlan QualifyGeneratedColumnDependencies(
        EnsureColumnIntent intent,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var sources = MySqlGeneratedColumnDependencies.Collect(intent.Definition);
        if (sources.Count == 0)
        {
            return plan;
        }

        var predicates = new string[sources.Count];
        // WHY: Store dependency predicates in their reserved slots without changing source-member order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < sources.Count; index++)
        {
            predicates[index] = StringComparer.OrdinalIgnoreCase.Equals(sources[index], intent.Definition.Name)
                ? "FALSE"
                : BuildRequiredColumnAddColumnExists(
                    intent.Table,
                    sources[index],
                    $"CAST(LOWER(c.COLUMN_NAME) AS BINARY) = CAST(LOWER({Literal(sources[index])}) AS BINARY)");
        }

        var missing = $"{BaseTableExists(intent.Table)} AND NOT ({ColumnExists(intent.Table, intent.Definition.Name)}) "
            + $"AND NOT ({string.Join(" AND ", predicates)})";

        return plan with
        {
            StateExpression = $"CASE WHEN {missing} THEN 'prerequisite_missing' ELSE ({plan.StateExpression}) END",
            ClassificationCodeExpression = $"CASE WHEN {missing} THEN 'generated_column_dependency_missing' "
            + $"ELSE ({plan.ClassificationCodeExpression ?? "NULL"}) END",
        };
    }
}
