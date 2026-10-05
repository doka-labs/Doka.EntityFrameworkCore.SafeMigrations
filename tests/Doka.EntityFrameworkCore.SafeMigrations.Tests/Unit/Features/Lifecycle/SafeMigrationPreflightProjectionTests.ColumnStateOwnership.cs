namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Proves independent table creation does not rescan accumulated column states.</summary>
    [Fact]
    public void CreatingIndependentTablesKeepsColumnStateCleanupLinear()
    {
        // Arrange
        const int tableCount = 1_000;
        var normalizer = new ColumnStateOwnershipNormalizer();
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: normalizer);
        var operations = Enumerable.Range(0, tableCount)
            .Select(number => new SafeMigrationOperation(
                new EnsureTableIntent(
                    new ExpectedTableDefinition($"items_{number}", [Column("id")]),
                    SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();
        var live = Live(SafeMigrationObservedState.Missing);

        // Act
        foreach (var operation in operations)
        {
            var analysis = projection.Project(operation, live);
            var decision = SafeMigrationDecisionPlanner.Plan(
                operation.Intent.Kind,
                analysis.ObservedState,
                operation.Policy,
                analysis.RepairCapability);

            projection.Observe(operation, live, analysis, decision);
        }

        var identityComparisons = normalizer.CountingComparer.EqualityChecks;
        var projectedColumns = operations.Count(operation =>
            ((ISafeMigrationProjectedColumnSource)projection).TryGetProjectedColumn(
                operation.Intent.ObjectName,
                schema: null,
                "id",
                out _));

        // Assert
        Assert.Equal(tableCount, projectedColumns);
        Assert.InRange(identityComparisons, 0, tableCount * 80);
    }

    /// <summary>Proves table rename does not scan column states owned by unrelated tables.</summary>
    /// <param name="providerOperation">Whether the accepted rename is an ordinary provider operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingTableDoesNotScanUnrelatedColumnStates(
        bool providerOperation
    )
    {
        // Arrange
        var normalizer = new ColumnStateOwnershipNormalizer();
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: normalizer);

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));

        for (var number = 0; number < 1_000; number++)
        {
            projection.ObserveProviderPostcondition(ProviderColumn("id", $"unrelated_{number}", isNullable: false));
        }

        var comparisonsBeforeRename = normalizer.CountingComparer.EqualityChecks;

        // Act
        if (providerOperation)
        {
            projection.ObserveProviderPostcondition(new RenameTableOperation { Name = "items", NewName = "renamed" });
        }
        else
        {
            ObserveAccepted(projection, new RenameTableIntent("items", "renamed"), SafeMigrationObservedState.Matching);
        }

        var renameComparisons = normalizer.CountingComparer.EqualityChecks - comparisonsBeforeRename;
        var columns = (ISafeMigrationProjectedColumnSource)projection;
        var renamedColumnExists = columns.TryGetProjectedColumn("renamed", null, "id", out var renamedColumn);
        var unrelatedColumnExists = columns.TryGetProjectedColumn("unrelated_999", null, "id", out _);

        // Assert
        Assert.InRange(renameComparisons, 0, 200);
        Assert.True(renamedColumnExists);
        Assert.Equal("id", renamedColumn?.Name);
        Assert.True(unrelatedColumnExists);
        Assert.False(columns.TryGetProjectedColumn("items", null, "id", out _));
    }

    /// <summary>Preserves exact, missing and unknown column states through normalized table aliases.</summary>
    /// <param name="providerOperation">Whether the accepted table rename is an ordinary provider operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingTablePreservesColumnStatesAndQualifiedIsolation(
        bool providerOperation
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnStateOwnershipNormalizer(normalizeAliases: true));

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("Items", [Column("id"), Column("legacy")]),
                SafeMigrationTableMode.StrictDefinition));
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [Column("id"), Column("legacy")], schema: "archive"),
                SafeMigrationTableMode.StrictDefinition));
        projection.ObserveProviderPostcondition(
            new RenameColumnOperation { Table = "ITEMS", Schema = "DBO", Name = "legacy", NewName = "current" });

        // WHY: An ordinary provider postcondition may lack prior exact source
        // evidence. Its renamed target must stay unknown, never inherit live proof.

        projection.ObserveProviderPostcondition(
            new RenameColumnOperation { Table = "ITEMS", Schema = "dbo", Name = "unobserved", NewName = "opaque" });

        // Act
        if (providerOperation)
        {
            projection.ObserveProviderPostcondition(
                new RenameTableOperation
                {
                    Name = "ITEMS",
                    Schema = "DBO",
                    NewName = "Renamed",
                    NewSchema = "next",
                });
        }
        else
        {
            ObserveAccepted(
                projection,
                new RenameTableIntent("ITEMS", "Renamed", schema: "DBO", newSchema: "next"),
                SafeMigrationObservedState.Matching);
        }

        var exact = ProjectOwnedColumn(projection, "RENAMED", "NEXT", "id", SafeMigrationObservedState.Missing);
        var renamed = ProjectOwnedColumn(projection, "renamed", "next", "current", SafeMigrationObservedState.Missing);
        var missing = ProjectOwnedColumn(projection, "renamed", "next", "legacy", SafeMigrationObservedState.Matching);
        var unknown = ProjectOwnedColumn(projection, "renamed", "next", "opaque", SafeMigrationObservedState.Matching);
        var qualified = ProjectOwnedColumn(
            projection,
            "ITEMS",
            "ARCHIVE",
            "legacy",
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, exact.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, renamed.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, missing.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, unknown.ObservedState);
        Assert.Equal("projected_structure_state_unknown", unknown.Code);
        Assert.Equal(SafeMigrationObservedState.Matching, qualified.ObservedState);
        Assert.False(
            ((ISafeMigrationProjectedColumnSource)projection).TryGetProjectedColumn("items", "dbo", "id", out _));
    }

    /// <summary>Proves drop and recreation replace only the owning table's accumulated column states.</summary>
    /// <param name="providerOperation">Whether drop and recreation use ordinary provider operations.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecreatingTableReplacesColumnStatesWithoutTouchingQualifiedPeers(
        bool providerOperation
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnStateOwnershipNormalizer(normalizeAliases: true));

        projection.ObserveProviderPostcondition(ProviderColumn("old", "items", isNullable: false));
        projection.ObserveProviderPostcondition(new DropColumnOperation { Table = "items", Name = "missing" });

        var opaque = ProviderColumn("opaque", "items", isNullable: false);

        opaque.AddAnnotation("provider:opaque", new object());
        projection.ObserveProviderPostcondition(opaque);

        var qualifiedColumn = ProviderColumn("old", "items", isNullable: false);

        qualifiedColumn.Schema = "archive";
        projection.ObserveProviderPostcondition(qualifiedColumn);
        projection.ObserveProviderPostcondition(ProviderColumn("old", "unrelated", isNullable: false));

        // Act
        if (providerOperation)
        {
            projection.ObserveProviderPostcondition(new DropTableOperation { Name = "ITEMS", Schema = "DBO" });

            var recreated = new CreateTableOperation { Name = "ITEMS", Schema = "DBO" };

            recreated.Columns.Add(ProviderColumn("missing", "ITEMS", isNullable: false));
            recreated.Columns.Add(ProviderColumn("opaque", "ITEMS", isNullable: false));
            projection.ObserveProviderPostcondition(recreated);
        }
        else
        {
            ObserveAccepted(
                projection,
                new DropTableIntent("ITEMS", schema: "DBO"),
                SafeMigrationObservedState.Matching);
            Apply(
                projection,
                new EnsureTableIntent(
                    new ExpectedTableDefinition("ITEMS", [Column("missing"), Column("opaque")], schema: "DBO"),
                    SafeMigrationTableMode.StrictDefinition));
        }

        var columns = (ISafeMigrationProjectedColumnSource)projection;
        var missing = ProjectOwnedColumn(projection, "items", null, "missing", SafeMigrationObservedState.Different);
        var opaqueAnalysis = ProjectOwnedColumn(
            projection,
            "items",
            null,
            "opaque",
            SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, missing.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, opaqueAnalysis.ObservedState);
        Assert.False(columns.TryGetProjectedColumn("items", null, "old", out _));
        Assert.True(columns.TryGetProjectedColumn("ITEMS", "ARCHIVE", "old", out _));
        Assert.True(columns.TryGetProjectedColumn("unrelated", "dbo", "old", out _));
    }

    /// <summary>Proves opaque operations clear all table-owned column evidence before later projection.</summary>
    /// <param name="sqlOperation">Whether the opaque operation is raw SQL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueOperationsClearEveryOwnedColumnState(
        bool sqlOperation
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(ProviderColumn("exact", "items", isNullable: false));
        projection.ObserveProviderPostcondition(new DropColumnOperation { Table = "items", Name = "missing" });

        var opaque = ProviderColumn("opaque", "other", isNullable: false);

        opaque.AddAnnotation("provider:opaque", new object());
        projection.ObserveProviderPostcondition(opaque);

        // Act
        projection.ObserveProviderPostcondition(sqlOperation
            ? new SqlOperation { Sql = "SELECT 1;" }
            : new AlterDatabaseOperation());

        var exact = ProjectOwnedColumn(projection, "items", null, "exact", SafeMigrationObservedState.Matching);
        var missing = ProjectOwnedColumn(projection, "items", null, "missing", SafeMigrationObservedState.Matching);
        var unknown = ProjectOwnedColumn(projection, "other", null, "opaque", SafeMigrationObservedState.Matching);

        // Assert
        Assert.False(
            ((ISafeMigrationProjectedColumnSource)projection).TryGetProjectedColumn("items", null, "exact", out _));
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, exact.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, missing.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, unknown.ObservedState);
        Assert.True(exact.IsOpaqueProjectionUnknown);
        Assert.True(missing.IsOpaqueProjectionUnknown);
        Assert.True(unknown.IsOpaqueProjectionUnknown);
    }

    private static SafeMigrationProviderAnalysis ProjectOwnedColumn(
        SafeMigrationPreflightProjection projection,
        string table,
        string? schema,
        string column,
        SafeMigrationObservedState liveState
    ) => projection.Project(
        new SafeMigrationOperation(
            new EnsureColumnIntent(table, Column(column), schema),
            SafeMigrationPolicy.ThrowIfDifferent),
        Live(liveState));

    private sealed class ColumnStateOwnershipNormalizer(
        bool normalizeAliases = false
    ) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <summary>Gets the instrumented provider identifier comparer.</summary>
        public CountingColumnStateIdentifierComparer CountingComparer { get; } = new(normalizeAliases);

        /// <inheritdoc />
        public StringComparer IdentifierComparer => CountingComparer;

        /// <inheritdoc />
        public string NormalizeIdentifier(
            string identifier
        ) => normalizeAliases ? identifier.ToUpperInvariant() : identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(
            string? schema
        ) => !normalizeAliases
            ? schema
            : schema is null || StringComparer.OrdinalIgnoreCase.Equals(schema, "dbo")
                ? null
                : schema.ToUpperInvariant();

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        ) => false;
    }

    private sealed class CountingColumnStateIdentifierComparer(
        bool ignoreCase
    ) : StringComparer
    {
        private readonly StringComparer _comparer = ignoreCase ? OrdinalIgnoreCase : Ordinal;

        /// <summary>Gets the number of identifier equality comparisons.</summary>
        public int EqualityChecks { get; private set; }

        /// <inheritdoc />
        public override int Compare(
            string? left,
            string? right
        ) => _comparer.Compare(left, right);

        /// <inheritdoc />
        public override bool Equals(
            string? left,
            string? right
        )
        {
            EqualityChecks++;

            return _comparer.Equals(left, right);
        }

        /// <inheritdoc />
        public override int GetHashCode(
            string value
        ) => _comparer.GetHashCode(value);
    }
}
