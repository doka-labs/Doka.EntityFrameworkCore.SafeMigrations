namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Isolates the diagnostic payload probe that deliberately collects process-wide managed objects.</summary>
[Collection(ProcessWideTestGroup.Name)]
public sealed class MySqlRuntimeDiagnosticsRetentionTests
{
    /// <summary>Generated commands retain identifiers and SQL, not source-managed row matrices.</summary>
    /// <param name="retainSource">The positive control deliberately retaining the original authored operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCommandsReleaseManagedPayloadWithoutLosingUsableContext(bool retainSource)
    {
        // Arrange
        using var context = MySqlRuntimeDiagnosticsTests.OfflineContext();
        var fixture = CreateManagedPayloadRetentionFixture(context, retainSource);

        // Act
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var sourceAlive = fixture.Operation.IsAlive;
        var keysAlive = fixture.Keys.IsAlive;
        var valuesAlive = fixture.Values.IsAlive;
        var cellAlive = fixture.Cell.IsAlive;

        // Assert
        Assert.Equal(retainSource, sourceAlive);
        Assert.Equal(retainSource, keysAlive);
        Assert.Equal(retainSource, valuesAlive);
        Assert.Equal(retainSource, cellAlive);
        var command = Assert.IsType<MySqlSafeMigrationDiagnosticCommand>(Assert.Single(fixture.Commands));
        Assert.Contains("diagnostic_seed_items", command.CommandText, StringComparison.Ordinal);
        Assert.Same(command.OriginalCommand.CommandText, command.CommandText);
        GC.KeepAlive(fixture.RetainedSource);
        GC.KeepAlive(fixture.Commands);
    }

    /// <summary>Keeps the authored source outside the test frame so JIT local lifetime cannot falsify collection.</summary>
    /// <param name="context">The provider service owner, which outlives both generation and collection.</param>
    /// <param name="retainSource">Whether the positive control explicitly retains the original source.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ManagedPayloadRetentionFixture CreateManagedPayloadRetentionFixture(DbContext context, bool retainSource)
    {
        var values = new object?[32, 2];
        for (var row = 0; row < values.GetLength(0); row++)
        {
            values[row, 0] = row;
            values[row, 1] = new byte[256];
        }

        var intent = new EnsureModelManagedDataIntent("diagnostic_seed_items", ["id"], ["int"], ["id", "payload"],
            ["int", "varbinary(256)"], values, schema: null, uniqueKeys: null);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return new ManagedPayloadRetentionFixture(commands, new WeakReference(operation), new WeakReference(intent.KeyValues),
            new WeakReference(intent.Values), new WeakReference(intent.Values.GetUnsafeValue(0, 1)!),
            retainSource ? operation : null);
    }

    /// <summary>Separates live generated commands, weak source probes and an intentional non-vacuous retention control.</summary>
    private sealed record ManagedPayloadRetentionFixture(IReadOnlyList<MigrationCommand> Commands, WeakReference Operation,
        WeakReference Keys, WeakReference Values, WeakReference Cell, SafeMigrationOperation? RetainedSource);
}
