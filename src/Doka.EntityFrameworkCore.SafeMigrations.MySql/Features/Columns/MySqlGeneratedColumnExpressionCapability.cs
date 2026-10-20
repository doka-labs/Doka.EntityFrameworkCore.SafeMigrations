namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Separates deterministic generated-expression capability from generic SQL renderability.</summary>
internal static class MySqlGeneratedColumnExpressionCapability
{
    /// <summary>Returns a finite capability refusal without changing default or ordinary expressions.</summary>
    internal static string? GetUnsupportedFeature(
        SafeMigrationSqlExpression expression
    )
    {
        var traversal = new MySqlExpressionTraversal(expression);
        try
        {
            while (traversal.TryMoveNext(out var current))
            {
                var failure = current switch
                {
                    SafeMigrationSqlCurrentValueExpression => "generated_column_nondeterministic_expression",
                    SafeMigrationSqlFunctionExpression value => FunctionFailure(value),
                    _ => null,
                };

                if (failure is not null)
                {
                    return failure;
                }
            }

            return null;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Allows only measured common deterministic built-ins with their native arity.</summary>
    private static string? FunctionFailure(
        SafeMigrationSqlFunctionExpression expression
    )
    {
        var name = expression.Name.AsSpan();
        if (name.Equals("NOW", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CURRENT_TIME", StringComparison.OrdinalIgnoreCase)
            || name.Equals("UTC_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LOCALTIME", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LOCALTIMESTAMP", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: MySQL forbids clocks in generated expressions; MariaDB
            // permits some virtual forms but not persisted/indexed variants.
            // The product's bounded contract is deterministic on both engines,
            // not a claim that every rejected MariaDB expression is invalid SQL.
            return "generated_column_nondeterministic_expression";
        }

        var supported =
            ((name.Equals("ABS", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LOWER", StringComparison.OrdinalIgnoreCase))
                && expression.Arguments.Count == 1)
            || (name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase) && expression.Arguments.Count > 0);

        if (!supported)
        {
            return "generated_column_function_unverified";
        }

        return null;
    }
}
