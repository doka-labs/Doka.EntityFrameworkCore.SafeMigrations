namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies collectible diagnostic payloads without concurrent process-wide collection.</summary>
// WHY: The payload-lifetime probe forces full process collections that can disturb other allocation contracts.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationRuntimeDiagnosticContextGarbageCollectionTests
{
    /// <summary>The context can stay live while model-managed payload values become collectible.</summary>
    [Fact]
    public void CapturedContextDoesNotRetainModelManagedValue()
    {
        // Arrange
        var (context, payload) = CaptureWithPayloadReference();

        // Act
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retained = payload.TryGetTarget(out _);
        var message = SafeMigrationDiagnosticMessages.FormatCapturedRuntime(context, "doka_sm_data_blocked");

        // Assert
        Assert.False(retained);
        Assert.Contains("owner=<unqualified>.[Items]", message, StringComparison.Ordinal);
        Assert.Contains("columns=([Id],[Value])", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('s', 128), message, StringComparison.Ordinal);
    }

    // WHY: Isolate the original operation and payload in a non-inlined frame so a conservative
    // JIT stack lifetime cannot masquerade as diagnostic metadata retaining a seed value.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (SafeMigrationRuntimeDiagnosticContext Context, WeakReference<string> Payload)
        CaptureWithPayloadReference()
    {
        var payload = new string('s', 4096);
        var operation = new SafeMigrationOperation(new EnsureModelManagedDataIntent("Items", ["Id"], ["int"],
            ["Id", "Value"], ["int", "text"], new object?[,] { { 1, payload } }, null, null),
            SafeMigrationPolicy.ThrowIfDifferent);

        return (SafeMigrationRuntimeDiagnosticContext.Create(operation), new WeakReference<string>(payload));
    }
}
