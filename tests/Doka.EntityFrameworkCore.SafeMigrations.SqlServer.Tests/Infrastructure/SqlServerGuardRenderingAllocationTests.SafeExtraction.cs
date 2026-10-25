namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerGuardRenderingAllocationTests
{
    /// <summary>Retains original references and order while ignoring ordinary, null and design-time entries.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafeExtraction_MixedStreamMatchesOriginalFilterWithoutMutatingInput(
        bool includeSafeOperations
    )
    {
        // Arrange
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            new SqlOperation { Sql = "SELECT 7;" },
            null!,
        };

        if (includeSafeOperations)
        {
            operations.Add(CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false));
        }

        operations.Add(new SqlOperation { Sql = "SELECT 9;" });
        if (includeSafeOperations)
        {
            operations.Add(CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, true));
        }

        var expected = operations.OfType<SafeMigrationOperation>().ToArray();
        var original = operations.ToArray();

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, expected.Length);

        // Assert
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index], actual[index]);
        }

        Assert.Equal(original.Length, operations.Count);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Same(original[index], operations[index]);
        }

        if (!includeSafeOperations)
        {
            Assert.Same(Array.Empty<SafeMigrationOperation>(), actual);
        }
    }

    /// <summary>Returns an independent exact-sized array while retaining the original safe object reference.</summary>
    [Fact]
    public void SafeExtraction_ResultArrayMutationDoesNotChangeSourceStream()
    {
        // Arrange
        var original = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false);
        var replacement = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, true);
        var operations = new List<MigrationOperation> { original };

        // Act
        var extracted = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, 1);
        var reference = extracted[0];
        extracted[0] = replacement;

        // Assert
        Assert.Same(original, reference);
        Assert.Same(original, Assert.Single(operations));
        Assert.Same(replacement, Assert.Single(extracted));
    }

    /// <summary>
    /// Rejects a mismatched preflight count instead of losing operations or returning null array slots.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SafeExtraction_InexactCountFailsClosedWithoutInputMutation(
        int count
    )
    {
        // Arrange
        var safe = CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false);
        var ordinary = new SqlOperation { Sql = "SELECT 7;" };
        var operations = new List<MigrationOperation> { ordinary, safe };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, count));

        // Assert
        Assert.Contains("preflight count", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Same(ordinary, operations[0]);
        Assert.Same(safe, operations[1]);
        Assert.Equal(2, operations.Count);
    }

    /// <summary>Rejects negative and oversized counts before extraction can allocate an invalid output.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void SafeExtraction_OutOfRangeCountFailsBeforeAllocation(
        int count
    )
    {
        // Arrange
        var operations = new List<MigrationOperation> { new SqlOperation { Sql = "SELECT 7;" } };

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, count));

        // Assert
        Assert.Equal("safeOperationCount", Assert.IsType<ArgumentOutOfRangeException>(failure).ParamName);
    }

    /// <summary>
    /// Counts prepared 1000-operation extraction against the original filtering and array growth path.
    /// </summary>
    [Fact]
    public void SafeExtraction_PreparedThousandOperationListAllocatesLessThanOriginalFilter()
    {
        // Arrange
        const int operationCount = 1000;
        const int renderCount = 32;
        var operations = Enumerable.Range(0, operationCount)
            .Select(_ => (MigrationOperation)CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false)).ToList();

        var snapshot = operations.ToArray();
        var currentResult = Array.Empty<SafeMigrationOperation>();
        var originalResult = Array.Empty<SafeMigrationOperation>();

        // WHY: The source list, intents and exact preflight count are prepared
        // before measuring. Only extraction is counted, and both code paths are
        // warmed so first-use initialization cannot masquerade as a saving.
        currentResult = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, operationCount);
        originalResult = operations.OfType<SafeMigrationOperation>().ToArray();

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < renderCount; iteration++)
        {
            currentResult = SqlServerSafeMigrationsSqlGenerator.ExtractSafeOperations(operations, operationCount);
        }

        var currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < renderCount; iteration++)
        {
            originalResult = operations.OfType<SafeMigrationOperation>().ToArray();
        }

        var originalBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.InRange(currentBytes, 1, originalBytes - 1);
        Assert.Equal(operationCount, currentResult.Length);
        Assert.Equal(operationCount, originalResult.Length);
        Assert.Equal(operationCount, operations.Count);
        for (var index = 0; index < operationCount; index++)
        {
            Assert.Same(snapshot[index], currentResult[index]);
            Assert.Same(snapshot[index], originalResult[index]);
            Assert.Same(snapshot[index], operations[index]);
        }
    }

    /// <summary>Preserves a valid marker and the exact position of ordinary commands around a safe operation.</summary>
    [Fact]
    public void SafeExtraction_GenerationRetainsMixedMarkerAndCommandOrder()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            new SqlOperation { Sql = "SELECT 7;" },
            CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false),
            new SqlOperation { Sql = "SELECT 9;" },
        };

        var snapshot = operations.ToArray();

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);

        // Assert
        Assert.Equal(4, commands.Count);
        Assert.StartsWith("EXEC sys.sp_executesql N'", commands[0].CommandText, StringComparison.Ordinal);
        Assert.Contains("SELECT 7;", commands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_state", commands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("SELECT 9;", commands[3].CommandText, StringComparison.Ordinal);
        Assert.All(commands, static command => Assert.IsType<SqlServerSafeMigrationGuardedCommand>(command));
        for (var index = 0; index < snapshot.Length; index++)
        {
            Assert.Same(snapshot[index], operations[index]);
        }
    }

    /// <summary>
    /// Preserves fail-closed unsupported preflight despite a marker and preceding ordinary operation.
    /// </summary>
    [Fact]
    public void SafeExtraction_UnsupportedMixedStreamStillFailsBeforeReturningCommands()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationDesignTimeServicesRequiredOperation());
        builder.Sql("SELECT 7;");
        builder.EnsureIndex(new ExpectedIndexDefinition("IX_unsupported", "items",
            [new ExpectedIndexKeyDefinition("Id")], method: "hash"), SafeMigrationPolicy.ThrowIfDifferent);

        var snapshot = builder.Operations.ToArray();

        // Act
        var failure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model));

        // Assert
        Assert.Contains("unsupported", Assert.IsType<NotSupportedException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(snapshot.Length, builder.Operations.Count);
        for (var index = 0; index < snapshot.Length; index++)
        {
            Assert.Same(snapshot[index], builder.Operations[index]);
        }
    }

    /// <summary>Retains the existing noninitial design-time marker rejection after safe extraction.</summary>
    [Fact]
    public void SafeExtraction_NoninitialMarkerRemainsRejected()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operations = new List<MigrationOperation>
        {
            CreateOperation(SafeMigrationPolicy.ThrowIfDifferent, false),
            new SafeMigrationDesignTimeServicesRequiredOperation(),
        };

        var snapshot = operations.ToArray();

        // Act
        var failure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, context.Model));

        // Assert
        Assert.Contains("first migration operation", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);

        Assert.Same(snapshot[0], operations[0]);
        Assert.Same(snapshot[1], operations[1]);
        Assert.Equal(snapshot.Length, operations.Count);
    }
}
