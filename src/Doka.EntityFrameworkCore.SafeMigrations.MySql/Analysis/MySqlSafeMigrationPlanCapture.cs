namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationPlanCapture
{
    private SafeMigrationOperation[]? _expected;
    private MySqlSafeMigrationRuntimePlan?[]? _plans;
    private bool _completed;

    public bool IsActive => _expected is not null && !_completed;

    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

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
        _completed = false;

        return new Lease(this);
    }

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
        _plans = null;
        _completed = false;
    }

    internal sealed class Lease : IDisposable
    {
        private MySqlSafeMigrationPlanCapture? _owner;

        public Lease(
            MySqlSafeMigrationPlanCapture owner
        )
        {
            _owner = owner;
        }

        public MySqlSafeMigrationRuntimePlan[] Complete()
        {
            ObjectDisposedException.ThrowIf(_owner is null, this);

            return _owner.Complete();
        }

        public void Dispose()
        {
            _owner?.Clear();
            _owner = null;
        }
    }
}
