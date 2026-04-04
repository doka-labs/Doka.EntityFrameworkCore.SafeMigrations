using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class PostgreSqlSafeMigrationPlannerTests
{
    [Fact]
    public void MissingFilteredIndex_PlansCreate()
    {
        var decision = PostgreSqlSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedIndexDefinition(
                "IX_users_active",
                "users",
                "public",
                ["email"],
                Unique: false,
                Filter: "is_active = true"));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
        Assert.True(decision.ShouldExecute);
    }

    [Fact]
    public void DifferentIndexInRepairMode_IsRejectedInsteadOfPretendingToRepair()
    {
        var decision = PostgreSqlSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Different,
            new ExpectedIndexDefinition("IX_users_email", "users", "public", ["email"], Unique: false));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void PreflightCreate_DoesNotExecute()
    {
        var decision = PostgreSqlSafeMigrationPlanner.PlanIndex(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible, PreflightOnly: true),
            SafeMigrationComparisonState.Missing,
            new ExpectedIndexDefinition("IX_users_email", "users", "public", ["email"], Unique: true));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingUniqueConstraint_PlansCreate()
    {
        var decision = SafeMigrationDecisionPlanner.PlanUniqueConstraint(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedUniqueConstraintDefinition("AK_users_email", "users", "public", ["email"]));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
    }

    [Fact]
    public void DifferentCheckConstraintInRepairMode_IsRejected()
    {
        var decision = SafeMigrationDecisionPlanner.PlanCheckConstraint(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Different,
            new ExpectedCheckConstraintDefinition("CK_users_age", "users", "public", "\"age\" >= 18"));

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingForeignKey_Preflight_DoesNotExecute()
    {
        var decision = SafeMigrationDecisionPlanner.PlanForeignKey(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible, PreflightOnly: true),
            SafeMigrationComparisonState.Missing,
            new ExpectedForeignKeyDefinition(
                "FK_users_departments_department_id",
                "users",
                "public",
                ["department_id"],
                "departments",
                "public",
                ["id"],
                ReferentialAction.NoAction,
                ReferentialAction.Cascade));

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void MissingColumnWithDefault_PlansCreate()
    {
        var decision = SafeMigrationDecisionPlanner.PlanColumn(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Missing,
            new ExpectedColumnDefinition(
                "created_at_utc",
                "timestamp without time zone",
                IsNullable: false,
                DefaultValueLiteral: null,
                DefaultValueSql: "CURRENT_TIMESTAMP",
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
                "integer",
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
