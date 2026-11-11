namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Measures prepared Core evidence and lookup paths without numeric acceptance budgets.</summary>
public class CoreEvidenceBenchmarks
{
    private readonly StringBuilder _display = new(new string('x', 1020) + "\\u20AC");
    private readonly string[] _keyColumns = ["Code", "Id"];
    private SafeMigrationOperation _alter = null!;
    private string _counts = null!;
    private SafeMigrationPreflightProjection _emptyManagedProjection = null!;
    private string _facets = null!;
    private SafeMigrationProviderAnalysis _live = null!;
    private SafeMigrationOperation _managedEnsure = null!;
    private SafeMigrationProviderAnalysis _managedLive = null!;
    private MigrationOperation[] _ordinaryColumns = null!;
    private SafeMigrationPreflightProjection _projection = null!;
    private SafeMigrationIntent _requiredColumn = null!;
    private string _states = null!;

    /// <summary>Prepares exact payloads and validates functional controls outside measured invocations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _facets = string.Join(
            "\u001e",
            Enumerable.Repeat("column_store_type\u001fvarchar(200)\u001fvarchar(10)", 16));

        _states = new string('2', 384);
        _counts = string.Join(',', Enumerable.Repeat("17", 64));
        _requiredColumn = new EnsureColumnIntent(
            "Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false, storeType: "tinyint(1)"),
            schema: "application");

        SetupIdentityFixtures();
        SetupManagedFixture();

        _alter = new SafeMigrationOperation(
            new AlterColumnIntent("items", VarcharColumn(500), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);

        _live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "alter_proof");

        var facets = SafeMigrationFacetDifferenceParser.Parse(_facets, "TestProvider");
        if (facets.Count != 16
            || facets.Any(static facet => facet.Facet != "column_store_type"
                || facet.Expected != "varchar(200)"
                || facet.Actual != "varchar(10)"))
        {
            throw new InvalidOperationException("Prepared facet evidence did not retain all exact records.");
        }

        var evidence = SafeMigrationModelManagedDataEvidence.Parse(_states, 384, _counts, 64, "test");
        if (evidence.RowStates.Length != 384
            || evidence.RowStates.Any(static state => state != SafeMigrationModelManagedRowState.Target)
            || evidence.DependencyCounts.Length != 64
            || evidence.DependencyCounts.Any(static count => count != 17))
        {
            throw new InvalidOperationException("Prepared model-managed evidence did not retain exact values.");
        }

        var canonical = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
            _keyColumns,
            normalizer: null,
            out var ordinals);

        if (!ReferenceEquals(_keyColumns, canonical)
            || ordinals is not null
            || PlanAction() != SafeMigrationAction.Repair
            || SelectDisplayBoundary() != 1020
            || LookupDiagnosticReason()
            != "A required column cannot be added to existing rows "
            + "without an explicitly supplied replacement value.")
        {
            throw new InvalidOperationException("Prepared Core lookup controls changed their functional output.");
        }

        var analyzer = new DependencyAnalyzer();
        var validation = CreateProjection(analyzer);
        _ = validation.Project(_alter, _live);
        if (!analyzer.WasCalled
            || analyzer.HasForeignKeyDependency
            || !analyzer.RepeatedEnumerationMatches)
        {
            throw new InvalidOperationException("Prepared no-FK projection did not retain its dependency contract.");
        }
    }

    /// <summary>Creates a fresh no-FK lookup fixture without including table preparation in the measurement.</summary>
    [IterationSetup(Target = nameof(ProjectAlterWithoutForeignKeys))]
    public void PrepareProjection() => _projection = CreateProjection(new DependencyAnalyzer());

    /// <summary>Parses the exact prepared sixteen-record facet payload.</summary>
    /// <returns>The parsed record count.</returns>
    [Benchmark]
    public int ParseFacetEvidence() => SafeMigrationFacetDifferenceParser.Parse(_facets, "TestProvider")
        .Count;

    /// <summary>Parses the prepared 384-row, 64-dependency model-managed payload.</summary>
    /// <returns>The final dependency count plus the row-state cardinality.</returns>
    [Benchmark]
    public long ParseModelManagedEvidence()
    {
        var evidence = SafeMigrationModelManagedDataEvidence.Parse(_states, 384, _counts, 64, "test");

        return evidence.DependencyCounts[63] + evidence.RowStates.Length;
    }

    /// <summary>Projects one alteration with 1000 unrelated accepted tables and no foreign keys.</summary>
    /// <returns>The conservative projected state.</returns>
    [Benchmark]
    public SafeMigrationObservedState ProjectAlterWithoutForeignKeys() => _projection.Project(_alter, _live)
        .ObservedState;

    /// <summary>Plans the canonical action without constructing a public decision.</summary>
    /// <returns>The planned repair action.</returns>
    [Benchmark]
    public SafeMigrationAction PlanAction() => SafeMigrationDecisionPlanner.PlanAction(
        _requiredColumn.Kind,
        SafeMigrationObservedState.Different,
        SafeMigrationPolicy.RepairIfSafe,
        SafeMigrationRepairCapability.Safe);

    /// <summary>Canonicalizes an already ordered composite model-managed key.</summary>
    /// <returns>The canonical column list.</returns>
    [Benchmark]
    public IReadOnlyList<string> CanonicalizeKey() => SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
        _keyColumns,
        normalizer: null,
        out _);

    /// <summary>Selects a display prefix without splitting the prepared escaped Unicode token.</summary>
    /// <returns>The complete-token boundary.</returns>
    [Benchmark]
    public int SelectDisplayBoundary() => SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(_display, 0, 1024);

    /// <summary>Looks up the required-column diagnostic explanation from prepared intent metadata.</summary>
    /// <returns>The canonical reason text.</returns>
    [Benchmark]
    public string LookupDiagnosticReason() => SafeMigrationDiagnosticMessages.GetExplanation(
            _requiredColumn,
            SafeMigrationObservedState.DataBlocked,
            "required_column_backfill_missing",
            "data_blocked")
        .Reason;

    /// <summary>Captures immutable identity metadata from the prepared safe column intent.</summary>
    /// <returns>The captured authored column identity.</returns>
    [Benchmark]
    public SafeMigrationOperationIdentity CaptureSafeColumnIdentity() => SafeMigrationOperationIdentity.Create(
        _requiredColumn);

    /// <summary>Captures identities for prepared ordinary EF add, alter, drop and rename operations.</summary>
    /// <returns>The total captured column count across all four families.</returns>
    [Benchmark]
    public int CaptureOrdinaryColumnIdentities()
    {
        var count = 0;
        foreach (var operation in _ordinaryColumns)
        {
            count += SafeMigrationRunner.CaptureProviderIdentity(operation)!
                .Columns.Count;
        }

        return count;
    }

    /// <summary>Projects a prepared 128-row managed Ensure without adding evidence to the empty cache.</summary>
    /// <returns>The unchanged live provider analysis.</returns>
    [Benchmark]
    public SafeMigrationProviderAnalysis ProjectEmptyManagedEvidence() => _emptyManagedProjection.Project(
        _managedEnsure,
        _managedLive);

    /// <summary>Prepares ordinary EF operations and validates exact production identity output.</summary>
    private void SetupIdentityFixtures()
    {
        _ordinaryColumns =
        [
            new AddColumnOperation
            {
                Name = "Value",
                Table = "Items",
                Schema = "authored",
                ClrType = typeof(int),
            },
            new AlterColumnOperation
            {
                Name = "Value",
                Table = "Items",
                Schema = "authored",
                ClrType = typeof(int),
            },
            new DropColumnOperation
            {
                Name = "Value",
                Table = "Items",
                Schema = "authored",
            },
            new RenameColumnOperation
            {
                Name = "Value",
                Table = "Items",
                Schema = "authored",
                NewName = "NewValue",
            },
        ];

        var safeIdentity = CaptureSafeColumnIdentity();
        if (safeIdentity.OperationKind != _requiredColumn.Kind
            || safeIdentity is not
            {
                Schema: "application",
                Table: "Items",
                ObjectName: "IsActive",
                Columns: ["IsActive"],
                NewObjectName: null,
            })
        {
            throw new InvalidOperationException("Prepared safe column identity did not retain exact metadata.");
        }

        SafeMigrationOperationKind[] kinds =
        [
            SafeMigrationOperationKind.EnsureColumn, SafeMigrationOperationKind.AlterColumn,
            SafeMigrationOperationKind.DropColumn, SafeMigrationOperationKind.RenameColumn,
        ];

        for (var index = 0; index < _ordinaryColumns.Length; index++)
        {
            var identity = SafeMigrationRunner.CaptureProviderIdentity(_ordinaryColumns[index]);
            var expectedNewName = index == 3
                ? "NewValue"
                : null;

            if (identity is not
                {
                    OperationKind: var kind,
                    Schema: "authored",
                    Table: "Items",
                    ObjectName: "Value",
                    Columns: ["Value"],
                }
                || kind != kinds[index]
                || identity.NewObjectName != expectedNewName)
            {
                throw new InvalidOperationException("Prepared ordinary column identity did not retain exact metadata.");
            }
        }

        if (CaptureOrdinaryColumnIdentities() != 4)
        {
            throw new InvalidOperationException("Prepared ordinary identity batch did not capture all four columns.");
        }
    }

    /// <summary>Prepares the large empty-cache fallback without observing or accepting any row evidence.</summary>
    private void SetupManagedFixture()
    {
        var values = new object?[128, 2];
        for (var row = 0; row < 128; row++)
        {
            values[row, 0] = row + 1;
            values[row, 1] = "administrator";
        }

        _managedEnsure = new SafeMigrationOperation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "name"],
                ["int", "varchar(64)"],
                values,
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);

        _managedLive = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            false,
            "test_live");

        _emptyManagedProjection = new SafeMigrationPreflightProjection();
        var first = ProjectEmptyManagedEvidence();
        var repeated = ProjectEmptyManagedEvidence();
        if (!ReferenceEquals(_managedLive, first)
            || !ReferenceEquals(_managedLive, repeated)
            || first.ObservedState != SafeMigrationObservedState.Matching
            || first.Code != "test_live")
        {
            throw new InvalidOperationException("Prepared empty-cache managed projection changed live fallback.");
        }
    }

    /// <summary>Recreates the accepted no-FK table stream used by the dependency lookup regression.</summary>
    /// <param name="analyzer">The callback-local dependency observation consumer.</param>
    /// <returns>A projection with one altered owner and 1000 unrelated tables.</returns>
    private static SafeMigrationPreflightProjection CreateProjection(
        DependencyAnalyzer analyzer
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: analyzer);
        ApplyTable(projection, "items", VarcharColumn(200));
        for (var number = 0; number < 1000; number++)
        {
            ApplyTable(
                projection,
                $"unrelated_{number}",
                new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"));
        }

        return projection;
    }

    /// <summary>Accepts one exact strict table definition before measuring the alteration.</summary>
    /// <param name="projection">The fixture-owned ordered projection.</param>
    /// <param name="name">The authored table identity.</param>
    /// <param name="column">The complete single-column definition.</param>
    private static void ApplyTable(
        SafeMigrationPreflightProjection projection,
        string name,
        ExpectedColumnDefinition column
    )
    {
        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(name, [column]),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            false,
            "test_live")
        {
            RenameTargetExists = false,
        };

        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        if (decision.Action != SafeMigrationAction.Apply)
        {
            throw new InvalidOperationException("Prepared no-FK table was not accepted for creation.");
        }

        projection.Observe(operation, live, analysis, decision);
    }

    /// <summary>Builds the source or target definition of the exact varchar widening workload.</summary>
    /// <param name="length">The declared maximum varchar length.</param>
    /// <returns>The nullable value column with matching store type and length facets.</returns>
    private static ExpectedColumnDefinition VarcharColumn(
        int length
    ) => new("value", typeof(string), isNullable: true, storeType: $"varchar({length})", maxLength: length);

    /// <summary>Copies only scalar callback observations without retaining borrowed projection views.</summary>
    private sealed class DependencyAnalyzer : ISafeMigrationProjectedColumnAnalyzer
    {
        /// <summary>Gets whether the alteration reached provider dependency validation.</summary>
        public bool WasCalled { get; private set; }

        /// <summary>Gets the copied FK dependency observation.</summary>
        public bool HasForeignKeyDependency { get; private set; }

        /// <summary>Gets whether callback-local table enumeration remains stable.</summary>
        public bool RepeatedEnumerationMatches { get; private set; }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            var columns = context.Table.Columns.ToArray();
            var uniqueConstraints = context.Table.UniqueConstraints.ToArray();
            var indexes = context.Table.Indexes.ToArray();
            var primaryKey = context.Table.PrimaryKey;

            // WHY: Copy only callback-local evidence; borrowed table views cannot escape this callback.
            WasCalled = true;
            HasForeignKeyDependency = context.HasForeignKeyDependency;
            RepeatedEnumerationMatches = columns.SequenceEqual(context.Table.Columns)
                && uniqueConstraints.SequenceEqual(context.Table.UniqueConstraints)
                && indexes.SequenceEqual(context.Table.Indexes)
                && ReferenceEquals(primaryKey, context.Table.PrimaryKey);

            return projectedAnalysis;
        }
    }
}
