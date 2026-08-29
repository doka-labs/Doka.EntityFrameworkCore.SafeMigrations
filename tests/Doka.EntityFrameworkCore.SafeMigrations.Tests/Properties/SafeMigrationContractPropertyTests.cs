namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Exercises provider-neutral invariants over generated contract inputs so
/// shrinking produces a minimal reproduction when an invariant regresses.
/// </summary>
public sealed class SafeMigrationContractPropertyTests
{
    private static readonly SafeMigrationSqlBinaryOperator[] s_binaryOperators =
        Enum.GetValues<SafeMigrationSqlBinaryOperator>();

    [Property(MaxTest = 1000)]
    public bool StructuredExpressions_PreserveEquivalenceAndCanonicalForm(
        string? rawIdentifier,
        int literal,
        byte operatorSelector,
        byte shapeSelector,
        bool negated
    )
    {
        var identifier = Identifier(rawIdentifier, "value");
        var binaryOperator = s_binaryOperators[operatorSelector % s_binaryOperators.Length];
        var first = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);
        var second = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);
        var third = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);

        using var firstWriter = new CanonicalHashWriter();
        using var secondWriter = new CanonicalHashWriter();

        SafeMigrationSqlExpressionContract.Write(firstWriter, first);
        SafeMigrationSqlExpressionContract.Write(secondWriter, second);

        return SafeMigrationSqlExpressionInspector.IsStructurallyComparable(first)
            && SafeMigrationSqlExpressionContract.Equivalent(first, first)
            && SafeMigrationSqlExpressionContract.Equivalent(first, second)
            && SafeMigrationSqlExpressionContract.Equivalent(second, first)
            && SafeMigrationSqlExpressionContract.Equivalent(second, third)
            && SafeMigrationSqlExpressionContract.Equivalent(first, third)
            && StringComparer.Ordinal.Equals(firstWriter.GetHash(), secondWriter.GetHash());
    }

    [Property(MaxTest = 1000)]
    public bool RenameIdentifier_RewritesOnlyStructuredIdentifierRoles(
        string? rawIdentifier,
        int literal,
        bool negated
    )
    {
        var source = Identifier(rawIdentifier, "source");
        var target = $"{source}_renamed";
        var original = RenameExpression(source, source, literal, negated);
        var expected = RenameExpression(target, source, literal, negated);

        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(original, source, target);
        var renamedAgain = SafeMigrationSqlExpressionInspector.RenameIdentifier(renamed, source, target);

        return SafeMigrationSqlExpressionContract.Equivalent(expected, renamed)
            && SafeMigrationSqlExpressionContract.Equivalent(renamed, renamedAgain);
    }

    [Property(MaxTest = 1000)]
    public bool ContractFingerprint_IsDeterministicOrderedAndCanonical(
        string? rawTable,
        string? rawSchema
    )
    {
        var table = Identifier(rawTable, "table");
        var schema = Identifier(rawSchema, "schema");
        var renamedTable = $"{table}_renamed";
        var drop = Operation(new DropTableIntent(table, schema));
        var rename = Operation(new RenameTableIntent(table, renamedTable, schema));
        MigrationOperation[] forward = [drop, rename];
        MigrationOperation[] reverse = [rename, drop];

        var first = SafeMigrationContractFingerprint.Create(forward);
        var second = SafeMigrationContractFingerprint.Create(forward);
        var reordered = SafeMigrationContractFingerprint.Create(reverse);

        return IsCanonicalFingerprint(first)
            && StringComparer.Ordinal.Equals(first, second)
            && !StringComparer.Ordinal.Equals(first, reordered);
    }

    [Property(MaxTest = 1000)]
    public bool ContractFingerprintValidation_AcceptsExactlyCanonicalSha256(
        byte[]? rawDigest,
        string? candidate
    )
    {
        var canonical = Convert.ToHexString(SHA256.HashData(rawDigest ?? [])).ToLowerInvariant();
        var expectedToPass = candidate is not null && IsCanonicalFingerprint(candidate);

        SafeMigrationContractFingerprint.Validate(canonical, nameof(canonical));

        try
        {
            SafeMigrationContractFingerprint.Validate(candidate!, nameof(candidate));
            return expectedToPass;
        }
        catch (ArgumentException)
        {
            return !expectedToPass;
        }
    }

    [Property(MaxTest = 2000)]
    public bool SqlExpressionParser_ArbitraryInputPreservesTheTryParseContract(
        string? input
    )
    {
        // Null is outside TryParse's SQL-text contract. Map FsCheck's null case
        // to the valid empty-input rejection path so every generated case still
        // verifies the parser's success-or-failure output invariant.
        var parsed = SafeMigrationSqlExpressionParser.TryParse(
            input ?? string.Empty,
            out var expression,
            out var failureCode);

        return parsed
            ? expression is not null
                && failureCode.Length == 0
                && SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
            : expression is null && failureCode.Length > 0;
    }

    private static string Identifier(
        string? value,
        string prefix
    ) => $"{prefix}_{value ?? "null"}";

    private static bool IsCanonicalFingerprint(
        string value
    ) => value.Length == 64
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationSqlExpression RenameExpression(
        string identifier,
        string literal,
        int value,
        bool negated
    )
    {
        var qualifiedIdentifier = SafeMigrationSql.Identifier("app", identifier);
        var function = SafeMigrationSql.Function(
            "coalesce",
            qualifiedIdentifier,
            SafeMigrationSql.Literal(literal));

        return SafeMigrationSql.In(
            function,
            [SafeMigrationSql.Identifier(identifier), SafeMigrationSql.Literal(value)],
            negated);
    }

    private static SafeMigrationSqlExpression StructuredExpression(
        string identifier,
        int literal,
        SafeMigrationSqlBinaryOperator binaryOperator,
        byte shapeSelector,
        bool negated
    )
    {
        var value = SafeMigrationSql.Identifier("app", identifier);
        var lower = SafeMigrationSql.Literal((long)literal - 1L);
        var upper = SafeMigrationSql.Literal((long)literal + 1L);

        return (shapeSelector % 6) switch
        {
            0 => SafeMigrationSql.Binary(value, binaryOperator, SafeMigrationSql.Literal(literal)),
            1 => SafeMigrationSql.Unary(
                negated ? SafeMigrationSqlUnaryOperator.Not : SafeMigrationSqlUnaryOperator.Negate,
                value),
            2 => SafeMigrationSql.Between(value, lower, upper, negated),
            3 => SafeMigrationSql.In(value, [lower, upper], negated),
            4 => SafeMigrationSql.Function("coalesce", value, SafeMigrationSql.Literal(literal)),
            _ => SafeMigrationSql.Collate(SafeMigrationSql.Cast(value, "text"), "canonical", "catalog"),
        };
    }
}
