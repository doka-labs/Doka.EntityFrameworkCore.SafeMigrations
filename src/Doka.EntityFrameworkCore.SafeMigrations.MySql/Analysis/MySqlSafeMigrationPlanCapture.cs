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

    private SafeMigrationOperation[]? _expected;
    private IReadOnlyDictionary<string, IReadOnlyList<ExpectedIndexDefinition>>? _expectedUniqueIndexes;
    private MySqlSafeMigrationRuntimePlan?[]? _plans;
    private bool _completed;

    /// <summary>Gets whether an incomplete analysis capture is active.</summary>
    public bool IsActive => _expected is not null && !_completed;

    /// <summary>Begins an ordered capture for one immutable operation batch.</summary>
    /// <param name="operations">The safe operations expected from Doka's handler pipeline.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations
    ) => Begin(operations, CreateExpectedUniqueIndexes(operations));

    /// <summary>Begins one bounded capture against a complete expected-index catalog.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyDictionary<string, IReadOnlyList<ExpectedIndexDefinition>> expectedUniqueIndexes
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(expectedUniqueIndexes);

        if (_expected is not null)
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

        _completed = false;

        return new Lease(this);
    }

    /// <summary>Builds the unique-index catalog shared by every bounded capture window.</summary>
    /// <param name="operations">The complete ordered SafeMigrations operation set.</param>
    /// <returns>The expected unique-index definitions keyed by unqualified table name.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<ExpectedIndexDefinition>> CreateExpectedUniqueIndexes(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        return SafeMigrationExpectedCatalog
            .Create(operations)
            .Where(static table => table.Schema is null && table.UniqueIndexes.Count > 0)
            .ToDictionary(
                static table => table.Table,
                static table => (IReadOnlyList<ExpectedIndexDefinition>)table
                    .IndexDefinitions
                    .Values
                    .Where(static index => index.Unique)
                    .OrderBy(static index => index.Name, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>Gets expected unique-index definitions for a table in the active batch.</summary>
    /// <param name="table">The unqualified MySQL or MariaDB table name.</param>
    /// <returns>The expected unique-index definitions, or an empty list.</returns>
    public IReadOnlyList<ExpectedIndexDefinition> GetExpectedUniqueIndexes(
        string table
    )
    {
        if (!IsActive
            || _expectedUniqueIndexes is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        return _expectedUniqueIndexes.GetValueOrDefault(table) ?? s_emptyUniqueIndexes;
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
        _expectedUniqueIndexes = null;
        _plans = null;
        _completed = false;
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
}
