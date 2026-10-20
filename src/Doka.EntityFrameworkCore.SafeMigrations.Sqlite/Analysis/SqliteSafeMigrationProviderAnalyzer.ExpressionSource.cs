namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Accepts only captured syntax whose identifier delimiters have native SQLite meaning.</summary>
    private static bool CapturedSourceDialectCompatible(string? source)
        => source is null || SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(source, "\"`[");

    /// <summary>Proves identifier roles against the complete declared table, not native DQS fallback.</summary>
    private static bool HasMissingDeclaredExpressionColumns(ExpectedTableDefinition definition)
    {
        HashSet<string>? references = null;
        foreach (var column in definition.Columns)
        {
            if (column.ComputedExpression is { } expression)
            {
                references ??= new HashSet<string>(s_identifierComparer);
                SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, references);
            }
        }

        foreach (var check in definition.CheckConstraints)
        {
            if (check.Expression is { } expression)
            {
                references ??= new HashSet<string>(s_identifierComparer);
                SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, references);
            }
        }

        if (references is null)
        {
            return false;
        }

        foreach (var column in definition.Columns)
        {
            _ = references.Remove(column.Name);
        }

        // WHY: SQLite can reinterpret a double-quoted unknown name as a string
        // when connection-owned DQS is enabled. A successful native DDL or row
        // probe therefore cannot certify the parsed source-column relationship.
        return references.Count != 0;
    }

    /// <summary>Requires live operands independently of native double-quoted string fallback.</summary>
    private static bool HasMissingLiveExpressionColumns(
        SafeMigrationSqlExpression? expression,
        SqliteTableSnapshot table
    )
    {
        if (expression is null)
        {
            return false;
        }

        var references = new HashSet<string>(s_identifierComparer);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, references);
        foreach (var reference in references)
        {
            if (!table.Columns.ContainsKey(reference))
            {
                return true;
            }
        }

        return false;
    }
}
