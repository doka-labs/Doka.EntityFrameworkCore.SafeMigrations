namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that runtime repair evidence is evaluated only for an eligible different state.</summary>
public sealed class SqlServerRuntimeRepairEvaluationTests
{
    /// <summary>Policies without repair and plans without proof omit the unused scalar scope entirely.</summary>
    /// <param name="policy">The authored policy governing repair eligibility.</param>
    /// <param name="capability">The provider's independently classified repair capability.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, SafeMigrationRepairCapability.Safe)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, SafeMigrationRepairCapability.None)]
    public void IneligibleRepair_DoesNotBindRepairExpression(
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability capability
    )
    {
        // Arrange
        var operation = CreateOperation(policy);
        var plan = new SqlServerSafeMigrationRuntimePlan("N'matching'", "1", capability,
            "SELECT forbidden_repair_binding") { RequiresDelayedBinding = true };

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));

        // Assert
        Assert.DoesNotContain("forbidden_repair_binding", sql, StringComparison.Ordinal);
        Assert.Contains("SET @doka_repair_ok = 0;", sql, StringComparison.Ordinal);
        Assert.Contains("SET @doka_state", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51005", sql, StringComparison.Ordinal);
    }

    /// <summary>Eligible repair scopes follow state classification and remain behind the Different branch.</summary>
    /// <param name="delayed">Whether the state and repair predicates require dynamic binding.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EligibleRepair_IsGuardedByFreshDifferentState(bool delayed)
    {
        // Arrange
        var operation = CreateOperation(SafeMigrationPolicy.RepairIfSafe);
        var plan = new SqlServerSafeMigrationRuntimePlan("N'different'", "1",
            SafeMigrationRepairCapability.Safe, "CASE WHEN 7 = 7 THEN 1 ELSE 0 END")
        {
            RequiresDelayedBinding = delayed,
        };

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));

        // Assert
        var guard = sql.IndexOf("IF @doka_state = N'different'", StringComparison.Ordinal);
        var proof = sql.IndexOf("CASE WHEN 7 = 7", StringComparison.Ordinal);
        Assert.True(guard > sql.IndexOf("SET @doka_state", StringComparison.Ordinal));
        Assert.True(proof > guard);
        Assert.Contains("ELSE\n    BEGIN\n        SET @doka_repair_ok = 0;", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51001", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51003", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51004", sql, StringComparison.Ordinal);
    }

    /// <summary>Fresh classification follows every safety gate before repair evidence can bind.</summary>
    /// <param name="delayed">Whether the classifier requires a separate binding scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshClassifier_FollowsSafetyGatesBeforeEligibleRepair(bool delayed)
    {
        // Arrange
        var operation = CreateOperation(SafeMigrationPolicy.RepairIfSafe);
        var plan = new SqlServerSafeMigrationRuntimePlan("N'different'", "1",
            SafeMigrationRepairCapability.Safe, "CASE WHEN 9 = 9 THEN 1 ELSE 0 END")
        {
            RequiresDelayedBinding = delayed,
            PrerequisiteExpression = "prerequisite_sentinel",
            StateEvaluationGuardExpression = "permission_sentinel",
            PhysicalTableSupportExpression = "physical_sentinel",
        };

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));

        // Assert
        Assert.Contains("physical_sentinel", sql, StringComparison.Ordinal);
        Assert.Contains("prerequisite_sentinel", sql, StringComparison.Ordinal);
        Assert.Contains("permission_sentinel", sql, StringComparison.Ordinal);
        var classification = sql.IndexOf(delayed ? "EXEC sys.sp_executesql" : "SET @doka_state = (N'different')",
            StringComparison.Ordinal);

        Assert.True(classification > sql.IndexOf("physical_sentinel", StringComparison.Ordinal));
        Assert.True(classification > sql.IndexOf("prerequisite_sentinel", StringComparison.Ordinal));
        Assert.True(classification > sql.IndexOf("permission_sentinel", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("CASE WHEN 9 = 9", StringComparison.Ordinal) > classification);
        Assert.Contains("IF @doka_state = N'different'", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51005", sql, StringComparison.Ordinal);
    }

    /// <summary>Creates a metadata-independent fixture for the policy rendering boundary.</summary>
    private static SafeMigrationOperation CreateOperation(SafeMigrationPolicy policy)
        => new(new EnsureColumnIntent("items", new ExpectedColumnDefinition("Caption", typeof(string), true,
            "nvarchar(80)")), policy);
}
