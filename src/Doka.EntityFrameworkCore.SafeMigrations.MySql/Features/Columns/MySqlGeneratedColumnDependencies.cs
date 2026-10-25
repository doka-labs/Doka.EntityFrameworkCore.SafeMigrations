namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Separates generated-expression binding from its narrower non-null value proof.</summary>
internal static class MySqlGeneratedColumnDependencies
{
    /// <summary>Rejects qualified bindings that a local generated column cannot certify.</summary>
    internal static bool ReferencesOnlyLocalColumns(
        SafeMigrationSqlExpression expression
    )
    {
        var traversal = new MySqlExpressionTraversal(expression);
        try
        {
            while (traversal.TryMoveNext(out var current))
            {
                if (current is SafeMigrationSqlIdentifierExpression { Parts.Count: not 1 }
                    or SafeMigrationSqlProviderFragmentExpression
                    or SafeMigrationSqlOpaqueExpression)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Includes every condition, result and ELSE source without requiring condition nullability.</summary>
    internal static IReadOnlyList<string> Collect(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.ComputedExpression is null)
        {
            return Array.Empty<string>();
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(definition.ComputedExpression, names);

        return names.Count == 0 ? Array.Empty<string>() : names.ToArray();
    }

    /// <summary>Rejects missing and self-referential sources before a fresh native CREATE is attempted.</summary>
    internal static bool DeclaredSourcesExist(
        ExpectedColumnDefinition definition,
        ExpectedTableDefinition table
    )
    {
        foreach (var source in Collect(definition))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(source, definition.Name))
            {
                return false;
            }

            var count = 0;
            var columnCount = table.Columns.Count;
            // WHY: Check every immutable column for ambiguity without an inner-loop enumerator.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var candidate = table.Columns[columnIndex];
                if (StringComparer.OrdinalIgnoreCase.Equals(source, candidate.Name))
                {
                    count++;
                }
            }

            if (count != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads native identity semantics from accepted metadata, not an integral type guess.</summary>
    internal static bool IsIdentitySource(
        ExpectedColumnDefinition definition
    ) => MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
        && metadata.ValueGenerationStrategy == MySqlValueGenerationStrategy.AutoIncrement;

    /// <summary>Rejects a native-forbidden source in an otherwise complete fresh table declaration.</summary>
    internal static bool HasDeclaredIdentitySource(
        ExpectedColumnDefinition definition,
        ExpectedTableDefinition table
    )
    {
        foreach (var source in Collect(definition))
        {
            foreach (var candidate in table.Columns)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(source, candidate.Name)
                    && IsIdentitySource(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
