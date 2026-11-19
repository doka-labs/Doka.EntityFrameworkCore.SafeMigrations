namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Exercises provider-neutral invariants over generated contract inputs so
/// shrinking produces a minimal reproduction when an invariant regresses.
/// </summary>
public sealed class SafeMigrationContractPropertyTests
{
    private static readonly SafeMigrationSqlBinaryOperator[] s_binaryOperators =
        Enum.GetValues<SafeMigrationSqlBinaryOperator>();

    /// <summary>Generated expression shapes preserve equivalence laws and deterministic canonical hashing.</summary>
    /// <param name="rawIdentifier">The generated identifier suffix, including the null fallback case.</param>
    /// <param name="literal">The generated integer used by the structured expression leaves.</param>
    /// <param name="operatorSelector">
    /// Selects a declared binary operator without narrowing the generated domain.
    /// </param>
    /// <param name="shapeSelector">Selects one of the six supported structured expression shapes.</param>
    /// <param name="negated">Controls negation where the selected expression shape supports it.</param>
    /// <returns>Whether comparability, equivalence laws and independently written canonical hashes agree.</returns>
    [Property(MaxTest = 1000)]
    public bool StructuredExpressions_PreserveEquivalenceAndCanonicalForm(
        string? rawIdentifier,
        int literal,
        byte operatorSelector,
        byte shapeSelector,
        bool negated
    )
    {
        // Arrange
        var identifier = Identifier(rawIdentifier, "value");
        var binaryOperator = s_binaryOperators[operatorSelector % s_binaryOperators.Length];
        var first = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);
        var second = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);
        var third = StructuredExpression(identifier, literal, binaryOperator, shapeSelector, negated);

        using var firstWriter = new CanonicalHashWriter();
        using var secondWriter = new CanonicalHashWriter();

        // Act
        SafeMigrationSqlExpressionContract.Write(firstWriter, first);
        SafeMigrationSqlExpressionContract.Write(secondWriter, second);

        var satisfiesContract = SafeMigrationSqlExpressionInspector.IsStructurallyComparable(first)
            && SafeMigrationSqlExpressionContract.Equivalent(first, first)
            && SafeMigrationSqlExpressionContract.Equivalent(first, second)
            && SafeMigrationSqlExpressionContract.Equivalent(second, first)
            && SafeMigrationSqlExpressionContract.Equivalent(second, third)
            && SafeMigrationSqlExpressionContract.Equivalent(first, third)
            && StringComparer.Ordinal.Equals(firstWriter.GetHash(), secondWriter.GetHash());

        // Assert
        return satisfiesContract;
    }

    /// <summary>Renaming identifiers preserves literal roles and is idempotent for the original source name.</summary>
    /// <param name="rawIdentifier">The generated source-name suffix, including the null fallback case.</param>
    /// <param name="literal">The generated integer retained as a literal leaf.</param>
    /// <param name="negated">Whether the generated IN expression is negated.</param>
    /// <returns>Whether the rewrite matches the independently constructed target and a repeated rewrite.</returns>
    [Property(MaxTest = 1000)]
    public bool RenameIdentifier_RewritesOnlyStructuredIdentifierRoles(
        string? rawIdentifier,
        int literal,
        bool negated
    )
    {
        // Arrange
        var source = Identifier(rawIdentifier, "source");
        var target = $"{source}_renamed";
        var original = RenameExpression(source, source, literal, negated);
        var expected = RenameExpression(target, source, literal, negated);

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(original, source, target);
        var renamedAgain = SafeMigrationSqlExpressionInspector.RenameIdentifier(renamed, source, target);

        var satisfiesContract = SafeMigrationSqlExpressionContract.Equivalent(expected, renamed)
            && SafeMigrationSqlExpressionContract.Equivalent(renamed, renamedAgain);

        // Assert
        return satisfiesContract;
    }

    /// <summary>Ordered operation fingerprints remain deterministic, order-sensitive and canonical.</summary>
    /// <param name="rawTable">The generated table-name suffix before adding its nonempty fixture prefix.</param>
    /// <param name="rawSchema">The generated schema-name suffix before adding its nonempty fixture prefix.</param>
    /// <returns>Whether repeated inputs match, reversed operations differ and the hash is canonical.</returns>
    [Property(MaxTest = 1000)]
    public bool ContractFingerprint_IsDeterministicOrderedAndCanonical(
        string? rawTable,
        string? rawSchema
    )
    {
        // Arrange
        var table = Identifier(rawTable, "table");
        var schema = Identifier(rawSchema, "schema");
        var renamedTable = $"{table}_renamed";
        var drop = Operation(new DropTableIntent(table, schema));
        var rename = Operation(new RenameTableIntent(table, renamedTable, schema));
        MigrationOperation[] forward = [drop, rename];
        MigrationOperation[] reverse = [rename, drop];

        // Act
        var first = SafeMigrationContractFingerprint.Create(forward);
        var second = SafeMigrationContractFingerprint.Create(forward);
        var reordered = SafeMigrationContractFingerprint.Create(reverse);

        var satisfiesContract = IsCanonicalFingerprint(first)
            && StringComparer.Ordinal.Equals(first, second)
            && !StringComparer.Ordinal.Equals(first, reordered);

        // Assert
        return satisfiesContract;
    }

    /// <summary>Fingerprint validation accepts exactly the independently recognized canonical SHA-256 domain.</summary>
    /// <param name="rawDigest">Generated hash input; null retains the existing empty-input hash case.</param>
    /// <param name="candidate">The arbitrary candidate, including null and noncanonical text.</param>
    /// <returns>
    /// Whether validation accepts a known canonical digest and matches the independent candidate predicate.
    /// </returns>
    [Property(MaxTest = 1000)]
    public bool ContractFingerprintValidation_AcceptsExactlyCanonicalSha256(
        byte[]? rawDigest,
        string? candidate
    )
    {
        // Arrange
        var canonical = Convert
            .ToHexString(SHA256.HashData(rawDigest ?? []))
            .ToLowerInvariant();

        var expectedToPass = candidate is not null && IsCanonicalFingerprint(candidate);
        var accepted = true;

        // Act
        SafeMigrationContractFingerprint.Validate(canonical, nameof(canonical));

        try
        {
            SafeMigrationContractFingerprint.Validate(candidate!, nameof(candidate));
        }
        catch (ArgumentException)
        {
            accepted = false;
        }

        // Assert
        return accepted == expectedToPass;
    }

    /// <summary>Arbitrary SQL text preserves the parser's success-or-failure output invariant.</summary>
    /// <param name="input">Generated SQL text; null maps to the existing empty-input rejection case.</param>
    /// <returns>Whether success yields a comparable expression and failure yields only a nonempty code.</returns>
    [Property(MaxTest = 2000)]
    public bool SqlExpressionParser_ArbitraryInputPreservesTheTryParseContract(
        string? input
    )
    {
        // Arrange
        // WHY: Null is outside TryParse's SQL-text contract. Map FsCheck's null case
        // to the valid empty-input rejection path so every generated case still
        // verifies the parser's success-or-failure output invariant.
        var sql = input ?? string.Empty;

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(
            sql,
            out var expression,
            out var failureCode);

        var satisfiesContract = parsed
            ? expression is not null
            && failureCode.Length == 0
            && SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
            : expression is null && failureCode.Length > 0;

        // Assert
        return satisfiesContract;
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
        var lower = SafeMigrationSql.Literal(literal - 1L);
        var upper = SafeMigrationSql.Literal(literal + 1L);

        return (shapeSelector % 6) switch
        {
            0 => SafeMigrationSql.Binary(value, binaryOperator, SafeMigrationSql.Literal(literal)),
            1 => SafeMigrationSql.Unary(
                negated
                    ? SafeMigrationSqlUnaryOperator.Not
                    : SafeMigrationSqlUnaryOperator.Negate,
                value),
            2 => SafeMigrationSql.Between(value, lower, upper, negated),
            3 => SafeMigrationSql.In(value, [lower, upper], negated),
            4 => SafeMigrationSql.Function("coalesce", value, SafeMigrationSql.Literal(literal)),
            _ => SafeMigrationSql.Collate(SafeMigrationSql.Cast(value, "text"), "canonical", "catalog"),
        };
    }
}
