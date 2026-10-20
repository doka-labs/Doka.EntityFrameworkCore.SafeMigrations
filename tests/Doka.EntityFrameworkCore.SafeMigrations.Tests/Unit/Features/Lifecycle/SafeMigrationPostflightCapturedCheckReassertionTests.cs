namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies complete known-rename table probes after source-preserving captured CHECK reassertions.</summary>
public sealed class SafeMigrationPostflightCapturedCheckReassertionTests
{
    private const string SourceSql = "leftbound >= 0 AND COALESCE(1e0, 2) = 1e0 "
        + "AND CAST(leftbound AS bigint) >= 0 AND 'KeepCase' = 'KeepCase'";

    /// <summary>Only native column-token spelling may preserve a transformed complete-table contract.</summary>
    /// <param name="targetSql">The authored CHECK after the known member rename.</param>
    /// <param name="preserves">Whether every other source role remains byte-identical.</param>
    [Theory]
    [InlineData("score >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(score AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", true)]
    [InlineData("SCORE >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(SCORE AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", true)]
    [InlineData("`score` >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(`score` AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", true)]
    [InlineData("score >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(score AS bigint) >= 0 "
        + "AND 'keepcase' = 'KeepCase'", false)]
    [InlineData("score >= 0 AND COALESCE(1.0, 2) = 1e0 AND CAST(score AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", false)]
    [InlineData("score >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(score AS integer) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", false)]
    [InlineData("score > 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(score AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", false)]
    [InlineData("score >= 0 AND coalesce(1e0, 2) = 1e0 AND CAST(score AS bigint) >= 0 "
        + "AND 'KeepCase' = 'KeepCase'", false)]
    public void CapturedCheckReassertionRetainsOnlyCertifiedFinalTableLineage(string targetSql, bool preserves)
    {
        // Arrange
        var operations = Operations(targetSql);
        var analyses = FinalAnalyses();

        // Act
        var probes = SafeMigrationPostflightProjection.CreateRenamedTableProbes(operations, analyses,
            new NativeCheckIdentity());

        // Assert
        if (!preserves)
        {
            Assert.Null(probes);

            return;
        }

        var probe = Assert.Single(probes!);
        Assert.Equal(0, probe.OperationOrdinal);
        Assert.Equal(0, probe.SafeOrdinal);
        var table = Assert.IsType<EnsureTableIntent>(probe.Operation.Intent).Definition;
        Assert.Equal("retained table", table.Comment);
        Assert.Equal(["score", "payload"], table.Columns.Select(static column => column.Name));
        Assert.Equal("retained column", table.Columns[1].Comment);
        Assert.Equal(SafeMigrationDefaultValueKind.Literal, table.Columns[1].DefaultValue.Kind);
        Assert.Equal(1, Assert.IsType<int>(table.Columns[1].DefaultValue.LiteralValue));
        Assert.Equal(["score"], table.PrimaryKey!.Columns);
        Assert.Equal(["payload"], Assert.Single(table.UniqueConstraints).Columns);
        Assert.Equal("`score` >= 0 AND COALESCE(1e0, 2) = 1e0 AND CAST(`score` AS bigint) >= 0 "
            + "AND 'KeepCase' = 'KeepCase'", Assert.Single(table.CheckConstraints).CapturedSql);
    }

    /// <summary>Explicit opaque CHECKs and later additions cannot acquire a historical full-table rewrite.</summary>
    /// <param name="addColumn">Whether a later additive mutation changes the historical strict shape.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedShapeMutationDoesNotAcquireCapturedCheckLineage(bool addColumn)
    {
        // Arrange
        var member = addColumn ? "`score`" : "score";
        var target = $"{member} >= 0 AND COALESCE(1e0, 2) = 1e0 "
            + $"AND CAST({member} AS bigint) >= 0 AND 'KeepCase' = 'KeepCase'";

        var operations = Operations(target, opaqueTarget: !addColumn).ToList();

        var analyses = FinalAnalyses().ToList();
        if (addColumn)
        {
            operations.Add(Safe(new EnsureColumnIntent("items", new ExpectedColumnDefinition(
                "added", typeof(int), true, "int"))));

            analyses.Add(new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, true, "classified_matching"));
        }

        // Act
        var probes = SafeMigrationPostflightProjection.CreateRenamedTableProbes(operations, analyses,
            new NativeCheckIdentity());

        // Assert
        Assert.Null(probes);
    }

    private static MigrationOperation[] Operations(string targetSql, bool opaqueTarget = false)
    {
        var original = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", SourceSql, null);
        var target = opaqueTarget ? new ExpectedCheckConstraintDefinition("ck_items", "items", targetSql)
            : SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", targetSql, null);

        var table = new ExpectedTableDefinition("items",
            [
                new ExpectedColumnDefinition("LeftBound", typeof(int), false, "int"),
                new ExpectedColumnDefinition("payload", typeof(int), false, "int", comment: "retained column",
                    defaultValue: SafeMigrationDefaultValue.Literal(1)),
            ], comment: "retained table",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["LeftBound"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_payload", "items", ["payload"])],
            checkConstraints: [original]);

        return
        [
            Safe(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            Safe(new RenameColumnIntent("LeftBound", "items", "score")),
            Safe(new EnsureCheckConstraintIntent(target)),
        ];
    }

    private static SafeMigrationProviderAnalysis[] FinalAnalyses() =>
    [
        new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false, "classified_different"),
        new(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, true, "classified_missing"),
        new(SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "classified_matching"),
    ];

    private static SafeMigrationOperation Safe(SafeMigrationIntent intent) =>
        new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private sealed class NativeCheckIdentity : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public StringComparer ColumnIdentifierComparer => StringComparer.OrdinalIgnoreCase;

        public char? CapturedSqlIdentifierDelimiter => '`';

        public string NormalizeIdentifier(string identifier) => identifier;

        public string? NormalizeSchema(string? schema) => schema;

        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }
}
