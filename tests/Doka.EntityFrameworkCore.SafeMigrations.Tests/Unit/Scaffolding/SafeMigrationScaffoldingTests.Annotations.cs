namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Snapshots annotation arrays and restores independent copies after both caller mutations.</summary>
    [Fact]
    public void ProviderColumnAnnotationsAreSnapshottedAndRestoredOnStandardOperations()
    {
        // Arrange
        var prefixLengths = new[] { new[] { 12, 24 } };
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:ValueGenerationStrategy"] = TestValueGenerationStrategy.Identity;
        operation["Test:PrefixLengths"] = prefixLengths;

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);
        prefixLengths[0][0] = 99;
        var standard = SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null);

        var restoredPrefixLengths = (int[][])standard["Test:PrefixLengths"]!;
        restoredPrefixLengths[0][0] = 48;
        var secondStandard = SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null);

        // Assert
        Assert.IsType<AddColumnOperation>(standard);
        Assert.IsType<AddColumnOperation>(secondStandard);
        Assert.Equal(TestValueGenerationStrategy.Identity, standard["Test:ValueGenerationStrategy"]);
        Assert.Equal([48, 24], restoredPrefixLengths[0]);
        Assert.Equal([12, 24], Assert.IsType<int[][]>(secondStandard["Test:PrefixLengths"])[0]);
    }

    /// <summary>Rejects mutable annotation values that cannot be snapshotted safely.</summary>
    [Fact]
    public void MutableProviderAnnotationValue_IsRejectedDuringCapture()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Mutable"] = new List<int> { 1 };

        // Act
        var exception = Assert.Throws<NotSupportedException>(() =>
            SafeMigrationExpectedDefinitionFactory.From(operation));

        // Assert
        Assert.Contains("cannot be captured immutably", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Fingerprints every supported immutable annotation value deterministically.</summary>
    /// <param name="valueCase">The serializable annotation-value case identifier.</param>
    [Theory]
    [InlineData("string")]
    [InlineData("char")]
    [InlineData("boolean")]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("decimal")]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("datetime")]
    [InlineData("datetimeoffset")]
    [InlineData("timespan")]
    [InlineData("guid")]
    [InlineData("bytes")]
    [InlineData("type")]
    [InlineData("enum")]
    [InlineData("array")]
    public void SupportedProviderAnnotationValueKindsHaveDeterministicFingerprints(
        string valueCase
    )
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Value"] = CreateAnnotationValue(valueCase);

        // Act
        var firstCapture = SafeMigrationProviderAnnotation.Capture(operation);
        var secondCapture = SafeMigrationProviderAnnotation.Capture(operation);

        // Assert
        var first = Assert.Single(firstCapture);
        var second = Assert.Single(secondCapture);

        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    /// <summary>Rejects each unsupported array shape before any annotation can be captured.</summary>
    /// <param name="nonZeroBased">Whether the array has a nonzero lower bound rather than multiple dimensions.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidProviderAnnotationArrayShapesAreRejected(
        bool nonZeroBased
    )
    {
        // Arrange
        var value = nonZeroBased ? Array.CreateInstance(typeof(int), [1], [1]) : new int[1, 1];
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Array"] = value;

        // Act
        var exception = Record.Exception(() => SafeMigrationProviderAnnotation.Capture(operation));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);

        Assert.Contains("one-dimensional and zero-based", unsupported.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects a null annotation source at the capture boundary.</summary>
    [Fact]
    public void NullProviderAnnotationSourceIsRejected()
    {
        // Arrange
        MigrationOperation? operation = null;

        // Act
        var exception = Record.Exception(() => SafeMigrationProviderAnnotation.Capture(operation!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Creates the original value for each serializable immutable-annotation theory case.</summary>
    private static object CreateAnnotationValue(
        string valueCase
    ) => valueCase switch
    {
        "string" => "value",
        "char" => 'v',
        "boolean" => true,
        "byte" => (byte)1,
        "sbyte" => (sbyte)-1,
        "short" => (short)-2,
        "ushort" => (ushort)2,
        "int" => -3,
        "uint" => (uint)3,
        "long" => -4L,
        "ulong" => 4UL,
        "decimal" => 1.25m,
        "float" => 1.5f,
        "double" => 1.75d,
        "date" => new DateOnly(2026, 8, 27),
        "time" => new TimeOnly(12, 34, 56),
        "datetime" => new DateTime(2026, 8, 27, 12, 34, 56, DateTimeKind.Utc),
        "datetimeoffset" => new DateTimeOffset(2026, 8, 27, 12, 34, 56, TimeSpan.FromHours(2)),
        "timespan" => TimeSpan.FromMinutes(5),
        "guid" => Guid.Parse("1ea905b8-9114-46f1-811d-9db2ba394f31"),
        "bytes" => new byte[] { 1, 2, 3 },
        "type" => typeof(string),
        "enum" => TestValueGenerationStrategy.Identity,
        "array" => new[] { 1, 2, 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(valueCase), valueCase, "Unknown annotation value."),
    };

    private enum TestValueGenerationStrategy
    {
        Identity = 1,
    }
}
