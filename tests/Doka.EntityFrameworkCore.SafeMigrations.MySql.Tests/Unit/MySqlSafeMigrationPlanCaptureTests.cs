namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationPlanCaptureTests
{
    [Fact]
    public void Capture_PreservesOrdinalAndOperationIdentity()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var first = Operation("first");
        var second = Operation("second");
        var firstPlan = Plan("first");
        var secondPlan = Plan("second");

        using var lease = capture.Begin([first, second]);
        capture.Record(1, second, secondPlan);
        capture.Record(0, first, firstPlan);
        var plans = lease.Complete();

        Assert.Same(firstPlan, plans[0]);
        Assert.Same(secondPlan, plans[1]);
        Assert.False(capture.IsActive);
    }

    [Fact]
    public void Capture_RejectsMissingDuplicateForeignAndOutOfRangeRecords()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");
        var plan = Plan("expected");

        using var lease = capture.Begin([operation]);

        Assert.Throws<InvalidOperationException>(() => lease.Complete());
        Assert.Throws<InvalidOperationException>(() => capture.Record(-1, operation, plan));
        Assert.Throws<InvalidOperationException>(() => capture.Record(1, operation, plan));
        Assert.Throws<InvalidOperationException>(() => capture.Record(0, Operation("foreign"), plan));

        capture.Record(0, operation, plan);

        Assert.Throws<InvalidOperationException>(() => capture.Record(0, operation, plan));
    }

    [Fact]
    public void Capture_DisposeClearsFailedLeaseAndAllowsRecovery()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");

        using (capture.Begin([operation]))
        {
            Assert.True(capture.IsActive);
            Assert.Throws<InvalidOperationException>(() => capture.Begin([operation]));
        }

        using var recovered = capture.Begin([operation]);
        capture.Record(0, operation, Plan("recovered"));

        Assert.Single(recovered.Complete());
    }

    private static SafeMigrationOperation Operation(
        string name
    ) => new(new EnsureSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);

    private static MySqlSafeMigrationRuntimePlan Plan(
        string value
    ) => new(value, value, SafeMigrationRepairCapability.None, value);
}
