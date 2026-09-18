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
    private IReadOnlyList<string>? _generationDatabaseQualifiers;
    private MySqlExpectedUniqueIndexCatalog? _generationUniqueIndexes;
    private MySqlExpectedUniqueIndexCatalog? _expectedUniqueIndexes;
    private MySqlSafeMigrationRuntimePlan?[]? _plans;
    private bool _includeAnalysisEvidence;
    private bool _includeTransitionEvidence;
    private bool _completed;

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
        SafeMigrationExpectedTableConstraints.FromOperations(operations),
        includeAnalysisEvidence,
        includeTransitionEvidence);

    /// <summary>Begins one bounded capture against complete expected table catalogs.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <param name="expectedTableConstraints">The complete migration's table-constraint transition catalog.</param>
    /// <param name="includeAnalysisEvidence">Whether to build detailed diagnostic SQL.</param>
    /// <param name="includeTransitionEvidence">Whether to build physical transition SQL.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<
            (string? Schema, string Table), SafeMigrationExpectedTableConstraints> expectedTableConstraints,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false
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

        var safeOperations = operations.OfType<SafeMigrationOperation>().ToArray();
        var databaseQualifiers = MySqlSafeMigrationCatalogSqlBuilder.GetDatabaseQualifiers(operations);
        var guardedDatabase = databaseQualifiers.Length == 1
            ? databaseQualifiers[0]
            : null;
        var tableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            schema => MySqlTableIdentity.NormalizeDatabase(schema, guardedDatabase));
        var uniqueIndexes = CreateExpectedUniqueIndexes(safeOperations, guardedDatabase);

        // WHY: Generation has no connection from which to prove DATABASE().
        // A single candidate may merge with unqualified identities only
        // because every generated SafeMigrations command receives the same
        // runtime guard. Multiple candidates remain distinct and make that
        // shared guard fail before any command mutates either database.
        _generationDatabaseQualifiers = databaseQualifiers;
        _generationTableConstraints = tableConstraints;
        _generationUniqueIndexes = uniqueIndexes;

        return new GenerationLease(this);
    }

    /// <summary>Builds the unique-index catalog shared by every bounded capture window.</summary>
    /// <param name="operations">The complete ordered SafeMigrations operation set.</param>
    /// <param name="currentDatabase">
    /// The selected database whose explicit qualifier is equivalent to an unqualified table,
    /// or null when runtime identity normalization is unavailable.
    /// </param>
    /// <returns>The expected unique-index definitions keyed by physical table identity.</returns>
    public static MySqlExpectedUniqueIndexCatalog CreateExpectedUniqueIndexes(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        // Only EnsureTable consumes this cross-operation catalog. Avoid
        // materializing the complete expected schema for unrelated batches.
        if (!operations.Any(static operation => operation.Intent is EnsureTableIntent))
        {
            SafeMigrationExpectedIndexTransitions.Validate(
                operations,
                schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase));

            return new MySqlExpectedUniqueIndexCatalog(s_emptyUniqueIndexCatalog, currentDatabase);
        }

        var definitions = new Dictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>();
        foreach (var table in SafeMigrationExpectedCatalog.Create(
                     operations,
                     schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase)))
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

        return new MySqlExpectedUniqueIndexCatalog(definitions, currentDatabase);
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
        _generationDatabaseQualifiers = null;
        _generationTableConstraints = null;
        _generationUniqueIndexes = null;
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
    string? currentDatabase
)
{
    public IReadOnlyList<ExpectedIndexDefinition>? Get(
        string table,
        string? schema
    ) => definitions.GetValueOrDefault(MySqlTableIdentity.Create(table, schema, currentDatabase));
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
