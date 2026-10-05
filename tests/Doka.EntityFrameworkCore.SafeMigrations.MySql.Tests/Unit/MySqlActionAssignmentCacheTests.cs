namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies bounded pure decision-SQL reuse independently of database state.</summary>
public sealed class MySqlActionAssignmentCacheTests
{
    /// <summary>
    /// Enumerates every valid planner tuple, including policies not normally selected by scaffolding.
    /// </summary>
    /// <returns>The operation kind, policy and repair capability for one canonical assignment.</returns>
    public static IEnumerable<object[]> CanonicalContracts()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
            {
                foreach (var capability in Enum.GetValues<SafeMigrationRepairCapability>())
                {
                    yield return [kind, policy, capability];
                }
            }
        }
    }

    /// <summary>Every cached assignment remains exactly coupled to all canonical planner states.</summary>
    /// <param name="kind">The operation family.</param>
    /// <param name="policy">The conflict policy.</param>
    /// <param name="capability">The structural repair capability.</param>
    [Theory]
    [MemberData(nameof(CanonicalContracts))]
    public void AssignmentMatchesCanonicalPlanner(
        SafeMigrationOperationKind kind,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability capability
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var expected = ExpectedAssignment(kind, policy, capability);

        // Act
        var sql = handler.BuildActionAssignment(kind, policy, capability);
        var repeated = handler.BuildActionAssignment(kind, policy, capability);

        // Assert
        Assert.Equal(expected, sql);
        Assert.Same(sql, repeated);
    }

    /// <summary>Changing any key component prevents stale SQL reuse.</summary>
    /// <param name="component">The one changed input component.</param>
    [Theory]
    [InlineData("kind")]
    [InlineData("policy")]
    [InlineData("capability")]
    public void ChangedContractDoesNotReuseAnotherAssignment(
        string component
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var kind = component == "kind"
            ? SafeMigrationOperationKind.DropColumn
            : SafeMigrationOperationKind.EnsureColumn;

        var policy = component == "policy" ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.RepairIfSafe;
        var capability = component == "capability"
            ? SafeMigrationRepairCapability.None
            : SafeMigrationRepairCapability.Safe;

        var expected = ExpectedAssignment(kind, policy, capability);

        // Act
        var initial = handler.BuildActionAssignment(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationPolicy.RepairIfSafe,
            SafeMigrationRepairCapability.Safe);

        var changed = handler.BuildActionAssignment(kind, policy, capability);

        // Assert
        Assert.NotSame(initial, changed);
        Assert.NotEqual(initial, changed);
        Assert.Equal(expected, changed);
    }

    /// <summary>Returning to an evicted tuple renders it again rather than growing a dictionary.</summary>
    [Fact]
    public void ReplacementRetainsOnlyOneAssignment()
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);

        // Act
        var first = handler.BuildActionAssignment(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationPolicy.ThrowIfDifferent,
            SafeMigrationRepairCapability.None);

        _ = handler.BuildActionAssignment(
            SafeMigrationOperationKind.DropColumn,
            SafeMigrationPolicy.ThrowIfDifferent,
            SafeMigrationRepairCapability.None);

        var returned = handler.BuildActionAssignment(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationPolicy.ThrowIfDifferent,
            SafeMigrationRepairCapability.None);

        // Assert
        Assert.Equal(first, returned);
        Assert.NotSame(first, returned);
    }

    /// <summary>Invalid inputs fail canonically without replacing the previous valid immutable entry.</summary>
    /// <param name="component">The one invalid enum component.</param>
    [Theory]
    [InlineData("kind")]
    [InlineData("policy")]
    [InlineData("capability")]
    public void InvalidContractDoesNotPoisonPreviousAssignment(
        string component
    )
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var kind = component == "kind" ? (SafeMigrationOperationKind)(-1) : SafeMigrationOperationKind.EnsureColumn;
        var policy = component == "policy" ? (SafeMigrationPolicy)(-1) : SafeMigrationPolicy.ThrowIfDifferent;
        var capability = component == "capability"
            ? (SafeMigrationRepairCapability)(-1)
            : SafeMigrationRepairCapability.None;

        // Act
        var initial = handler.BuildActionAssignment(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationPolicy.ThrowIfDifferent,
            SafeMigrationRepairCapability.None);

        var exception = Record.Exception(() => handler.BuildActionAssignment(kind, policy, capability));
        var recovered = handler.BuildActionAssignment(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationPolicy.ThrowIfDifferent,
            SafeMigrationRepairCapability.None);

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Same(initial, recovered);
    }

    /// <summary>Concurrent pure-cache reads never combine one key with another immutable assignment.</summary>
    [Fact]
    public void ConcurrentPureAssignmentsRetainExactContract()
    {
        // Arrange
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var contracts = CanonicalContracts().ToArray();
        var expected = contracts.Select(contract => ExpectedAssignment(
            (SafeMigrationOperationKind)contract[0],
            (SafeMigrationPolicy)contract[1],
            (SafeMigrationRepairCapability)contract[2])).ToArray();

        var results = new string[512];

        // Act
        // WHY: Only the pure assignment accessor is shared; concurrent DbContext/generator use is not supported.
        Parallel.For(0, results.Length, index =>
        {
            var contract = contracts[index % contracts.Length];
            results[index] = handler.BuildActionAssignment(
                (SafeMigrationOperationKind)contract[0],
                (SafeMigrationPolicy)contract[1],
                (SafeMigrationRepairCapability)contract[2]);
        });

        // Assert
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(expected[index % expected.Length], results[index]);
        }
    }

    /// <summary>Creates the real scoped handler dependencies without connecting to any database.</summary>
    private static MySqlSafeMigrationOperationHandler CreateHandler(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>(),
        context.GetService<MySqlSafeMigrationPlanCapture>(),
        context.GetService<IDesignTimeModel>());

    /// <summary>Creates an isolated provider service scope for the pure SQL tests.</summary>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder()
            .UseMySql(
                "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }

    /// <summary>Derives every SQL arm independently from the canonical provider-neutral decision table.</summary>
    private static string ExpectedAssignment(
        SafeMigrationOperationKind kind,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability capability
    )
    {
        var builder = new StringBuilder("SET @doka_sm_action = CASE @doka_sm_state ");
        foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
        {
            var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy, capability);
            var stateCode = state switch
            {
                SafeMigrationObservedState.Missing => "missing",
                SafeMigrationObservedState.Matching => "matching",
                SafeMigrationObservedState.Different => "different",
                SafeMigrationObservedState.Unsupported => "unsupported",
                SafeMigrationObservedState.DataBlocked => "data_blocked",
                SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
                SafeMigrationObservedState.TransitionReady => "transition_ready",
                _ => throw new InvalidOperationException("The canonical state requires an explicit SQL arm."),
            };

            var action = decision.Action switch
            {
                SafeMigrationAction.Apply => "'apply'",
                SafeMigrationAction.NoOp => "'no_op'",
                SafeMigrationAction.Repair => "CASE WHEN COALESCE(@doka_sm_repair_ok, FALSE) "
                    + "THEN 'repair' ELSE 'reject_different' END",
                SafeMigrationAction.RejectDifferent => "'reject_different'",
                SafeMigrationAction.RejectUnsupported => "'reject_unsupported'",
                SafeMigrationAction.RejectDataBlocked => "'reject_data_blocked'",
                SafeMigrationAction.RejectPrerequisiteMissing => "'reject_prerequisite_missing'",
                _ => throw new InvalidOperationException("The canonical action requires an explicit SQL arm."),
            };

            builder.Append("WHEN '").Append(stateCode).Append("' THEN ").Append(action).Append(' ');
        }

        return builder.Append("ELSE 'reject_unsupported' END;").ToString();
    }
}
