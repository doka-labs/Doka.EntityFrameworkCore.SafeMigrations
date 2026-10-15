namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Captures the runtime catalog plans emitted for one ordered analysis batch.
/// </summary>
/// <remarks>
/// Analysis activates the capture before invoking Doka's operation handlers.
/// Runtime generation uses the same scoped service without an active capture.
/// </remarks>
internal sealed class MySqlSafeMigrationPlanCapture
{
    private static readonly IReadOnlyList<ExpectedIndexDefinition> s_emptyUniqueIndexes = [];
    private static readonly IReadOnlyDictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>
        s_emptyUniqueIndexCatalog = new Dictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>();

    private SafeMigrationOperation[]? _expected;
    private IReadOnlyDictionary<
        (string? Schema, string Table), SafeMigrationExpectedTableConstraints>? _expectedTableConstraints;
    private IReadOnlyDictionary<
        (string? Schema, string Table), SafeMigrationExpectedTableConstraints>? _generationTableConstraints;
    private IReadOnlyDictionary<
        (string? Schema, string Table), SafeMigrationExpectedTableConstraints>? _generationFoldedTableConstraints;
    private IReadOnlyList<string>? _generationDatabaseQualifiers;
    private MySqlExpectedUniqueIndexCatalog? _generationUniqueIndexes;
    private MySqlExpectedUniqueIndexCatalog? _generationFoldedUniqueIndexes;
    private MySqlExpectedUniqueIndexCatalog? _expectedUniqueIndexes;
    private MySqlBackfillConstraintCatalog? _expectedBackfillConstraints;
    private MySqlBackfillConstraintCatalog? _generationBackfillConstraints;
    private MySqlBackfillConstraintCatalog? _generationFoldedBackfillConstraints;
    private bool _generationFoldedTableContractInvalid;
    private MySqlSafeMigrationRuntimePlan?[]? _plans;
    private bool _includeAnalysisEvidence;
    private bool _includeTransitionEvidence;
    private bool _completed;
    private Dictionary<int, MySqlSafeMigrationRuntimeDiagnostic>? _runtimeDiagnostics;
    private int _runtimeDiagnosticCapacity;

    /// <summary>Records authored native-guard context only while the outer generator owns the stream.</summary>
    /// <param name="diagnostic">The exact handler ordinal and bounded correlation.</param>
    internal void RecordRuntimeDiagnostic(MySqlSafeMigrationRuntimeDiagnostic diagnostic)
    {
        if (HasGenerationContract)
        {
            // WHY: The complete authored safe-operation count is already known.
            // Allocate its lookup once, lazily, rather than repeatedly growing
            // a large migration's dictionary or allocating for ordinary EF SQL.
            (_runtimeDiagnostics ??= new Dictionary<int, MySqlSafeMigrationRuntimeDiagnostic>(
                _runtimeDiagnosticCapacity)).Add(diagnostic.Ordinal, diagnostic);
        }
    }

    /// <summary>Finds context only when both the native command marker and its generation correlation agree.</summary>
    /// <param name="commandText">The unchanged original native command text.</param>
    /// <returns>The owning diagnostic, or null for ordinary provider operations.</returns>
    internal MySqlSafeMigrationRuntimeDiagnostic? FindRuntimeDiagnostic(string commandText)
    {
        if (_runtimeDiagnostics is not null
            && MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(commandText, out var ordinal, out var correlation)
            && _runtimeDiagnostics.TryGetValue(ordinal, out var diagnostic)
            && correlation.Equals(diagnostic.Correlation, StringComparison.Ordinal))
        {
            return diagnostic;
        }

        return null;
    }

    /// <summary>Gets whether an incomplete analysis capture is active.</summary>
    public bool IsActive => _expected is not null && !_completed;

    /// <summary>Gets whether runtime SQL generation owns an ordered transition catalog.</summary>
    public bool HasGenerationContract => _generationTableConstraints is not null
        && _generationUniqueIndexes is not null
        && _generationDatabaseQualifiers is not null;

    /// <summary>Gets every explicit database identity required by runtime generation.</summary>
    public IReadOnlyList<string> GenerationDatabaseQualifiers => _generationDatabaseQualifiers
        ?? throw new InvalidOperationException(
            "No MySQL SafeMigrations runtime generation scope is active.");

    /// <summary>Gets whether the active capture requests detailed diagnostic SQL.</summary>
    public bool IncludeAnalysisEvidence => IsActive && _includeAnalysisEvidence;

    /// <summary>Gets whether the active capture requests physical transition SQL.</summary>
    public bool IncludeTransitionEvidence => IsActive && _includeTransitionEvidence;

    /// <summary>Begins an ordered capture for one immutable operation batch.</summary>
    /// <param name="operations">The safe operations expected from Doka's handler pipeline.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        return Begin(
            operations,
            CreateExpectedUniqueIndexes(operations),
            SafeMigrationExpectedTableConstraints.FromOperations(operations),
            includeAnalysisEvidence: false,
            includeTransitionEvidence: false);
    }

    /// <summary>Begins one bounded capture against a complete expected-index catalog.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <param name="includeAnalysisEvidence">Whether to build detailed diagnostic SQL.</param>
    /// <param name="includeTransitionEvidence">Whether to build physical transition SQL.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false
    ) => Begin(
        operations,
        expectedUniqueIndexes,
        SafeMigrationExpectedTableConstraints.FromOperations(operations,
            expectedUniqueIndexes.NormalizeSchema, expectedUniqueIndexes.TableComparer),
        includeAnalysisEvidence,
        includeTransitionEvidence);

    /// <summary>Begins one bounded capture against complete expected table catalogs.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <param name="expectedTableConstraints">The complete migration's table-constraint transition catalog.</param>
    /// <param name="includeAnalysisEvidence">Whether to build detailed diagnostic SQL.</param>
    /// <param name="includeTransitionEvidence">Whether to build physical transition SQL.</param>
    /// <param name="backfillConstraints">Complete-stream constraint hazards retained across bounded windows.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<
            (string? Schema, string Table), SafeMigrationExpectedTableConstraints> expectedTableConstraints,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false,
        MySqlBackfillConstraintCatalog? backfillConstraints = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(expectedUniqueIndexes);
        ArgumentNullException.ThrowIfNull(expectedTableConstraints);

        if (_expected is not null
            || HasGenerationContract)
        {
            throw new InvalidOperationException("A MySQL SafeMigrations plan capture is already active in this scope.");
        }

        _expected = operations.ToArray();
        if (_expected.Any(static operation => operation is null))
        {
            Clear();
            throw new ArgumentException("The operation batch cannot contain null entries.", nameof(operations));
        }

        _plans = new MySqlSafeMigrationRuntimePlan?[_expected.Length];
        _expectedUniqueIndexes = expectedUniqueIndexes;
        _expectedTableConstraints = expectedTableConstraints;
        _expectedBackfillConstraints = backfillConstraints ?? MySqlBackfillConstraintCatalog.Create(operations,
            expectedUniqueIndexes.CurrentDatabase, expectedUniqueIndexes.TableComparer);
        _includeAnalysisEvidence = includeAnalysisEvidence;
        _includeTransitionEvidence = includeTransitionEvidence;

        _completed = false;

        return new Lease(this);
    }

    /// <summary>Begins runtime generation for one complete ordered operation stream.</summary>
    /// <param name="operations">The migration operations passed to the provider generator.</param>
    /// <returns>A lease that clears the transition catalog after generation.</returns>
    public GenerationLease BeginGeneration(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (IsActive
            || HasGenerationContract)
        {
            throw new InvalidOperationException(
                "A MySQL SafeMigrations analysis or generation scope is already active.");
        }

        ClearGeneration();
        var safeOperations = operations.OfType<SafeMigrationOperation>().ToArray();
        _runtimeDiagnosticCapacity = safeOperations.Length;
        var databaseQualifiers = MySqlSafeMigrationCatalogSqlBuilder.GetDatabaseQualifiers(operations);
        var guardedDatabase = databaseQualifiers.Length == 1
            ? databaseQualifiers[0]
            : null;
        var tableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            schema => MySqlTableIdentity.NormalizeDatabase(schema, guardedDatabase));
        var uniqueIndexes = CreateExpectedUniqueIndexes(safeOperations, guardedDatabase);
        var hasNativeAlternatives = (safeOperations.Any(static operation =>
                operation.Intent is EnsureTableIntent { Mode: SafeMigrationTableMode.StrictDefinition })
            || safeOperations.Any(RequiresAlterBackfillContract))
            && HasCaseAmbiguousOwners(safeOperations, guardedDatabase);

        if (hasNativeAlternatives)
        {
            // WHY: Generation cannot read the server profile. Keep both
            // owner interpretations and let guarded native SQL select one;
            // an invalid folded stream must not reject a valid mode-zero one.
            try
            {
                _generationFoldedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
                    operations, schema => MySqlTableIdentity.NormalizeDatabase(schema, guardedDatabase),
                    StringComparer.OrdinalIgnoreCase);

                _generationFoldedUniqueIndexes = CreateExpectedUniqueIndexes(safeOperations, guardedDatabase,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException)
            {
                _generationFoldedTableContractInvalid = true;
            }

            _generationFoldedBackfillConstraints = MySqlBackfillConstraintCatalog.Create(safeOperations,
                guardedDatabase, StringComparer.OrdinalIgnoreCase);
        }

        // WHY: Generation has no connection from which to prove DATABASE().
        // A single candidate may merge with unqualified identities only
        // because every generated SafeMigrations command receives the same
        // runtime guard. Multiple candidates remain distinct and make that
        // shared guard fail before any command mutates either database.
        _generationDatabaseQualifiers = databaseQualifiers;
        _generationTableConstraints = tableConstraints;
        _generationUniqueIndexes = uniqueIndexes;
        _generationBackfillConstraints = MySqlBackfillConstraintCatalog.Create(safeOperations, guardedDatabase);

        return new GenerationLease(this);
    }

    /// <summary>Builds the unique-index catalog shared by every bounded capture window.</summary>
    /// <param name="operations">The complete ordered SafeMigrations operation set.</param>
    /// <param name="currentDatabase">
    /// The selected database whose explicit qualifier is equivalent to an unqualified table,
    /// or null when runtime identity normalization is unavailable.
    /// </param>
    /// <param name="tableComparer">
    /// Explicitly qualified owner equality; connection-free callers retain exact names.
    /// </param>
    /// <returns>The expected unique-index definitions keyed by physical table identity.</returns>
    public static MySqlExpectedUniqueIndexCatalog CreateExpectedUniqueIndexes(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase = null,
        StringComparer? tableComparer = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        var comparer = tableComparer ?? StringComparer.Ordinal;

        // Only EnsureTable consumes this cross-operation catalog. Avoid
        // materializing the complete expected schema for unrelated batches.
        if (!operations.Any(static operation => operation.Intent is EnsureTableIntent))
        {
            SafeMigrationExpectedIndexTransitions.Validate(
                operations,
                schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase), comparer);

            return new MySqlExpectedUniqueIndexCatalog(s_emptyUniqueIndexCatalog, currentDatabase, comparer);
        }

        var definitions = new Dictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>(
            new MySqlTableIdentityComparer(comparer));
        foreach (var table in SafeMigrationExpectedCatalog.Create(
                     operations,
                     schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase), comparer))
        {
            if (table.UniqueIndexes.Count == 0)
            {
                continue;
            }

            var key = MySqlTableIdentity.Create(table.Table, table.Schema, currentDatabase);
            var indexes = table
                .IndexDefinitions
                .Values
                .Where(static index => index.Unique)
                .OrderBy(static index => index.Name, StringComparer.Ordinal)
                .ToArray();

            if (!definitions.TryAdd(key, indexes))
            {
                throw new InvalidOperationException(
                    "The migration contains multiple expected table definitions for one MySQL database object.");
            }
        }

        return new MySqlExpectedUniqueIndexCatalog(definitions, currentDatabase, comparer);
    }

    /// <summary>Identifies newly admitted alterations whose backfill must honor ordered constraints.</summary>
    /// <param name="operation">The operation being captured or rendered.</param>
    /// <returns>Whether declared constraint participation must be retained.</returns>
    internal static bool RequiresAlterBackfillContract(SafeMigrationOperation operation) =>
        operation is
        {
            Policy: SafeMigrationPolicy.RepairIfSafe,
            Intent: AlterColumnIntent
            {
                OldDefinition: { IsNullable: true } source,
                Definition: { IsNullable: false } target,
            },
        }
        && !SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, target)
        && SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(target.DefaultValue);

    /// <summary>Gets whether declared constraints require a NULL-free backfill proof for this operation.</summary>
    /// <param name="operation">The exact operation captured or rendered.</param>
    /// <returns>Whether complete-stream declarations constrain this backfill.</returns>
    public bool RequiresNullFreeBackfill(SafeMigrationOperation operation) =>
        (IsActive ? _expectedBackfillConstraints : _generationBackfillConstraints)
        ?.RequiresNullFreeRows(operation) == true;

    /// <summary>Gets an additional backfill obligation selected only by the actual runtime owner mode.</summary>
    internal string? GetGenerationNullFreeBackfillCondition(SafeMigrationOperation operation) =>
        !IsActive && !RequiresNullFreeBackfill(operation)
            && _generationFoldedBackfillConstraints?.RequiresNullFreeRows(operation) == true
                ? "@@lower_case_table_names IN (1, 2)"
                : null;

    /// <summary>Gets the folded strict-table alternative without assuming the executing server's profile.</summary>
    internal MySqlTableContractAlternative? GetGenerationTableAlternative(EnsureTableIntent intent)
    {
        if (!HasGenerationContract
            || intent.Mode != SafeMigrationTableMode.StrictDefinition)
        {
            return null;
        }

        if (_generationFoldedTableContractInvalid)
        {
            return new MySqlTableContractAlternative(false, s_emptyUniqueIndexes, null);
        }

        return _generationFoldedTableConstraints is null || _generationFoldedUniqueIndexes is null
            ? null
            : new MySqlTableContractAlternative(true,
                _generationFoldedUniqueIndexes.Get(intent.Definition.Table, intent.Definition.Schema)
                    ?? s_emptyUniqueIndexes,
                _generationFoldedTableConstraints.GetValueOrDefault(
                    (intent.Definition.Schema, intent.Definition.Table)));
    }

    /// <summary>Detects mixed spellings which can change a strict-table or backfill owner contract.</summary>
    private static bool HasCaseAmbiguousOwners(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase
    )
    {
        var owners = new Dictionary<MySqlTableIdentity, string>(
            new MySqlTableIdentityComparer(StringComparer.OrdinalIgnoreCase));

        foreach (var operation in operations)
        {
            (string? Table, string? Schema) owner = operation.Intent switch
            {
                EnsureTableIntent value => (value.Definition.Table, value.Definition.Schema),
                DropTableIntent value => (value.Table, value.Schema),
                RenameTableIntent value => (value.Name, value.Schema),
                EnsureColumnIntent value => (value.Table, value.Schema),
                AlterColumnIntent value => (value.Table, value.Schema),
                DropColumnIntent value => (value.Table, value.Schema),
                RenameColumnIntent value => (value.Table, value.Schema),
                EnsureIndexIntent value => (value.Definition.Table, value.Definition.Schema),
                DropIndexIntent value => (value.Table, value.Schema),
                RenameIndexIntent value => (value.Table, value.Schema),
                EnsurePrimaryKeyIntent value => (value.Definition.Table, value.Definition.Schema),
                DropPrimaryKeyIntent value => (value.Table, value.Schema),
                EnsureUniqueConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
                DropUniqueConstraintIntent value => (value.Table, value.Schema),
                EnsureCheckConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
                DropCheckConstraintIntent value => (value.Table, value.Schema),
                EnsureForeignKeyIntent value => (value.Definition.Table, value.Definition.Schema),
                DropForeignKeyIntent value => (value.Table, value.Schema),
                _ => ((string?)null, (string?)null),
            };

            if (owner.Table is not null && Add(owner.Table, owner.Schema))
            {
                return true;
            }

            if (operation.Intent is RenameTableIntent rename
                && Add(rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema))
            {
                return true;
            }

        }

        return false;

        bool Add(string table, string? schema)
        {
            var identity = MySqlTableIdentity.Create(table, schema, currentDatabase);
            if (owners.TryGetValue(identity, out var previous))
            {
                return !StringComparer.Ordinal.Equals(previous, table);
            }

            owners.Add(identity, table);

            return false;
        }
    }

    /// <summary>Gets expected unique-index definitions for a table in the active batch.</summary>
    /// <param name="table">The unqualified MySQL or MariaDB table name.</param>
    /// <param name="schema">The optional database qualifier.</param>
    /// <returns>The expected unique-index definitions, or an empty list.</returns>
    public IReadOnlyList<ExpectedIndexDefinition> GetExpectedUniqueIndexes(
        string table,
        string? schema = null
    )
    {
        if (!IsActive
            || _expectedUniqueIndexes is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        return _expectedUniqueIndexes.Get(table, schema) ?? s_emptyUniqueIndexes;
    }

    /// <summary>Gets the table-constraint transition contract for the active batch.</summary>
    /// <param name="intent">The table operation being planned.</param>
    /// <returns>The transition contract, or null when the batch has no table owner.</returns>
    public SafeMigrationExpectedTableConstraints? GetExpectedTableConstraints(
        EnsureTableIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (!IsActive
            || _expectedTableConstraints is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        return _expectedTableConstraints.GetValueOrDefault(
            (intent.Definition.Schema, intent.Definition.Table));
    }

    /// <summary>Gets the ordered transition contract used by runtime SQL generation.</summary>
    /// <param name="intent">The table operation being generated.</param>
    /// <returns>The transition contract, or null when the stream has no table owner.</returns>
    public SafeMigrationExpectedTableConstraints? GetGenerationTableConstraints(
        EnsureTableIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (_generationTableConstraints is null)
        {
            throw new InvalidOperationException(
                "No MySQL SafeMigrations runtime generation scope is active.");
        }

        return _generationTableConstraints.GetValueOrDefault(
            (intent.Definition.Schema, intent.Definition.Table));
    }

    /// <summary>Gets expected unique indexes for runtime SQL generation.</summary>
    /// <param name="table">The MySQL or MariaDB table name.</param>
    /// <param name="schema">The optional database qualifier.</param>
    /// <returns>The expected unique indexes, or an empty list.</returns>
    public IReadOnlyList<ExpectedIndexDefinition> GetGenerationUniqueIndexes(
        string table,
        string? schema = null
    )
    {
        if (_generationUniqueIndexes is null)
        {
            throw new InvalidOperationException(
                "No MySQL SafeMigrations runtime generation scope is active.");
        }

        return _generationUniqueIndexes.Get(table, schema) ?? s_emptyUniqueIndexes;
    }

    /// <summary>Records the provider plan emitted for one expected operation ordinal.</summary>
    /// <param name="ordinal">The zero-based operation ordinal.</param>
    /// <param name="operation">The exact expected operation instance.</param>
    /// <param name="plan">The emitted runtime catalog plan.</param>
    public void Record(
        int ordinal,
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);

        if (!IsActive
            || _expected is null
            || _plans is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        if ((uint)ordinal >= (uint)_expected.Length)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler produced an invalid capture ordinal.");
        }

        if (!ReferenceEquals(_expected[ordinal], operation))
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler captured a different operation instance.");
        }

        if (_plans[ordinal] is not null)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler captured an operation more than once.");
        }

        _plans[ordinal] = plan;
    }

    private MySqlSafeMigrationRuntimePlan[] Complete()
    {
        if (!IsActive
            || _plans is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        if (_plans.Any(static plan => plan is null))
        {
            throw new InvalidOperationException("The MySQL SafeMigrations handler did not capture every operation.");
        }

        var result = _plans
            .Cast<MySqlSafeMigrationRuntimePlan>()
            .ToArray();

        _completed = true;

        return result;
    }

    private void Clear()
    {
        _expectedBackfillConstraints = null;
        if (_expected is not null)
        {
            Array.Clear(_expected);
        }

        if (_plans is not null)
        {
            Array.Clear(_plans);
        }

        _expected = null;
        _expectedTableConstraints = null;
        _expectedUniqueIndexes = null;
        _plans = null;
        _includeAnalysisEvidence = false;
        _includeTransitionEvidence = false;
        _completed = false;
    }

    private void ClearGeneration()
    {
        _runtimeDiagnostics = null;
        _runtimeDiagnosticCapacity = 0;
        _generationBackfillConstraints = null;
        _generationFoldedBackfillConstraints = null;
        _generationDatabaseQualifiers = null;
        _generationTableConstraints = null;
        _generationFoldedTableConstraints = null;
        _generationUniqueIndexes = null;
        _generationFoldedUniqueIndexes = null;
        _generationFoldedTableContractInvalid = false;
    }

    /// <summary>Owns the lifetime of one active plan capture.</summary>
    internal sealed class Lease : IDisposable
    {
        private MySqlSafeMigrationPlanCapture? _owner;

        /// <summary>Initializes a lease for an active capture owner.</summary>
        /// <param name="owner">The capture owner to complete or clear.</param>
        public Lease(
            MySqlSafeMigrationPlanCapture owner
        )
        {
            _owner = owner;
        }

        /// <summary>Completes the capture and returns every plan in operation order.</summary>
        /// <returns>The complete ordered runtime-plan batch.</returns>
        public MySqlSafeMigrationRuntimePlan[] Complete()
        {
            ObjectDisposedException.ThrowIf(_owner is null, this);

            return _owner.Complete();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner?.Clear();
            _owner = null;
        }
    }

    /// <summary>Owns one runtime SQL-generation transition catalog.</summary>
    internal sealed class GenerationLease : IDisposable
    {
        private MySqlSafeMigrationPlanCapture? _owner;

        /// <summary>Initializes a lease for one runtime generation scope.</summary>
        /// <param name="owner">The generation scope owner.</param>
        public GenerationLease(
            MySqlSafeMigrationPlanCapture owner
        )
        {
            _owner = owner;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner?.ClearGeneration();
            _owner = null;
        }
    }
}

internal sealed class MySqlExpectedUniqueIndexCatalog(
    IReadOnlyDictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>> definitions,
    string? currentDatabase,
    StringComparer? tableComparer = null
)
{
    /// <summary>Gets owner equality explicitly selected by the capture's proven native profile.</summary>
    internal StringComparer TableComparer { get; } = tableComparer ?? StringComparer.Ordinal;

    /// <summary>Gets the independently qualified current database, or null when no such proof is available.</summary>
    internal string? CurrentDatabase => currentDatabase;

    /// <summary>Normalizes only the existing current-database qualifier contract.</summary>
    internal string? NormalizeSchema(string? schema) => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase);

    public IReadOnlyList<ExpectedIndexDefinition>? Get(
        string table,
        string? schema
    ) => definitions.GetValueOrDefault(MySqlTableIdentity.Create(table, schema, currentDatabase));
}

/// <summary>Stores the folded interpretation used only behind a native execution-time mode guard.</summary>
/// <param name="IsValid">Whether the folded ordered stream has a coherent contract.</param>
/// <param name="UniqueIndexes">Unique indexes declared for the folded physical owner.</param>
/// <param name="Constraints">Ordered table constraints declared for that owner.</param>
internal sealed record MySqlTableContractAlternative(
    bool IsValid,
    IReadOnlyList<ExpectedIndexDefinition> UniqueIndexes,
    SafeMigrationExpectedTableConstraints? Constraints
);

/// <summary>Keeps native table equality separate from the existing exact database qualifier contract.</summary>
internal sealed class MySqlTableIdentityComparer(StringComparer tableComparer) : IEqualityComparer<MySqlTableIdentity>
{
    /// <inheritdoc />
    public bool Equals(
        MySqlTableIdentity first,
        MySqlTableIdentity second
    ) =>
        tableComparer.Equals(first.Table, second.Table)
        && StringComparer.Ordinal.Equals(first.Database, second.Database);

    /// <inheritdoc />
    public int GetHashCode(MySqlTableIdentity identity) => HashCode.Combine(tableComparer.GetHashCode(identity.Table),
        identity.Database is null ? 0 : StringComparer.Ordinal.GetHashCode(identity.Database));
}

internal readonly record struct MySqlTableIdentity(
    string Table,
    string? Database
)
{
    public static MySqlTableIdentity Create(
        string table,
        string? database,
        string? currentDatabase
    ) => new(table, NormalizeDatabase(database, currentDatabase));

    public static string? NormalizeDatabase(
        string? database,
        string? currentDatabase
    ) => database is not null && StringComparer.Ordinal.Equals(database, currentDatabase)
        ? null
        : database;
}
