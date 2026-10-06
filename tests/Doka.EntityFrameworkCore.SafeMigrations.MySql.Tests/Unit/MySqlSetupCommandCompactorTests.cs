namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies bounded setup grouping never changes statement text or trusted execution boundaries.</summary>
public sealed class MySqlSetupCommandCompactorTests
{
    /// <summary>Incomplete owned SQL remains an independent boundary without delimiter rewriting.</summary>
    /// <param name="independent">The fragment that does not end in an owned statement terminator.</param>
    [Theory]
    [InlineData("DO 2")]
    [InlineData("DO 2; -- trailing comment")]
    [InlineData("")]
    [InlineData(" \r\n")]
    public void UnterminatedOwnedFragmentsRemainIndependent(string independent)
    {
        // Arrange
        string[] setup = ["DO 0;", "DO 1;", independent, "DO 3;", "DO 4;"];
        string[] expected = ["DO 0;DO 1;", independent, "DO 3;DO 4;"];

        // Act
        var compacted = Compact(setup, new string(' ', 100) + "DO 0;");

        // Assert
        Assert.Equal(expected, compacted);
        Assert.Same(independent, compacted[1]);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
    }

    /// <summary>Compaction cannot hide blank setup entries from the real provider scope validator.</summary>
    /// <param name="blank">The empty or whitespace-only setup entry.</param>
    /// <param name="deferred">Whether setup uses the deferred-fragment overload.</param>
    [Theory]
    [InlineData("", false)]
    [InlineData(" \r\n", false)]
    [InlineData("", true)]
    [InlineData(" \r\n", true)]
    public void BlankSetupFragmentsRetainProviderRejection(
        string blank,
        bool deferred
    )
    {
        // Arrange
        string[] setup = ["DO 0;", "DO 1;", blank, "DO 3;", "DO 4;"];
        var fragments = setup.Select(static sql => new MySqlSafeMigrationSetupFragment(sql)).ToArray();
        var body = new string(' ', 100) + "DO 0;";
        string[] cleanup = ["DO 0;", "DO 0;"];

        // Act
        var compacted = deferred
            ? MySqlSafeMigrationSetupCommandCompactor.Compact(fragments, body, cleanup, fragments.Length, 0)
            : MySqlSafeMigrationSetupCommandCompactor.Compact(setup, body, cleanup, setup.Length, 0);

        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped(compacted, body, cleanup));

        // Assert
        Assert.Contains(blank, compacted);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
        Assert.Equal("setupCommands", Assert.IsType<ArgumentException>(exception).ParamName);
    }

    /// <summary>Owned setup is combined without adding delimiters or changing statement order.</summary>
    [Fact]
    public void OwnedFragmentsKeepExactTextAndOrder()
    {
        // Arrange
        string[] setup = ["SET @a = 1;", "SET @b = 2;", "SET @c = 3;"];
        var body = new string(' ', 100) + "DO 0;";

        // Act
        var compacted = Compact(setup, body);

        // Assert
        Assert.Equal(string.Concat(setup), Assert.Single(compacted));
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
    }

    /// <summary>No emitted command exceeds the largest original setup/body UTF-8 payload.</summary>
    [Theory]
    [InlineData(10, 2)]
    [InlineData(9, 3)]
    [InlineData(4, 3)]
    public void OriginalPayloadCeilingSplitsGroups(
        int bodyLength,
        int expectedCount
    )
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;", "DO 3;"];
        var body = new string('x', bodyLength);
        var maximumBytes = Math.Max(bodyLength, setup.Max(Encoding.UTF8.GetByteCount));

        // Act
        var compacted = Compact(setup, body);

        // Assert
        Assert.Equal(expectedCount, compacted.Count);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
        Assert.All(compacted, command => Assert.True(Encoding.UTF8.GetByteCount(command) <= maximumBytes));
    }

    /// <summary>Byte accounting, rather than UTF-16 length, controls non-ASCII literal payloads.</summary>
    [Fact]
    public void Utf8ExpansionPreventsUnsafeGrouping()
    {
        // Arrange
        string[] setup = ["DO '\u20ac';", "DO '\u20ac';", "DO 0;"];
        string[] expected = ["DO '\u20ac';", "DO '\u20ac';DO 0;"];

        // Act
        var compacted = Compact(setup, new string(' ', 9) + "DO 0;");

        // Assert
        Assert.Equal(expected, compacted);
        Assert.All(compacted, command => Assert.True(Encoding.UTF8.GetByteCount(command) <= 14));
    }

    /// <summary>Opaque provider setup is never joined or crossed even when a larger group would fit.</summary>
    [Fact]
    public void ProviderFragmentsRemainIndependentBarriers()
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;", "opaque", "provider", "DO 3;", "DO 4;"];
        string[] expected = ["DO 1;DO 2;", "opaque", "provider", "DO 3;DO 4;"];

        // Act
        var compacted = MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, new string('x', 100), ["DO 0;"], providerSetupOffset: 2, providerSetupCount: 2);

        // Assert
        Assert.Equal(expected, compacted);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
    }

    /// <summary>Neither the body nor independent cleanup participates in grouping.</summary>
    [Fact]
    public void BodyAndCleanupAreNotInTheCompactedSequence()
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;"];
        string[] cleanup = ["SELECT missing_cleanup;", "SET @guard = NULL;"];
        var expectedCleanup = cleanup.ToArray();

        // Act
        var compacted = MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, "SELECT original_body;", cleanup, providerSetupOffset: 2, providerSetupCount: 0);

        // Assert
        Assert.Equal("DO 1;DO 2;", Assert.Single(compacted));
        Assert.Equal(expectedCleanup, cleanup);
    }

    /// <summary>Provider scope roles, reverse cleanup and transaction suppression survive grouping.</summary>
    /// <param name="transactionSuppressed">
    /// Whether EF executes the complete scope outside its migration transaction.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactedScopeRetainsBodyCleanupAndTransactionContract(
        bool transactionSuppressed
    )
    {
        // Arrange
        string[] setup = ["SET @first = 1;\n", "SET @second = 2;\n"];
        var body = "SELECT @first, @second;" + new string(' ', 100) + "\n";
        string[] cleanup = ["SET @first = NULL;\n", "SET @second = NULL;\n"];
        var original = MySqlMigrationCommandSpec.CreateScoped(setup, body, cleanup, transactionSuppressed);

        // Act
        var compacted = MySqlSafeMigrationSetupCommandCompactor.Compact(setup, body, cleanup, 2, 0);
        var scope = MySqlMigrationCommandSpec.CreateScoped(compacted, body, cleanup, transactionSuppressed);

        // Assert
        Assert.Equal(original.CommandText, scope.CommandText);
        Assert.Equal(transactionSuppressed, scope.TransactionSuppressed);
        Assert.Single(scope.Fragments, fragment => fragment.Kind == MySqlMigrationCommandFragmentKind.Setup);
        Assert.Single(scope.Fragments, fragment => fragment.Kind == MySqlMigrationCommandFragmentKind.Body);
        Assert.Equal(
            original.Fragments.Where(fragment => fragment.Kind == MySqlMigrationCommandFragmentKind.Cleanup)
                .Select(fragment => fragment.CommandText.ToString()),
            scope.Fragments.Where(fragment => fragment.Kind == MySqlMigrationCommandFragmentKind.Cleanup)
                .Select(fragment => fragment.CommandText.ToString()));
    }

    /// <summary>Scopes exceeding the provider's original fragment count cannot be hidden by compaction.</summary>
    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public void OriginalFragmentBoundIsPreserved(
        int setupCount
    )
    {
        // Arrange
        var setup = Enumerable.Repeat("DO 0;", setupCount).ToArray();

        // Act
        var compacted = Compact(setup, new string('x', 100));
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped(
            compacted, new string('x', 100), ["DO 0;", "DO 0;"]));

        // Assert
        Assert.Same(setup, compacted);
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Scopes exceeding the original SQL length retain the provider's own rejection.</summary>
    [Fact]
    public void OriginalTextBoundIsPreserved()
    {
        // Arrange
        string[] setup = [new string(' ', 1_048_571) + "DO 0;", "DO 0;"];

        // Act
        var compacted = Compact(setup, "DO 0;");
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped(
            compacted, "DO 0;", ["DO 0;", "DO 0;"]));

        // Assert
        Assert.Same(setup, compacted);
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>The provider's maximum accepted original fragment count is still eligible for grouping.</summary>
    [Fact]
    public void ExactOriginalFragmentBoundIsAccepted()
    {
        // Arrange
        var setup = Enumerable.Repeat("DO 0;", 125).ToArray();
        var body = new string('x', 100);

        // Act
        var compacted = Compact(setup, body);
        var specification = MySqlMigrationCommandSpec.CreateScoped(compacted, body, ["DO 0;", "DO 0;"]);

        // Assert
        Assert.True(compacted.Count < setup.Length);
        Assert.Equal(string.Concat(setup) + body + "DO 0;DO 0;", specification.CommandText);
    }

    /// <summary>The exact documented text ceiling remains accepted without changing the payload.</summary>
    [Fact]
    public void ExactOriginalTextBoundIsAccepted()
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;"];
        var body = new string('x', 1_048_576 - 20);

        // Act
        var compacted = Compact(setup, body);
        var scope = MySqlMigrationCommandSpec.CreateScoped(compacted, body, ["DO 0;", "DO 0;"]);

        // Assert
        Assert.Equal(1_048_576, scope.CommandText.Length);
        Assert.Equal(string.Concat(setup) + body + "DO 0;DO 0;", scope.CommandText);
    }

    /// <summary>Directly fused controls still count toward the original provider fragment ceiling.</summary>
    /// <param name="fusedFragments">The controls already included in final assignment allocations.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    public void FusedControlsRetainExactOriginalFragmentAdmission(int fusedFragments)
    {
        // Arrange
        var setup = Enumerable.Repeat("DO 0;", 125 - fusedFragments).ToArray();
        var body = new string('x', 100);

        // Act
        var compacted = MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, body, ["DO 0;", "DO 0;"], setup.Length, 0, fusedFragments);

        var scope = MySqlMigrationCommandSpec.CreateScoped(compacted, body, ["DO 0;", "DO 0;"]);

        // Assert
        Assert.True(compacted.Count < setup.Length);
        Assert.Equal(string.Concat(setup) + body + "DO 0;DO 0;", scope.CommandText);
    }

    /// <summary>A physically small fused scope cannot hide an originally oversized sequence.</summary>
    /// <param name="fusedFragments">The controls removed from the physical fragment list.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void FusedControlsRejectOriginalFragmentOverflow(int fusedFragments)
    {
        // Arrange
        var count = fusedFragments == int.MaxValue ? 1 : 126 - fusedFragments;
        var setup = Enumerable.Repeat("DO 0;", count).ToArray();

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, "DO 0;", ["DO 0;", "DO 0;"], setup.Length, 0, fusedFragments));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Negative fusion accounting cannot weaken the original scope admission.</summary>
    [Fact]
    public void NegativeFusedFragmentCountIsRejected()
    {
        // Arrange
        string[] setup = ["DO 0;"];

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, "DO 0;", ["DO 0;"], 1, 0, fusedSetupFragmentCount: -1));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Fusion preserves the provider's exact full-scope text ceiling and its rejection.</summary>
    /// <param name="textLength">The complete original SQL length, including body and cleanup.</param>
    /// <param name="accepted">Whether the original text fits the provider ceiling.</param>
    [Theory]
    [InlineData(1_048_576, true)]
    [InlineData(1_048_577, false)]
    public void FusedControlsRetainOriginalTextAdmission(int textLength, bool accepted)
    {
        // Arrange
        string[] setup = [new string(' ', textLength - 20) + "DO 0;"];
        MySqlMigrationCommandSpec? scope = null;

        // Act
        var exception = Record.Exception(() =>
        {
            var compacted = MySqlSafeMigrationSetupCommandCompactor.Compact(
                setup, "DO 0;", ["DO 0;", "DO 0;"], 1, 0, fusedSetupFragmentCount: 3);

            scope = MySqlMigrationCommandSpec.CreateScoped(compacted, "DO 0;", ["DO 0;", "DO 0;"]);
        });

        // Assert
        if (accepted)
        {
            Assert.Null(exception);
            Assert.NotNull(scope);
            Assert.Equal(textLength, scope.CommandText.Length);
        }
        else
        {
            Assert.IsType<ArgumentException>(exception);
            Assert.Null(scope);
        }
    }

    /// <summary>Already minimal inputs are returned unchanged without an extra collection allocation.</summary>
    [Fact]
    public void UnchangedGroupingReusesOriginalSequence()
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;"];

        // Act
        var compacted = Compact(setup, "DO 0;");

        // Assert
        Assert.Same(setup, compacted);
    }

    /// <summary>
    /// Large classifiers remain original references while nearby short control fragments are grouped.
    /// </summary>
    [Fact]
    public void LargeOwnedPayloadIsNotCopiedIntoAGroup()
    {
        // Arrange
        var classifier = new string(' ', 4092) + "DO 0;";
        string[] setup = ["DO 1;", "DO 2;", classifier, "DO 3;", "DO 4;"];

        // Act
        var compacted = Compact(setup, new string('x', 1000));

        // Assert
        Assert.Equal(3, compacted.Count);
        Assert.Same(classifier, compacted[1]);
        Assert.Equal("DO 1;DO 2;", compacted[0]);
        Assert.Equal("DO 3;DO 4;", compacted[2]);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
    }

    /// <summary>The small-control copy ceiling is exact and never admits a larger grouped command.</summary>
    [Theory]
    [InlineData(2048, 2)]
    [InlineData(2049, 3)]
    public void SmallControlPayloadCeilingLimitsCopiedBytes(
        int fragmentLength,
        int expectedGroups
    )
    {
        // Arrange
        var fragment = new string(' ', fragmentLength - 5) + "DO 0;";
        string[] setup = [fragment, fragment, fragment];

        // Act
        var compacted = Compact(setup, new string('x', 8192));

        // Assert
        Assert.Equal(expectedGroups, compacted.Count);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
        Assert.All(compacted, command => Assert.True(Encoding.UTF8.GetByteCount(command) <= 4096));
    }

    /// <summary>Multibyte fragments obey the byte ceiling rather than the UTF-16 length fast path.</summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void MultibyteSmallControlPayloadCeilingLimitsCopiedBytes(
        int asciiSuffixLength,
        int expectedGroups
    )
    {
        // Arrange
        var fragment = "DO '" + new string('\u20ac', 680) + new string('x', asciiSuffixLength) + "';";
        string[] setup = [fragment, fragment];

        // Act
        var compacted = Compact(setup, new string('x', 8192));

        // Assert
        Assert.Equal(expectedGroups, compacted.Count);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
        Assert.All(compacted, command => Assert.InRange(Encoding.UTF8.GetByteCount(command), 1, 4096));
    }

    /// <summary>A large UTF-8 payload with short UTF-16 length remains the same original string.</summary>
    [Fact]
    public void LargeMultibyteOwnedPayloadIsNotCopiedIntoAGroup()
    {
        // Arrange
        var classifier = "DO '" + new string('\u20ac', 1363) + "xx';";
        string[] setup = ["DO 1;", "DO 2;", classifier, "DO 3;", "DO 4;"];

        // Act
        var compacted = Compact(setup, new string('x', 1000));

        // Assert
        Assert.Equal(4097, Encoding.UTF8.GetByteCount(classifier));
        Assert.True(classifier.Length < 4096);
        Assert.Equal(3, compacted.Count);
        Assert.Same(classifier, compacted[1]);
        Assert.Equal("DO 1;DO 2;", compacted[0]);
        Assert.Equal("DO 3;DO 4;", compacted[2]);
        Assert.Equal(string.Concat(setup), string.Concat(compacted));
    }

    /// <summary>An invalid opaque range fails rather than silently including provider-owned SQL.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void InvalidProviderRangeIsRejected(
        int offset,
        int count
    )
    {
        // Arrange
        string[] setup = ["DO 1;", "DO 2;"];

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupCommandCompactor.Compact(
            setup, "DO 0;", ["DO 0;"], offset, count));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Uses the real handler's two cleanup fragments without introducing a separate test-only bound.</summary>
    private static IReadOnlyList<string> Compact(
        string[] setup,
        string body
    ) => MySqlSafeMigrationSetupCommandCompactor.Compact(setup, body, ["DO 0;", "DO 0;"], setup.Length, 0);
}
