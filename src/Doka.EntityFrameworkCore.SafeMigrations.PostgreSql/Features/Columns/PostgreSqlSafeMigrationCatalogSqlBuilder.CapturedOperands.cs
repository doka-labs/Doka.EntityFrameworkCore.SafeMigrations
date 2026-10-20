namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Reuses finite operand proofs for captured integer arithmetic and Boolean comparisons.</summary>
    internal static bool RequiresCapturedOperandBinding(ExpectedColumnDefinition definition)
        => definition.CapturedComputedSql is not null
            && definition.ClrType is { } type && (type == typeof(int) || type == typeof(long) || type == typeof(bool))
            && definition.ComputedExpression is { } expression && IsFiniteCapturedOperand(expression);

    /// <summary>Excludes functions, casts and opaque grammar rather than inventing their native binding.</summary>
    private static bool IsFiniteCapturedOperand(SafeMigrationSqlExpression expression)
    {
        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (traversal.TryTake(out var node))
            {
                if (node is not (SafeMigrationSqlIdentifierExpression
                    or SafeMigrationSqlLiteralExpression { Value: null or bool or int }
                    or SafeMigrationSqlLiteralExpression { Value: long and >= int.MinValue and <= int.MaxValue }
                    or SafeMigrationSqlUnaryExpression or SafeMigrationSqlNullTestExpression
                    or SafeMigrationSqlBinaryExpression))
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

    /// <summary>Requires exact integer assignment width when reusing an accepted sparse expression binding.</summary>
    private long? CapturedOperandAssignmentType(ExpectedColumnDefinition definition)
        => _typeMappingSource.FindMapping(definition.ClrType, definition.StoreType)?.StoreTypeNameBase switch
        {
            "integer" or "int4" => 23,
            "bigint" or "int8" => 20,
            "boolean" or "bool" => 16,
            _ => null,
        };

    /// <summary>Does not confuse a numeric family with proof that the generated assignment cannot narrow it.</summary>
    private bool CapturedOperandInputMatchesAssignment(ExpectedColumnDefinition definition, long sourceType)
        => CapturedOperandAssignmentType(definition) is { } target && (target == 16 || target == sourceType);
}
