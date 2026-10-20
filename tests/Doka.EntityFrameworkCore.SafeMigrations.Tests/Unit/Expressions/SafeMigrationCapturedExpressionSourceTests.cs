namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Preserves native source semantics beside the bounded structural expression contract.</summary>
public sealed class SafeMigrationCapturedExpressionSourceTests
{
    /// <summary>Parsed computed SQL reaches ordinary provider DDL without typed-literal reinterpretation.</summary>
    /// <param name="sql">The native expression whose exact spelling must survive.</param>
    [Theory]
    [InlineData("2147483648 / 2147483647")]
    [InlineData("1 / 9223372036854775807")]
    [InlineData("1e0 / 3")]
    [InlineData("CASE WHEN [source_value] > 1 THEN 2147483648 ELSE 0 END")]
    public void CapturedComputedSourceSurvivesAddAndAlterReconstruction(string sql)
    {
        // Arrange
        var definition = CaptureColumn(sql);
        var addIntent = new EnsureColumnIntent("items", definition);
        var alterIntent = new AlterColumnIntent("items", definition, definition);

        // Act
        var add = Assert.IsType<AddColumnOperation>(SafeMigrationStandardOperationFactory.Create(
            addIntent, static _ => throw new InvalidOperationException("Captured SQL must not be re-rendered.")));

        var alter = Assert.IsType<AlterColumnOperation>(SafeMigrationStandardOperationFactory.Create(
            alterIntent, static _ => throw new InvalidOperationException("Captured SQL must not be re-rendered.")));

        // Assert
        Assert.NotNull(definition.ComputedExpression);
        Assert.Null(definition.ComputedColumnSql);
        Assert.Same(sql, definition.CapturedComputedSql);
        Assert.Same(sql, add.ComputedColumnSql);
        Assert.Same(sql, alter.ComputedColumnSql);
        Assert.Same(sql, alter.OldColumn.ComputedColumnSql);
    }

    /// <summary>CHECK capture retains arithmetic domains rather than substituting a typed AST rendering.</summary>
    /// <param name="sql">The native predicate whose original literal domains matter.</param>
    [Theory]
    [InlineData("2147483648 / 2147483647 > 1")]
    [InlineData("1e0 / 3 > 0")]
    [InlineData("CASE WHEN [source_value] > 0 THEN 1 ELSE 0 END = 1")]
    public void CapturedCheckSourceSurvivesReconstruction(string sql)
    {
        // Arrange
        var definition = CaptureCheck(sql);

        // Act
        var operation = Assert.IsType<AddCheckConstraintOperation>(SafeMigrationStandardOperationFactory.Create(
            new EnsureCheckConstraintIntent(definition),
            static _ => throw new InvalidOperationException("Captured SQL must not be re-rendered.")));

        // Assert
        Assert.NotNull(definition.Expression);
        Assert.Null(definition.Sql);
        Assert.Same(sql, definition.CapturedSql);
        Assert.Same(sql, operation.Sql);
    }

    /// <summary>Unknown syntax cannot acquire a parsed-source approval by entering through EF metadata.</summary>
    [Fact]
    public void UnsupportedSourceRemainsOpaque()
    {
        // Arrange
        const string sql = "source_value; SELECT 1";

        // Act
        var column = CaptureColumn(sql);
        var check = CaptureCheck(sql);

        // Assert
        Assert.Null(column.ComputedExpression);
        Assert.Null(column.CapturedComputedSql);
        Assert.Same(sql, column.ComputedColumnSql);
        Assert.Null(check.Expression);
        Assert.Null(check.CapturedSql);
        Assert.Same(sql, check.Sql);
    }

    /// <summary>Authoring an explicit raw definition does not opt into the EF metadata capture boundary.</summary>
    [Fact]
    public void ExplicitRawContractsDoNotAcquireCapturedProof()
    {
        // Arrange
        const string sql = "source_value + 1";

        // Act
        var column = new ExpectedColumnDefinition("computed_value", typeof(long), true, computedColumnSql: sql);
        var check = new ExpectedCheckConstraintDefinition("ck_items", "items", sql);

        // Assert
        Assert.Null(column.CapturedComputedSql);
        Assert.Null(column.ComputedExpression);
        Assert.Same(sql, column.ComputedColumnSql);
        Assert.Null(check.CapturedSql);
        Assert.Null(check.Expression);
        Assert.Same(sql, check.Sql);
    }

    /// <summary>Public structured APIs continue to use the explicitly typed provider renderer.</summary>
    [Fact]
    public void ExplicitStructuredContractsDoNotInventCapturedSource()
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(2147483648L);
        var column = new ExpectedColumnDefinition("computed_value", typeof(long), true,
            computedExpression: expression);

        var check = ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items", expression);

        // Act
        var add = Assert.IsType<AddColumnOperation>(SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("items", column), static _ => "typed_column"));

        var constraint = Assert.IsType<AddCheckConstraintOperation>(SafeMigrationStandardOperationFactory.Create(
            new EnsureCheckConstraintIntent(check), static _ => "typed_check"));

        // Assert
        Assert.Null(column.CapturedComputedSql);
        Assert.Null(check.CapturedSql);
        Assert.Equal("typed_column", add.ComputedColumnSql);
        Assert.Equal("typed_check", constraint.Sql);
    }

    /// <summary>Identical parsed CLR values cannot erase the distinct authored native spelling.</summary>
    [Fact]
    public void ComputedSourceParticipatesInEquivalenceRepairAndFingerprint()
    {
        // Arrange
        var plain = CaptureColumn("2147483648 / 2147483647");
        var parenthesized = CaptureColumn("(2147483648 / 2147483647)");
        var repeated = CaptureColumn("2147483648 / 2147483647");
        var typed = new ExpectedColumnDefinition("computed_value", typeof(long), true,
            computedExpression: plain.ComputedExpression);

        // Act
        var differentSourceEqual = SafeMigrationDefinitionEquivalence.Column(plain, parenthesized);
        var repeatedSourceEqual = SafeMigrationDefinitionEquivalence.Column(plain, repeated);
        var typedSourceEqual = SafeMigrationDefinitionEquivalence.Column(plain, typed);
        var repair = SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(plain, parenthesized);
        var firstHash = Fingerprint(new EnsureColumnIntent("items", plain));
        var secondHash = Fingerprint(new EnsureColumnIntent("items", parenthesized));

        // Assert
        Assert.True(SafeMigrationSqlExpressionContract.Equivalent(
            plain.ComputedExpression, parenthesized.ComputedExpression));
        Assert.False(differentSourceEqual);
        Assert.True(repeatedSourceEqual);
        Assert.False(typedSourceEqual);
        Assert.False(repair);
        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(firstHash, Fingerprint(new EnsureColumnIntent("items", repeated)));
    }

    /// <summary>CHECK semantic dictionaries and contract hashes retain the same source-sensitive identity.</summary>
    [Fact]
    public void CheckSourceParticipatesInSemanticHashAndFingerprint()
    {
        // Arrange
        var plain = CaptureCheck("2147483648 / 2147483647 > 1");
        var parenthesized = CaptureCheck("(2147483648 / 2147483647 > 1)");
        var repeated = CaptureCheck("2147483648 / 2147483647 > 1");
        var comparer = SafeMigrationSemanticDefinitionComparers.CheckConstraint;
        var definitions = new HashSet<ExpectedCheckConstraintDefinition>(comparer) { plain, parenthesized, repeated, };

        // Act
        var repeatedEquals = comparer.Equals(plain, repeated);
        var differentlySpelledEquals = comparer.Equals(plain, parenthesized);
        var firstHash = Fingerprint(new EnsureCheckConstraintIntent(plain));
        var secondHash = Fingerprint(new EnsureCheckConstraintIntent(parenthesized));

        // Assert
        Assert.True(repeatedEquals);
        Assert.False(differentlySpelledEquals);
        Assert.Equal(comparer.GetHashCode(plain), comparer.GetHashCode(repeated));
        Assert.Equal(2, definitions.Count);
        Assert.NotEqual(firstHash, secondHash);
    }

    /// <summary>Renaming a semantic inventory candidate changes its identity, not its native arithmetic.</summary>
    [Fact]
    public void SemanticCheckCandidateRetainsCapturedSourceReference()
    {
        // Arrange
        const string sql = "2147483648 / 2147483647 > 1";
        var definition = CaptureCheck(sql);
        var operation = new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(definition), SafeMigrationPolicy.ThrowIfDifferent);

        var unexpected = new SafeMigrationUnexpectedObject(
            SafeMigrationDatabaseObjectKind.CheckConstraint, null, "items", "ck_legacy", "unexpected_check_constraint");

        // Act
        var candidate = Assert.Single(SafeMigrationSemanticCandidateFactory.Create([operation], [unexpected]));
        var copied = Assert.IsType<EnsureCheckConstraintIntent>(candidate.Operation.Intent).Definition;

        // Assert
        Assert.Equal("ck_legacy", copied.Name);
        Assert.Same(sql, copied.CapturedSql);
        Assert.Same(definition.Expression, copied.Expression);
    }

    private static ExpectedColumnDefinition CaptureColumn(string sql) =>
        SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "computed_value",
            Table = "items",
            ClrType = typeof(long),
            IsNullable = true,
            ComputedColumnSql = sql,
        });

    private static ExpectedCheckConstraintDefinition CaptureCheck(string sql) =>
        SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", sql, null);

    private static string Fingerprint(SafeMigrationIntent intent) =>
        SafeMigrationContractFingerprint.Create(
            [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)]);
}
