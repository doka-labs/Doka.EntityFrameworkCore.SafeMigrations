using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class MariaDbSafeMigrationPlannerTests
{
    [Fact]
    public void PlanIndex_WithNullExpected_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => MariaDbSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            expected: null!));

        Assert.Equal("expected", exception.ParamName);
    }

    [Fact]
    public void MissingUnfilteredIndex_PlansCreate()
    {
        var decision = MariaDbSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedIndexDefinition("IX_users_email", "users", null, ["email"], Unique: true));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
        Assert.True(decision.ShouldExecute);
    }

    [Fact]
    public void DifferentIndexInRepairMode_IsRejectedInsteadOfPretendingToRepair()
    {
        var decision = MariaDbSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Different,
            new ExpectedIndexDefinition("IX_users_email", "users", null, ["email"], Unique: false));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void FilteredIndex_IsRejectedByProviderVeto()
    {
        var decision = MariaDbSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedIndexDefinition(
                "IX_users_active",
                "users",
                null,
                ["email"],
                Unique: false,
                Filter: "is_active = 1"));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
        Assert.Contains("does not support filtered indexes", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightCreate_DoesNotExecute()
    {
        var decision = MariaDbSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.ThrowIfDifferent, PreflightOnly: true),
            SafeMigrationComparisonState.Missing,
            new ExpectedIndexDefinition("IX_users_email", "users", null, ["email"], Unique: false));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingUniqueConstraint_PlansCreate()
    {
        var decision = SafeMigrationDecisionPlanner.PlanUniqueConstraint(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedUniqueConstraintDefinition("AK_users_email", "users", null, ["email"]));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
    }

    [Fact]
    public void DifferentForeignKeyInRepairMode_IsRejected()
    {
        var decision = SafeMigrationDecisionPlanner.PlanForeignKey(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Different,
            new ExpectedForeignKeyDefinition(
                "FK_users_departments_department_id",
                "users",
                null,
                ["department_id"],
                "departments",
                null,
                ["id"],
                ReferentialAction.NoAction,
                ReferentialAction.Cascade));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingCheckConstraint_Preflight_DoesNotExecute()
    {
        var decision = SafeMigrationDecisionPlanner.PlanCheckConstraint(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible, PreflightOnly: true),
            SafeMigrationComparisonState.Missing,
            new ExpectedCheckConstraintDefinition("CK_users_age", "users", null, "`age` >= 18"));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingNullableColumn_PlansCreate()
    {
        var decision = SafeMigrationDecisionPlanner.PlanColumn(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedColumnDefinition(
                "display_name",
                "varchar(200)",
                IsNullable: true,
                DefaultValueLiteral: null,
                DefaultValueSql: null,
                ComputedColumnSql: null,
                Precision: null,
                Scale: null,
                Collation: null,
                IsStored: null));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.True(decision.ShouldExecute);
    }

    [Fact]
    public void MissingRequiredColumnWithoutDefault_IsRejected()
    {
        var decision = SafeMigrationDecisionPlanner.PlanColumn(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedColumnDefinition(
                "age",
                "int",
                IsNullable: false,
                DefaultValueLiteral: null,
                DefaultValueSql: null,
                ComputedColumnSql: null,
                Precision: null,
                Scale: null,
                Collation: null,
                IsStored: null));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.Contains("Safe additive-column repair is not allowed", decision.Reason, StringComparison.Ordinal);
    }
}
