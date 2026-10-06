namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies deferred setup materialization retains exact SQL and transport boundaries.</summary>
public sealed class MySqlDeferredSetupFragmentTests
{
    /// <summary>Suffix inspection uses emitted parts and leaves whitespace bytes unchanged.</summary>
    /// <param name="hexadecimalSuffix">Whether encoded bytes follow the raw terminator.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredTerminatorInspectionUsesTheEmittedSuffix(bool hexadecimalSuffix)
    {
        // Arrange
        var suffix = hexadecimalSuffix
            ? MySqlSafeMigrationSetupFragment.Part.Hex(";")
            : (MySqlSafeMigrationSetupFragment.Part)" \r\n";

        var fragment = MySqlSafeMigrationSetupFragment.Compose("DO 0;", suffix, string.Empty, "\t");
        var expected = hexadecimalSuffix ? "DO 0;3B\t" : "DO 0; \r\n\t";

        // Act
        var terminated = fragment.EndsWithStatementTerminator;
        var rendered = fragment.ToSql();

        // Assert
        Assert.Equal(!hexadecimalSuffix, terminated);
        Assert.Equal(expected, rendered);
    }

    /// <summary>Every storage path fills only its own destination region and preserves caller padding.</summary>
    /// <param name="length">The fragment length covering empty, materialized, deferred, and eager paths.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(4096)]
    public void WriteToPreservesPaddingAcrossStoragePaths(int length)
    {
        // Arrange
        var expected = new string('x', length);
        var fragment = SplitRaw(expected);
        var destination = Enumerable.Repeat('#', length + 7).ToArray();

        // Act
        fragment.WriteTo(destination.AsSpan(2));

        // Assert
        Assert.Equal(expected, new string(destination, 2, length));
        Assert.All(destination.AsSpan(0, 2).ToArray(), character => Assert.Equal('#', character));
        Assert.All(destination.AsSpan(length + 2).ToArray(), character => Assert.Equal('#', character));
    }

    /// <summary>
    /// Deferred pieces preserve independent statement boundaries without materializing for inspection.
    /// </summary>
    /// <param name="independent">The incomplete SQL that must not be joined to either neighbor.</param>
    [Theory]
    [InlineData("DO 2")]
    [InlineData("DO 2; -- trailing comment")]
    [InlineData("")]
    [InlineData(" \r\n")]
    public void UnterminatedDeferredFragmentsRemainIndependent(string independent)
    {
        // Arrange
        MySqlSafeMigrationSetupFragment[] setup =
        [
            SplitRaw("DO 0;"), SplitRaw("DO 1;"), SplitRaw(independent),
            SplitRaw("DO 3;"), SplitRaw("DO 4;"),
        ];

        string[] expected = ["DO 0;DO 1;", independent, "DO 3;DO 4;"];

        // Act
        var compacted = Compact(setup, new string(' ', 100) + "DO 0;");

        // Assert
        Assert.Equal(expected, compacted);
    }

    /// <summary>Already materialized SQL remains the original string without a second copy.</summary>
    [Fact]
    public void MaterializedFragmentReusesOriginalString()
    {
        // Arrange
        var sql = new string('x', 64);
        var fragment = new MySqlSafeMigrationSetupFragment(sql);

        // Act
        var actual = fragment.ToSql();

        // Assert
        Assert.Same(sql, actual);
        Assert.Equal(sql.Length, fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(sql), fragment.Utf8ByteCount);
    }

    /// <summary>Removing empty pieces retains the single remaining raw string by identity.</summary>
    [Fact]
    public void SingleRawPartWithEmptyPiecesReusesOriginalString()
    {
        // Arrange
        var sql = new string('x', 64);

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            string.Empty, sql, MySqlSafeMigrationSetupFragment.Part.Hex(string.Empty), string.Empty);

        // Assert
        Assert.Same(sql, fragment.ToSql());
        Assert.Equal(sql.Length, fragment.Length);
        Assert.Equal(sql.Length, fragment.Utf8ByteCount);
    }

    /// <summary>Raw pieces are encoded as one SQL string, including surrogate pairs split across pieces.</summary>
    /// <param name="caseId">The raw text pair constructed without attribute-string serialization.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RawCompositionPreservesTextAndUtf8Accounting(int caseId)
    {
        // Arrange
        // WHY: Attribute/runner serialization normalizes malformed UTF-16 and can collapse distinct
        // theory rows. Case IDs keep discovery distinct while local strings retain exact code units.
        var (first, second) = caseId switch
        {
            0 => ("SET @a = ", "1;"),
            1 => ("\u20ac", "\u00e4"),
            2 => ("\ud83d", "\ude00"),
            3 => ("x\ud83d", "\ude00y"),
            4 => ("\ud83d", "x"),
            5 => ("x", "\ude00"),
            6 => ("\ud83d", "\ud83d"),
            7 => ("", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };

        var expected = first + second;

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            first, string.Empty, MySqlSafeMigrationSetupFragment.Part.Hex(string.Empty), second);

        var actual = fragment.ToSql();

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), fragment.Utf8ByteCount);
    }

    /// <summary>Deferred hexadecimal output matches the established UTF-8 encoder for every source shape.</summary>
    /// <param name="caseId">The source text constructed without attribute-string serialization.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void HexCompositionPreservesIndependentSourceEncoding(int caseId)
    {
        // Arrange
        // WHY: Construct unpaired surrogates locally so discovery cannot normalize high and low
        // surrogate cases to the same replacement-string value and silently omit one test.
        var source = caseId switch
        {
            0 => "",
            1 => "SELECT 'value';",
            2 => "SELECT '\u20ac\u00e4';",
            3 => "\ud83d\ude00",
            4 => "\ud83d",
            5 => "\ude00",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };

        var expected = "SET @sql = 0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(source)) + ";";

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            "SET @sql = 0x", MySqlSafeMigrationSetupFragment.Part.Hex(source), ";");

        var actual = fragment.ToSql();

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), fragment.Utf8ByteCount);
    }

    /// <summary>Separate encoded SQL sources must not create a surrogate pair across the source boundary.</summary>
    [Fact]
    public void SeparateHexSourcesDoNotJoinSurrogatePairs()
    {
        // Arrange
        var expected = Convert.ToHexString(Encoding.UTF8.GetBytes("\ud83d"))
                       + Convert.ToHexString(Encoding.UTF8.GetBytes("\ude00"));

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            MySqlSafeMigrationSetupFragment.Part.Hex("\ud83d"),
            MySqlSafeMigrationSetupFragment.Part.Hex("\ude00"));

        // Assert
        Assert.Equal(expected, fragment.ToSql());
        Assert.Equal(expected.Length, fragment.Length);
        Assert.Equal(expected.Length, fragment.Utf8ByteCount);
    }

    /// <summary>An encoded nonempty piece interrupts raw surrogate adjacency.</summary>
    [Fact]
    public void NonemptyHexPartSeparatesRawSurrogatePieces()
    {
        // Arrange
        var expected = "\ud83d41\ude00";

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            "\ud83d", MySqlSafeMigrationSetupFragment.Part.Hex("A"), "\ude00");

        // Assert
        Assert.Equal(expected, fragment.ToSql());
        Assert.Equal(expected.Length, fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), fragment.Utf8ByteCount);
    }

    /// <summary>Later caller mutation cannot change deferred SQL or its cached size.</summary>
    [Fact]
    public void CompositionSnapshotsCallerOwnedParts()
    {
        // Arrange
        MySqlSafeMigrationSetupFragment.Part[] parts =
        [
            "SET @sql = 0x", MySqlSafeMigrationSetupFragment.Part.Hex("SELECT 1;"), ";",
        ];

        var expected = "SET @sql = 0x" + Convert.ToHexString(Encoding.UTF8.GetBytes("SELECT 1;")) + ";";

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(parts);
        Array.Fill(parts, (MySqlSafeMigrationSetupFragment.Part)"mutated");
        var actual = fragment.ToSql();

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Length, fragment.Length);
        Assert.Equal(expected.Length, fragment.Utf8ByteCount);
    }

    /// <summary>Large raw and encoded compositions preserve text and UTF-8 size when materialized immediately.</summary>
    /// <param name="length">The complete UTF-16 fragment length at or above the grouping ceiling.</param>
    [Theory]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    public void LargeMixedCompositionPreservesExactSqlAndUtf8Size(int length)
    {
        // Arrange
        const string prefix = "/* \u20ac */ SET @sql = 0x";
        const string source = "SELECT '\u00e4\ud83d\ude00';";
        var hexadecimal = Convert.ToHexString(Encoding.UTF8.GetBytes(source));
        var suffix = ";" + new string(' ', length - prefix.Length - hexadecimal.Length - 1);
        var expected = prefix + hexadecimal + suffix;

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            prefix, MySqlSafeMigrationSetupFragment.Part.Hex(source), suffix);

        var actual = fragment.ToSql();

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(length, fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), fragment.Utf8ByteCount);
    }

    /// <summary>Large compositions reuse one immutable materialization after the caller changes its descriptors.</summary>
    /// <param name="length">The complete fragment length that cannot be copied into a larger group.</param>
    [Theory]
    [InlineData(4096)]
    [InlineData(4097)]
    public void LargeCompositionReusesMaterializedStringAfterCallerMutation(int length)
    {
        // Arrange
        var prefix = new string('x', length - 5);
        MySqlSafeMigrationSetupFragment.Part[] parts =
        [
            prefix, MySqlSafeMigrationSetupFragment.Part.Hex("AB"), ";",
        ];

        var expected = prefix + "4142;";

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(parts);
        var first = fragment.ToSql();
        Array.Fill(parts, (MySqlSafeMigrationSetupFragment.Part)"changed");
        var second = fragment.ToSql();

        // Assert
        Assert.Equal(expected, second);
        Assert.Same(first, second);
        Assert.Equal(length, fragment.Length);
        Assert.Equal(length, fragment.Utf8ByteCount);
    }

    /// <summary>Writing into a destination slice preserves surrounding characters and exact SQL text.</summary>
    /// <param name="spareLength">The unused destination suffix length.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void WriteToPreservesSurroundingDestination(int spareLength)
    {
        // Arrange
        var fragment = MySqlSafeMigrationSetupFragment.Compose(
            "SET @sql = 0x", MySqlSafeMigrationSetupFragment.Part.Hex("SELECT '\u20ac';"), ";");

        var expected = "SET @sql = 0x" + Convert.ToHexString(Encoding.UTF8.GetBytes("SELECT '\u20ac';")) + ";";
        var destination = Enumerable.Repeat('#', expected.Length + spareLength + 1).ToArray();

        // Act
        fragment.WriteTo(destination.AsSpan(1));

        // Assert
        Assert.Equal('#', destination[0]);
        Assert.Equal(expected, new string(destination, 1, expected.Length));
        Assert.All(destination.Skip(expected.Length + 1), value => Assert.Equal('#', value));
    }

    /// <summary>An undersized destination is rejected instead of truncating SQL.</summary>
    [Fact]
    public void WriteToRejectsInsufficientDestination()
    {
        // Arrange
        var fragment = MySqlSafeMigrationSetupFragment.Compose("SELECT ", "1;");
        var destination = new char[fragment.Length - 1];

        // Act
        var exception = Record.Exception(() => fragment.WriteTo(destination));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>An empty composition remains a valid empty fragment rather than an uninitialized value.</summary>
    [Fact]
    public void EmptyCompositionIsInitialized()
    {
        // Arrange
        MySqlSafeMigrationSetupFragment.Part[] parts = [];

        // Act
        var fragment = MySqlSafeMigrationSetupFragment.Compose(parts);

        // Assert
        Assert.Same(string.Empty, fragment.ToSql());
        Assert.Equal(0, fragment.Length);
        Assert.Equal(0, fragment.Utf8ByteCount);
    }

    /// <summary>Uninitialized values fail closed at every readable or writable surface.</summary>
    /// <param name="member">The member selected for the invalid-value probe.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DefaultFragmentIsRejected(int member)
    {
        // Arrange
        var fragment = default(MySqlSafeMigrationSetupFragment);

        // Act
        var exception = Record.Exception(() =>
        {
            switch (member)
            {
                case 0:
                    _ = fragment.Length;
                    break;
                case 1:
                    _ = fragment.Utf8ByteCount;
                    break;
                case 2:
                    _ = fragment.ToSql();
                    break;
                case 3:
                    fragment.WriteTo([]);
                    break;
                case 4:
                    _ = fragment.EndsWithStatementTerminator;
                    break;
                default:
                    throw new UnreachableException();
            }
        });

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>An invalid part cannot be discarded as though it were an explicitly empty piece.</summary>
    [Fact]
    public void DefaultCompositionPartIsRejected()
    {
        // Arrange
        MySqlSafeMigrationSetupFragment.Part[] parts = ["SELECT ", default, "1;"];

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupFragment.Compose(parts));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Every string entry point rejects null without creating an invalid deferred value.</summary>
    /// <param name="entryPoint">The constructor or conversion selected for the null probe.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NullSourceIsRejected(int entryPoint)
    {
        // Arrange
        string source = null!;

        // Act
        var exception = Record.Exception(() =>
        {
            switch (entryPoint)
            {
                case 0:
                    _ = new MySqlSafeMigrationSetupFragment(source);
                    break;
                case 1:
                    _ = (MySqlSafeMigrationSetupFragment)source;
                    break;
                case 2:
                    _ = (MySqlSafeMigrationSetupFragment.Part)source;
                    break;
                case 3:
                    _ = MySqlSafeMigrationSetupFragment.Part.Hex(source);
                    break;
                default:
                    throw new UnreachableException();
            }
        });

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>The deferred and materialized paths form the same groups at the fixed UTF-8 ceiling.</summary>
    /// <param name="euroCount">The number of three-byte characters in each fragment.</param>
    /// <param name="asciiCount">The ASCII suffix length in each fragment.</param>
    /// <param name="expectedGroups">The expected group count at the exact boundary.</param>
    [Theory]
    [InlineData(0, 2042, 1)]
    [InlineData(0, 2043, 2)]
    [InlineData(680, 2, 1)]
    [InlineData(680, 3, 2)]
    public void DeferredGroupingMatchesMaterializedUtf8Boundary(
        int euroCount,
        int asciiCount,
        int expectedGroups
    )
    {
        // Arrange
        var raw = "DO '" + new string('\u20ac', euroCount) + new string('x', asciiCount) + "';";
        string[] materialized = [raw, raw];
        MySqlSafeMigrationSetupFragment[] deferred = [SplitRaw(raw), SplitRaw(raw)];
        var body = new string('x', 8192);
        var expected = Compact(materialized, body);

        // Act
        var actual = Compact(deferred, body);

        // Assert
        Assert.Equal(expectedGroups, actual.Count);
        Assert.Equal(expected, actual);
        Assert.All(actual, command => Assert.True(Encoding.UTF8.GetByteCount(command) <= 4096));
    }

    /// <summary>Changing composition strategy at the copy ceiling does not change final group boundaries.</summary>
    /// <param name="length">The composed fragment length immediately around the copy ceiling.</param>
    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void LargeCompositionBoundaryPreservesMaterializedGrouping(int length)
    {
        // Arrange
        var prefix = new string(' ', length - 11) + "DO X'";
        string[] materialized = [prefix + "4142';", "DO 1;", "DO 2;"];
        MySqlSafeMigrationSetupFragment[] deferred =
        [
            MySqlSafeMigrationSetupFragment.Compose(prefix, MySqlSafeMigrationSetupFragment.Part.Hex("AB"), "';"),
            "DO 1;", SplitRaw("DO 2;"),
        ];

        var body = new string('x', 8192);
        var expected = Compact(materialized, body);

        // Act
        var actual = Compact(deferred, body);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(2, actual.Count);
        Assert.Equal(length, actual[0].Length);
        Assert.Equal(string.Concat(materialized), string.Concat(actual));
    }

    /// <summary>A surrogate pair split inside a deferred fragment does not inflate its transport size.</summary>
    [Fact]
    public void DeferredGroupingUsesCombinedSurrogateByteCount()
    {
        // Arrange
        MySqlSafeMigrationSetupFragment[] deferred =
        [
            MySqlSafeMigrationSetupFragment.Compose("DO '\ud83d", string.Empty, "\ude00';"),
            MySqlSafeMigrationSetupFragment.Compose("D", "O 0;"),
        ];

        string[] materialized = ["DO '\ud83d\ude00';", "DO 0;"];
        var expected = Compact(materialized, new string('x', 15));

        // Act
        var actual = Compact(deferred, new string('x', 15));

        // Assert
        Assert.Single(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(15, Encoding.UTF8.GetByteCount(actual[0]));
    }

    /// <summary>The original payload ceiling still wins over the larger fixed grouping limit.</summary>
    /// <param name="bodyLength">The original body payload length.</param>
    /// <param name="expectedGroups">The expected number of independent setup groups.</param>
    [Theory]
    [InlineData(10, 2)]
    [InlineData(9, 3)]
    [InlineData(4, 3)]
    public void DeferredGroupingRetainsSmallerOriginalPayloadCeiling(int bodyLength, int expectedGroups)
    {
        // Arrange
        string[] materialized = ["DO 1;", "DO 2;", "DO 3;"];
        var deferred = materialized.Select(SplitRaw).ToArray();
        var body = new string('x', bodyLength);
        var expected = Compact(materialized, body);

        // Act
        var actual = Compact(deferred, body);

        // Assert
        Assert.Equal(expectedGroups, actual.Count);
        Assert.Equal(expected, actual);
    }

    /// <summary>Opaque provider fragments preserve identity and separate surrounding owned groups.</summary>
    [Fact]
    public void DeferredGroupingPreservesProviderBarriers()
    {
        // Arrange
        var providerFirst = new string('p', 8);
        var providerSecond = new string('q', 8);
        string[] materialized = ["DO 1;", "DO 2;", providerFirst, providerSecond, "DO 3;", "DO 4;"];
        MySqlSafeMigrationSetupFragment[] deferred =
        [
            SplitRaw("DO 1;"), SplitRaw("DO 2;"), providerFirst, providerSecond,
            SplitRaw("DO 3;"), SplitRaw("DO 4;"),
        ];

        var body = new string('x', 100);
        var expected = MySqlSafeMigrationSetupCommandCompactor.Compact(
            materialized, body, ["DO 0;"], providerSetupOffset: 2, providerSetupCount: 2);

        // Act
        var actual = MySqlSafeMigrationSetupCommandCompactor.Compact(
            deferred, body, ["DO 0;"], providerSetupOffset: 2, providerSetupCount: 2);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(4, actual.Count);
        Assert.Same(providerFirst, actual[1]);
        Assert.Same(providerSecond, actual[2]);
    }

    /// <summary>Hexadecimal pieces and raw SQL share the same final grouping path.</summary>
    [Fact]
    public void DeferredHexAssignmentGroupingMatchesMaterializedSql()
    {
        // Arrange
        var source = "SELECT '\u20ac';";
        var assignment = "SET @sql = 0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(source)) + ";";
        string[] materialized = [assignment, "PREPARE guard FROM @sql;", "EXECUTE guard;", "DEALLOCATE PREPARE guard;"];
        MySqlSafeMigrationSetupFragment[] deferred =
        [
            MySqlSafeMigrationSetupFragment.Compose("SET @sql = 0x", MySqlSafeMigrationSetupFragment.Part.Hex(source), ";"),
            SplitRaw(materialized[1]), SplitRaw(materialized[2]), SplitRaw(materialized[3]),
        ];

        var body = new string('x', 1024);
        var expected = Compact(materialized, body);

        // Act
        var actual = Compact(deferred, body);

        // Assert
        Assert.Single(actual);
        Assert.Equal(expected, actual);
    }

    /// <summary>Oversized original fragment scopes stay ungrouped and retain provider rejection.</summary>
    /// <param name="setupCount">The original number of setup fragments before compaction.</param>
    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public void DeferredGroupingCannotHideOversizedOriginalFragmentScope(int setupCount)
    {
        // Arrange
        var materialized = Enumerable.Repeat("DO 0;", setupCount).ToArray();
        var deferred = materialized.Select(SplitRaw).ToArray();
        var body = new string('x', 100);
        var expected = Compact(materialized, body);

        // Act
        var actual = Compact(deferred, body);
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped(
            actual, body, ["DO 0;", "DO 0;"]));

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(setupCount, actual.Count);
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Deferred materialization cannot conceal an oversized full-scope SQL length.</summary>
    [Fact]
    public void DeferredGroupingCannotHideOversizedOriginalTextScope()
    {
        // Arrange
        var largeSql = new string(' ', 1_048_571) + "DO 0;";
        string[] materialized = [largeSql, "DO 0;"];
        MySqlSafeMigrationSetupFragment[] deferred = [SplitRaw(largeSql), SplitRaw("DO 0;")];
        var expected = Compact(materialized, "DO 0;");

        // Act
        var actual = Compact(deferred, "DO 0;");
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped(
            actual, "DO 0;", ["DO 0;", "DO 0;"]));

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(2, actual.Count);
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Deferred fragments preserve the exact original 128-fragment admission with fused controls.</summary>
    /// <param name="fusedCount">The controls already fused into setup fragments.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    public void DeferredGroupingAcceptsExactOriginalFragmentAdmission(int fusedCount)
    {
        // Arrange
        var materialized = Enumerable.Repeat("DO 0;", 125 - fusedCount).ToArray();
        var deferred = materialized.Select(SplitRaw).ToArray();
        var body = new string('x', 100);
        var expected = MySqlSafeMigrationSetupCommandCompactor.Compact(
            materialized, body, ["DO 0;", "DO 0;"], materialized.Length, 0, fusedCount);

        // Act
        var actual = MySqlSafeMigrationSetupCommandCompactor.Compact(
            deferred, body, ["DO 0;", "DO 0;"], deferred.Length, 0, fusedCount);

        var scope = MySqlMigrationCommandSpec.CreateScoped(actual, body, ["DO 0;", "DO 0;"]);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(string.Concat(materialized) + body + "DO 0;DO 0;", scope.CommandText);
    }

    /// <summary>Physical fragment savings cannot admit an original 129-fragment scope.</summary>
    /// <param name="fusedCount">The controls already fused into setup fragments.</param>
    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void DeferredGroupingRejectsOriginalFusedFragmentOverflow(int fusedCount)
    {
        // Arrange
        var setupCount = fusedCount == int.MaxValue ? 1 : 126 - fusedCount;
        var deferred = Enumerable.Repeat(SplitRaw("DO 0;"), setupCount).ToArray();

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupCommandCompactor.Compact(
            deferred, "DO 0;", ["DO 0;", "DO 0;"], deferred.Length, 0, fusedCount));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Invalid provider ranges and fusion counts fail before any grouping can weaken boundaries.</summary>
    /// <param name="offset">The first provider setup index.</param>
    /// <param name="count">The provider setup fragment count.</param>
    /// <param name="fusedCount">The original already-fused fragment count.</param>
    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(3, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 0, -1)]
    public void DeferredGroupingRejectsInvalidRanges(int offset, int count, int fusedCount)
    {
        // Arrange
        MySqlSafeMigrationSetupFragment[] deferred = [SplitRaw("DO 1;"), SplitRaw("DO 2;")];

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSetupCommandCompactor.Compact(
            deferred, "DO 0;", ["DO 0;"], offset, count, fusedCount));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Creates raw deferred pieces without changing the materialized reference SQL.</summary>
    private static MySqlSafeMigrationSetupFragment SplitRaw(string sql)
        => MySqlSafeMigrationSetupFragment.Compose(sql[..(sql.Length / 2)], sql[(sql.Length / 2)..]);

    /// <summary>Uses the production cleanup shape for the existing materialized comparison path.</summary>
    private static IReadOnlyList<string> Compact(string[] setup, string body)
        => MySqlSafeMigrationSetupCommandCompactor.Compact(setup, body, ["DO 0;", "DO 0;"], setup.Length, 0);

    /// <summary>Uses the identical production cleanup shape for the deferred materialization path.</summary>
    private static IReadOnlyList<string> Compact(MySqlSafeMigrationSetupFragment[] setup, string body)
        => MySqlSafeMigrationSetupCommandCompactor.Compact(setup, body, ["DO 0;", "DO 0;"], setup.Length, 0);
}
