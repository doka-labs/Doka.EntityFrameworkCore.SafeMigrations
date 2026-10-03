namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies Boolean collision detection without retaining groups or changing complete guard input.</summary>
public sealed class SqlServerIdentifierCollisionAllocationTests
{
    /// <summary>Retains empty, singleton and identical-name behavior for every physical scope.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RepeatedScope_AllScopeKindsMatchOriginalGrouping(
        int count
    )
    {
        // Arrange
        var cases = Enum.GetValues<SqlServerIdentifierScope>().Select(scope =>
            Enumerable.Repeat(new SqlServerIdentifierReference(scope, "dbo", "items", "same_name"), count)
                .ToArray()).ToArray();

        var expected = cases.Select(OriginalHasRepeatedScope).ToArray();
        var snapshots = cases.Select(static references => references.ToArray()).ToArray();

        // Act
        var actual = cases.Select(static references =>
            SqlServerIdentifierContract.HasRepeatedPhysicalScope(references)).ToArray();

        // Assert
        Assert.Equal(expected, actual);
        Assert.All(actual, result => Assert.Equal(count > 1, result));
        for (var index = 0; index < cases.Length; index++)
        {
            Assert.Equal(snapshots[index], cases[index]);
        }
    }

    /// <summary>
    /// Uses the original case-sensitive tuple identity, including null versus empty table qualifiers.
    /// </summary>
    [Theory]
    [InlineData("dbo", "dbo", "items", "items", true)]
    [InlineData("dbo", "DBO", "items", "items", false)]
    [InlineData("dbo", "dbo", "items", "Items", false)]
    [InlineData("dbo", "dbo", null, null, true)]
    [InlineData("dbo", "dbo", null, "", false)]
    [InlineData("", "dbo", null, null, false)]
    [InlineData("O'Brien", "O'Brien", "\uD83D\uDE80", "\uD83D\uDE80", true)]
    public void RepeatedScope_QualifiersMatchOriginalOrdinalTupleIdentity(
        string firstSchema,
        string secondSchema,
        string? firstTable,
        string? secondTable,
        bool repeated
    )
    {
        // Arrange
        var cases = Enum.GetValues<SqlServerIdentifierScope>().Select(scope => new[]
        {
            new SqlServerIdentifierReference(scope, firstSchema, firstTable, "first_name"),
            new SqlServerIdentifierReference(scope, secondSchema, secondTable, "SECOND_NAME"),
        }).ToArray();

        var expected = cases.Select(OriginalHasRepeatedScope).ToArray();

        // Act
        var actual = cases.Select(static references =>
            SqlServerIdentifierContract.HasRepeatedPhysicalScope(references)).ToArray();

        // Assert
        Assert.Equal(expected, actual);
        Assert.All(actual, result => Assert.Equal(repeated, result));
    }

    /// <summary>Distinct scope kinds do not become one group even when every qualifier and name is identical.</summary>
    [Fact]
    public void RepeatedScope_DifferentKindsRemainDistinct()
    {
        // Arrange
        var references = Enum.GetValues<SqlServerIdentifierScope>().Select(static scope =>
            new SqlServerIdentifierReference(scope, "dbo", null, "shared_name")).ToArray();

        var expected = OriginalHasRepeatedScope(references);

        // Act
        var actual = SqlServerIdentifierContract.HasRepeatedPhysicalScope(references);

        // Assert
        Assert.Equal(expected, actual);
        Assert.False(actual);
    }

    /// <summary>Retains full-group oracle parity when a repeated scope appears early, late or never.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(-1)]
    public void RepeatedScope_EarlyAndLateRepeatsMatchOriginalGrouping(
        int repeatAt
    )
    {
        // Arrange
        var references = Enumerable.Range(0, 1000).Select(static index => new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Column, "schema_" + index.ToString(CultureInfo.InvariantCulture),
            "items", "name_" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        if (repeatAt >= 0)
        {
            references[repeatAt] = references[0] with { Name = "different_name" };
        }

        var expected = OriginalHasRepeatedScope(references);
        var snapshot = references.ToArray();

        // Act
        var actual = SqlServerIdentifierContract.HasRepeatedPhysicalScope(references);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(repeatAt >= 0, actual);
        Assert.Equal(snapshot, references);
    }

    /// <summary>
    /// Invalid names or qualifiers after an early repeated scope still reject before creating temporary SQL.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CollisionGuard_InvalidLaterIdentifierRetainsValidationPrecedence(
        int malformedKind,
        bool throwOnCollision
    )
    {
        // Arrange
        var invalid = malformedKind switch
        {
            0 => "invalid\0name",
            1 => "unpaired\uD800",
            2 => "unpaired\uDC00",
            _ => new string('x', 129),
        };

        var first = new SqlServerIdentifierReference(SqlServerIdentifierScope.Column, "dbo", "items", "first");
        var second = first with { Name = "second" };
        var invalidReferences = new[]
        {
            first with { Name = invalid },
            first with { Schema = invalid },
            first with { Table = invalid },
        };

        var cases = invalidReferences.Select(reference => new[] { first, second, reference }).ToArray();
        var snapshots = cases.Select(static references => references.ToArray()).ToArray();
        var expected = EndGuardReference(throwOnCollision, false);

        // Act
        var results = new List<(IReadOnlyList<string> Commands, string? TemporaryTable)>();
        foreach (var references in cases)
        {
            var commands = SqlServerIdentifierContract.BuildCollisionGuardCommands(
                references, throwOnCollision, out var temporaryTable);

            results.Add((commands, temporaryTable));
        }

        // Assert
        Assert.All(results, result =>
        {
            Assert.Null(result.TemporaryTable);
            Assert.Equal(expected, Assert.Single(result.Commands));
        });

        for (var index = 0; index < cases.Length; index++)
        {
            Assert.Equal(snapshots[index], cases[index]);
        }
    }

    /// <summary>
    /// The SQL-free path still validates every reference and retains its original proof/throw statement.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollisionGuard_UniqueScopeProofRetainsExactOriginalSql(
        bool throwOnCollision
    )
    {
        // Arrange
        var references = Enum.GetValues<SqlServerIdentifierScope>().Select(static scope =>
            new SqlServerIdentifierReference(scope, "dbo", null, "O'Brien \uD83D\uDE80")).ToArray();

        var expected = EndGuardReference(throwOnCollision, true);

        // Act
        var commands = SqlServerIdentifierContract.BuildCollisionGuardCommands(
            references, throwOnCollision, out var temporaryTable);

        // Assert
        Assert.False(OriginalHasRepeatedScope(references));
        Assert.Null(temporaryTable);
        Assert.Equal(expected, Assert.Single(commands));
    }

    /// <summary>
    /// Early detection never truncates or deduplicates the complete ordered guard input or its 512-row chunks.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollisionGuard_EarlyRepeatStillRendersEveryOriginalReference(
        bool throwOnCollision
    )
    {
        // Arrange
        var references = Enumerable.Range(0, 1000).Select(static index => new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Column, "dbo", "O'Brien \uD83D\uDE80",
            "name_" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        references[1] = references[0];
        var snapshot = references.ToArray();

        // Act
        var commands = SqlServerIdentifierContract.BuildCollisionGuardCommands(
            references, throwOnCollision, out var temporaryTable);

        var expectedInserts = references.Chunk(512).Select(chunk => "INSERT INTO " + temporaryTable
            + " VALUES " + string.Join(", ", chunk.Select(RowReference)) + ";").ToArray();

        var actualInserts = commands.Where(static sql => sql.StartsWith("INSERT INTO ", StringComparison.Ordinal))
            .ToArray();

        var lastCommand = commands[commands.Count - 1];

        // Assert
        Assert.True(OriginalHasRepeatedScope(references));
        Assert.NotNull(temporaryTable);
        Assert.Equal(expectedInserts, actualInserts);
        Assert.Equal(2, actualInserts.Length);
        Assert.Contains("DROP TABLE " + temporaryTable + ";", lastCommand, StringComparison.Ordinal);
        Assert.Contains("GROUP BY scope, schema_name, table_name, name", lastCommand, StringComparison.Ordinal);
        Assert.Equal(snapshot, references);
    }

    /// <summary>Counts only repeated-scope detection on a completed 1000-reference workload.</summary>
    [Fact]
    public void RepeatedScope_PreparedThousandReferencesAllocateLessThanOriginalGrouping()
    {
        // Arrange
        const int renderCount = 64;
        var references = Enumerable.Range(0, 1000).Select(static index => new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Column, "dbo", "items",
            "name_" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        var snapshot = references.ToArray();
        Func<bool> current = () => SqlServerIdentifierContract.HasRepeatedPhysicalScope(references);
        Func<bool> original = () => OriginalHasRepeatedScope(references);

        // WHY: Both paths receive the same completed input. Warm the exact
        // grouping and scan paths before measuring per-thread allocations;
        // input preparation and machine-specific timings are not counted.
        _ = current();
        _ = original();

        // Act
        var currentResult = MeasureDetectionAllocations(current, renderCount);
        var originalResult = MeasureDetectionAllocations(original, renderCount);

        // Assert
        Assert.True(currentResult.Repeated);
        Assert.Equal(originalResult.Repeated, currentResult.Repeated);
        Assert.InRange(currentResult.Bytes, 1, originalResult.Bytes - 1);
        Assert.Equal(snapshot, references);
    }

    /// <summary>Rejects an absent reference collection before attempting scope detection.</summary>
    [Fact]
    public void RepeatedScope_NullInputFailsBeforeDetection()
    {
        // Arrange
        IReadOnlyList<SqlServerIdentifierReference>? references = null;

        // Act
        var failure = Record.Exception(() => SqlServerIdentifierContract.HasRepeatedPhysicalScope(references!));

        // Assert
        Assert.Equal("references", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }

    /// <summary>Retains the exact former GroupBy/Skip predicate as an independent detection oracle.</summary>
    private static bool OriginalHasRepeatedScope(
        SqlServerIdentifierReference[] references
    ) => references.GroupBy(static reference => (reference.Scope, reference.Schema, reference.Table))
        .Any(static group => group.Skip(1).Any());

    /// <summary>
    /// Retains the original SQL-free proof and throw templates independently of the production helper.
    /// </summary>
    private static string EndGuardReference(
        bool throwOnCollision,
        bool safe
    ) => "DECLARE @doka_identity_safe int = " + (safe ? "1" : "0") + "; "
        + (throwOnCollision
            ? "IF @doka_identity_safe <> 1 THROW 51002, N'doka_sm_unsupported', 1;"
            : "SELECT @doka_identity_safe;");

    /// <summary>Renders one complete original reference row without relying on production SQL helpers.</summary>
    private static string RowReference(
        SqlServerIdentifierReference reference
    ) => "(" + ((byte)reference.Scope).ToString(CultureInfo.InvariantCulture) + ", "
        + LiteralReference(reference.Schema) + ", " + LiteralReference(reference.Table ?? string.Empty) + ", "
        + LiteralReference(reference.Name) + ")";

    /// <summary>Retains exact ordinal literal quoting for the independent complete-row oracle.</summary>
    private static string LiteralReference(
        string value
    ) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Counts detector allocations on the current thread while retaining the Boolean result.</summary>
    private static (long Bytes, bool Repeated) MeasureDetectionAllocations(
        Func<bool> detect,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var repeated = false;
        for (var index = 0; index < count; index++)
        {
            repeated = detect();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        return (allocated, repeated);
    }
}
