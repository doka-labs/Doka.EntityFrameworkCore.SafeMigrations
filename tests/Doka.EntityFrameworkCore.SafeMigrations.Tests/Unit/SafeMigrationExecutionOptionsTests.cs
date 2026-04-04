namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class SafeMigrationExecutionOptionsTests
{
    [Fact]
    public void ExecutionOptions_DefaultOptionalValues_AreSafe()
    {
        var options = new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible);

        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, options.ConflictMode);
        Assert.False(options.PreflightOnly);
    }

    [Fact]
    public void ExecutionOptions_PreflightOnly_CanBeSetToTrue()
    {
        var options = new SafeMigrationExecutionOptions(
            SafeMigrationConflictMode.ThrowIfDifferent,
            PreflightOnly: true);

        Assert.Equal(SafeMigrationConflictMode.ThrowIfDifferent, options.ConflictMode);
        Assert.True(options.PreflightOnly);
    }

    [Fact]
    public void ExecutionOptions_RecordEquality_EqualWhenSameValues()
    {
        var a = new SafeMigrationExecutionOptions(SafeMigrationConflictMode.None, PreflightOnly: false);
        var b = new SafeMigrationExecutionOptions(SafeMigrationConflictMode.None, PreflightOnly: false);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void ExecutionOptions_RecordEquality_NotEqualWhenDifferentMode()
    {
        var a = new SafeMigrationExecutionOptions(SafeMigrationConflictMode.None);
        var b = new SafeMigrationExecutionOptions(SafeMigrationConflictMode.ThrowIfDifferent);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void ExecutionOptions_WithExpression_ProducesNonDestructiveCopy()
    {
        var original = new SafeMigrationExecutionOptions(
            SafeMigrationConflictMode.RepairIfPossible,
            PreflightOnly: false);
        var modified = original with { PreflightOnly = true };

        Assert.False(original.PreflightOnly);
        Assert.True(modified.PreflightOnly);
        Assert.Equal(original.ConflictMode, modified.ConflictMode);
        Assert.NotEqual(original, modified);
    }
}
