namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that accepted physical drops and current source-bound row proofs authorize projection.</summary>
public sealed class SqlServerProjectedIntegerTransitionTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=projected_integer;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Every removable dependency requires its exact owner and name to be dropped before widening.</summary>
    [Theory]
    [InlineData("index")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("check")]
    [InlineData("foreign")]
    public void NamedDependency_RequiresExactAcceptedDrop(string kind)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = Widening();
        analyzer.CaptureProjectedIntegerWidening(widening, SafeSource(),
            [new SqlServerSafeMigrationProviderAnalyzer.TransitionDependency(kind, "dbo", "items", "owned")]);
        var blocked = Different();
        var wrongDrop = Drop(kind, "other");
        var exactDrop = Drop(kind, "owned");
        var matching = Matching();

        // Act
        var before = analyzer.ValidateProjectedOperation(widening, blocked, new EmptyColumns());
        analyzer.ObserveAcceptedOperation(wrongDrop, matching, matching, Decision(wrongDrop, matching));
        var afterWrong = analyzer.ValidateProjectedOperation(widening, blocked, new EmptyColumns());
        analyzer.ObserveAcceptedOperation(exactDrop, matching, matching, Decision(exactDrop, matching));
        var afterExact = analyzer.ValidateProjectedOperation(widening, blocked, new EmptyColumns());

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, before.RepairCapability);
        Assert.Equal(SafeMigrationRepairCapability.None, afterWrong.RepairCapability);
        Assert.Equal(SafeMigrationRepairCapability.Safe, afterExact.RepairCapability);
    }

    /// <summary>Core structural uncertainty requires the provider's source and accepted-drop certificate.</summary>
    [Fact]
    public void CoreProjection_AcceptedDropPromotesTheCapturedSourceRepair()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = Widening();
        analyzer.CaptureProjectedIntegerWidening(widening, SafeSource(),
            [new SqlServerSafeMigrationProviderAnalyzer.TransitionDependency("index", "dbo", "items", "owned")]);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer, projectedColumnAnalyzer: analyzer);

        var drop = Drop("index", "owned");
        var matching = Matching();
        var live = Different();

        // Act
        projection.Observe(drop, matching, matching, Decision(drop, matching));
        var analysis = projection.Project(widening, live);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, analysis.RepairCapability);
        Assert.Equal(SafeMigrationAction.Repair, Decision(widening, analysis).Action);
    }

    /// <summary>A catalog-resolved alias discharges only the physical dependency removed by an accepted drop.</summary>
    /// <param name="accepted">Whether the authored drop was physically executable, not an absent-object NoOp.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvedDropIdentity_RequiresAcceptedPhysicalDrop(
        bool accepted
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = Widening();
        var physical = new SqlServerSafeMigrationProviderAnalyzer.TransitionDependency(
            "index", "dbo", "items", "IX_owned");

        var authored = new SqlServerSafeMigrationProviderAnalyzer.TransitionDependency(
            "index", "DBO", "ITEMS", "ix_owned");

        analyzer.CaptureProjectedIntegerWidening(widening, SafeSource(), [physical]);
        analyzer.CaptureProjectedTransitionDropIdentity(authored, physical);
        var drop = new SafeMigrationOperation(new DropIndexIntent("ix_owned", "ITEMS", "DBO"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var state = accepted ? Matching()
            : new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, false, "classified_missing");

        // Act
        analyzer.ObserveAcceptedOperation(drop, state, state, Decision(drop, state));
        var result = analyzer.ValidateProjectedOperation(widening, Different(), new EmptyColumns());

        // Assert
        Assert.Equal(accepted ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
    }

    /// <summary>Metadata-only widening survives managed DML, but nullable-tightening evidence is global.</summary>
    [Theory]
    [InlineData(false, SafeMigrationRepairCapability.Safe)]
    [InlineData(true, SafeMigrationRepairCapability.None)]
    public void UnrelatedManagedDml_InvalidatesOnlyRowDependentWidening(
        bool tightensNullability,
        SafeMigrationRepairCapability expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = Widening(tightensNullability);
        analyzer.CaptureProjectedIntegerWidening(widening, SafeSource(), []);
        var data = new SafeMigrationOperation(new EnsureModelManagedDataIntent("other_items", ["Id"], ["int"],
            ["Id"], ["int"], new object?[,] { { 1 } }, null, null), SafeMigrationPolicy.ThrowIfDifferent);

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "classified_missing");

        // Act
        analyzer.ObserveAcceptedOperation(data, missing, missing, Decision(data, missing));
        var analysis = analyzer.ValidateProjectedOperation(widening, Different(), new EmptyColumns());

        // Assert
        Assert.Equal(expected, analysis.RepairCapability);
    }

    /// <summary>Opaque SQL and changed column identity cannot inherit the old source certificate.</summary>
    [Theory]
    [InlineData("sql")]
    [InlineData("rename")]
    [InlineData("drop")]
    public void SourceIdentityChange_RejectsStaleWidening(string mutation)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var widening = Widening();
        analyzer.CaptureProjectedIntegerWidening(widening, SafeSource(), []);
        var matching = Matching();

        // Act
        if (mutation == "sql")
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "UPDATE dbo.items SET Value=1;" });
        }
        else
        {
            var change = new SafeMigrationOperation(mutation == "rename"
                ? new RenameColumnIntent("Value", "items", "Changed") : new DropColumnIntent("Value", "items"),
                SafeMigrationPolicy.ThrowIfDifferent);

            analyzer.ObserveAcceptedOperation(change, matching, matching, Decision(change, matching));
        }

        var analysis = analyzer.ValidateProjectedOperation(widening, Different(), new EmptyColumns());

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
    }

    /// <summary>Repeated references and captures crossing 512 classifiers retain bounded transport ownership.</summary>
    [Theory]
    [InlineData(true, 513)]
    [InlineData(false, 513)]
    [InlineData(true, 1)]
    public async Task TransitionCapture_IsSegmentedAndToleratesRepeatedInstances(
        bool nativeBatch,
        int count
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformClassifierTable = table =>
            {
                foreach (DataRow row in table.Rows)
                {
                    row["state"] = "different";
                    row["repair"] = 1;
                }
            },
        };

        var operations = Enumerable.Range(0, count).Select(index => Widening(table: "items_" + index)).ToArray();
        var ordered = operations.SelectMany(operation => new[]
        {
            new SafeMigrationOperation(new DropIndexIntent("ix_alias", ((AlterColumnIntent)operation.Intent).Table),
                SafeMigrationPolicy.ThrowIfDifferent),
            operation,
        }).ToArray();

        var repeated = ordered.Concat(ordered).ToArray();

        // Act
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, repeated,
            repeated.Select(static _ => Different()).ToArray(), true, 73,
            CancellationToken.None);
        var result = analyzer.ValidateProjectedOperation(operations[^1], Different(), new EmptyColumns());

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.All(connection.BatchPayloadBytes,
            size => Assert.InRange(size, 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
        Assert.All(connection.ObservedTimeouts, timeout => Assert.Equal(73, timeout));
        Assert.True(connection.RecordedStatements.Count > 0);
        Assert.Contains(connection.RecordedStatements,
            statement => statement.Contains("i.index_id>0", StringComparison.Ordinal));
        Assert.Contains(connection.RecordedStatements,
            statement => statement.Contains("COLLATE CATALOG_DEFAULT", StringComparison.Ordinal)
                && statement.Contains("d.name=", StringComparison.Ordinal));
    }

    /// <summary>Denied dependency-catalog reads suppress widening proofs but retain CHECK drop identities.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransitionCapture_DeniedDependencyCatalogNeverBindsProtectedView(
        bool capturesCheckDrop
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(false);
        var operations = new List<SafeMigrationOperation> { Widening() };
        if (capturesCheckDrop)
        {
            operations.Add(Drop("check", "CK_items"));
            operations.Add(new SafeMigrationOperation(new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=0")),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        // Act
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, operations,
            operations.Select(static _ => Different()).ToArray(), false, 73,
            CancellationToken.None);

        // Assert
        Assert.DoesNotContain(connection.RecordedStatements,
            static statement => statement.Contains("sys.sql_expression_dependencies", StringComparison.Ordinal));
        Assert.Equal(capturesCheckDrop, connection.RecordedStatements.Count > 0);
    }

    /// <summary>Lazy sets remain candidate-owned while duplicate catalog rows describe one physical blocker.</summary>
    /// <param name="candidate">The captured ordinal to inspect.</param>
    /// <param name="safe">Whether that ordinal has no named dependency.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task TransitionCapture_DependencyRowsDoNotContaminateEmptyCandidates(
        int candidate,
        bool safe
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = Analyzer(context);
        var operations = new[] { Widening(table: "items_0"), Widening(table: "items_1") };
        await using var connection = new SqlServerCatalogTestConnection(false)
        {
            TransformClassifierTable = table =>
            {
                if (table.Rows.Count > 0)
                {
                    foreach (DataRow row in table.Rows)
                    {
                        row["state"] = "different";
                        row["repair"] = 1;
                    }

                    return;
                }

                // WHY: The existing transport double yields no rows for a
                // dependency UNION. Inject its five-column contract directly;
                // repeated rows must stay local to the second candidate.
                table.Columns.Clear();
                table.Columns.Add("ordinal", typeof(int));
                table.Columns.Add("kind", typeof(string));
                table.Columns.Add("schema", typeof(string));
                table.Columns.Add("table", typeof(string));
                table.Columns.Add("name", typeof(string));
                table.Rows.Add(1, "index", "dbo", "items_1", "ix_owned");
                table.Rows.Add(1, "index", "dbo", "items_1", "ix_owned");
            },
        };

        // Act
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, operations,
            operations.Select(static _ => Different()).ToArray(), true, 73,
            CancellationToken.None);
        var result = analyzer.ValidateProjectedOperation(operations[candidate], Different(), new EmptyColumns());

        // Assert
        Assert.Equal(safe ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
        Assert.Equal(4, connection.RowsRead);
    }

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation Widening(
        bool tightens = false,
        string table = "items"
    )
        => new(new AlterColumnIntent(table, new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), tightens, "int")), SafeMigrationPolicy.RepairIfSafe);

    private static SafeMigrationOperation Drop(
        string kind,
        string name
    )
        => new(kind switch
        {
            "index" => new DropIndexIntent(name, "items"),
            "primary" => new DropPrimaryKeyIntent(name, "items"),
            "unique" => new DropUniqueConstraintIntent(name, "items"),
            "check" => new DropCheckConstraintIntent(name, "items"),
            "foreign" => new DropForeignKeyIntent(name, "items"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis SafeSource()
        => new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "candidate_safe");

    private static SafeMigrationProviderAnalysis Different()
        => new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false, "classified_different");

    private static SafeMigrationProviderAnalysis Matching()
        => new(SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "classified_matching");

    private static SafeMigrationDecision Decision(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
        => SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = null;

            return false;
        }
    }
}
