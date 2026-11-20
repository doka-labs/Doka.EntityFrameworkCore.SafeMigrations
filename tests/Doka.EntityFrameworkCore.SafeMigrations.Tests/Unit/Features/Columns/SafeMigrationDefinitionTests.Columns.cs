namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationDefinitionTests
{
    /// <summary>Unknown nullable facets do not invent a scale-versus-precision conflict.</summary>
    /// <param name="precision">The optional declared precision.</param>
    /// <param name="scale">The independent optional scale.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 3)]
    [InlineData(3, null)]
    [InlineData(3, 0)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ColumnDefinitionPreservesIndependentNullablePrecisionAndScale(
        int? precision,
        int? scale
    )
    {
        // Arrange: The theory supplies the independent nullable facets.

        // Act
        var column = new ExpectedColumnDefinition(
            "amount",
            typeof(decimal),
            isNullable: false,
            precision: precision,
            scale: scale);

        // Assert
        Assert.Equal(precision, column.Precision);
        Assert.Equal(scale, column.Scale);
    }

    /// <summary>A known precision still bounds scale, including the largest supported CLR facet.</summary>
    /// <param name="precision">The known positive precision.</param>
    /// <param name="scale">The scale that exceeds that precision.</param>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(10, 11)]
    [InlineData(int.MaxValue - 1, int.MaxValue)]
    public void ColumnDefinitionRejectsScaleBeyondKnownPrecision(
        int precision,
        int scale
    )
    {
        // Arrange: The theory supplies precision and an incompatible scale.

        // Act
        var exception = Record.Exception(() =>
        {
            _ = new ExpectedColumnDefinition(
                "amount",
                typeof(decimal),
                isNullable: false,
                precision: precision,
                scale: scale);
        });

        // Assert
        Assert.Equal("scale",
            Assert.IsType<ArgumentException>(exception)
                .ParamName);
    }

    /// <summary>Preserves each supported literal family as one independently discoverable value case.</summary>
    /// <param name="ordinal">The representative literal's ordinal in the shared contract.</param>
    [Theory]
    [MemberData(nameof(RepresentativeLiteralOrdinals))]
    public void DefaultValueSupportsEveryDocumentedLiteralFamily(
        int ordinal
    )
    {
        // Arrange
        var value = SafeMigrationLiteralContract
            .CreateRepresentativeValues()[ordinal];

        // Act
        var literal = SafeMigrationDefaultValue.Literal(value);

        // Assert
        Assert.Equal(SafeMigrationDefaultValueKind.Literal, literal.Kind);
    }

    /// <summary>Enumerates serializable case identities without dropping nonserializable literal families.</summary>
    /// <returns>Every ordinal from the shared representative literal contract.</returns>
    public static IEnumerable<object[]> RepresentativeLiteralOrdinals()
    {
        var values = SafeMigrationLiteralContract.CreateRepresentativeValues();

        // WHY: Discovery receives serializable ordinals, not date/time or binary literal payloads.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            yield return [ordinal];
        }
    }

    /// <summary>Proves addition safety only for a nullable, defaulted or computed value source.</summary>
    /// <param name="source">The complete source-of-value contract.</param>
    /// <param name="expected">Whether a missing column can safely be added.</param>
    [Theory]
    [InlineData("nullable", true)]
    [InlineData("default", true)]
    [InlineData("computed", true)]
    [InlineData("absent", false)]
    public void MissingColumnRepairRequiresAKnownValueSource(
        string source,
        bool expected
    )
    {
        // Arrange
        var definition = source switch
        {
            "nullable" => RepairColumn(isNullable: true),
            "default" => RepairColumn(defaultValue: SafeMigrationDefaultValue.Literal(1m)),
            "computed" => RepairColumn(computedColumnSql: "1 + 1", isStored: true),
            "absent" => RepairColumn(),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        // Act
        var allowed = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(definition);

        // Assert
        Assert.Equal(expected, allowed);
    }

    /// <summary>Preserves the exact lossless-transition exclusions as independent facet cases.</summary>
    /// <param name="facet">The transition facet changed independently from the baseline.</param>
    /// <param name="expected">Whether the complete transition is approved.</param>
    [Theory]
    [InlineData("lossless", true)]
    [InlineData("name", false)]
    [InlineData("clr_type", false)]
    [InlineData("store_type", false)]
    [InlineData("unicode", false)]
    [InlineData("length", false)]
    [InlineData("fixed", false)]
    [InlineData("row_version", false)]
    [InlineData("precision", false)]
    [InlineData("scale", false)]
    [InlineData("collation", false)]
    [InlineData("computed", false)]
    [InlineData("computed_storage", false)]
    public void ColumnRepairSafetyBindsEveryLosslessTransitionField(
        string facet,
        bool expected
    )
    {
        // Arrange
        var baseline = facet == "computed_storage"
            ? RepairColumn(computedColumnSql: "1 + 1", isStored: true)
            : RepairColumn(isNullable: true, comment: "legacy");

        var target = facet switch
        {
            "lossless" => RepairColumn(isNullable: false,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal(1m)),
            "name" => RepairColumn(name: "other", isNullable: true, comment: "legacy"),
            "clr_type" => RepairColumn(clrType: typeof(long), isNullable: true, comment: "legacy"),
            "store_type" => RepairColumn(storeType: "bigint", isNullable: true, comment: "legacy"),
            "unicode" => RepairColumn(isUnicode: true, isNullable: true, comment: "legacy"),
            "length" => RepairColumn(maxLength: 40, isNullable: true, comment: "legacy"),
            "fixed" => RepairColumn(isFixedLength: true, isNullable: true, comment: "legacy"),
            "row_version" => RepairColumn(isRowVersion: true, isNullable: true, comment: "legacy"),
            "precision" => RepairColumn(precision: 11, isNullable: true, comment: "legacy"),
            "scale" => RepairColumn(scale: 3, isNullable: true, comment: "legacy"),
            "collation" => RepairColumn(collation: "other", isNullable: true, comment: "legacy"),
            "computed" => RepairColumn(computedColumnSql: "1 + 1",
                isNullable: true,
                comment: "legacy",
                isStored: true),
            "computed_storage" => RepairColumn(computedColumnSql: "1 + 1", isStored: false),
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };

        // Act
        var allowed = SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(baseline, target);

        // Assert
        Assert.Equal(expected, allowed);
    }

    /// <summary>Requires a real definition at every repair entry point.</summary>
    /// <param name="entryPoint">The missing add, alter-old, alter-target or convergence input.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ColumnRepairRejectsMissingDefinitions(
        int entryPoint
    )
    {
        // Arrange
        var baseline = RepairColumn(isNullable: true, comment: "legacy");
        var target = RepairColumn(isNullable: false,
            comment: "canonical",
            defaultValue: SafeMigrationDefaultValue.Literal(1m));

        Action repair = entryPoint switch
        {
            0 => () => SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(null!),
            1 => () => SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(null!, target),
            2 => () => SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(baseline, null!),
            3 => () => SafeMigrationColumnRepairHelper.CanSafelyConvergeExistingColumn(null!),
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };

        // Act
        var exception = Record.Exception(repair);

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Retains intrinsic and provider-owned exclusions on existing-column convergence.</summary>
    /// <param name="facet">The independent existing-column contract.</param>
    /// <param name="expected">Whether convergence is intrinsically safe.</param>
    [Theory]
    [InlineData("lossless", true)]
    [InlineData("row_version", false)]
    [InlineData("computed", false)]
    [InlineData("provider", false)]
    public void ExistingColumnConvergenceRetainsEveryIntrinsicExclusion(
        string facet,
        bool expected
    )
    {
        // Arrange
        var providerOperation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            IsNullable = false,
            ["Test:Identity"] = "identity",
        };

        var definition = facet switch
        {
            "lossless" => RepairColumn(isNullable: false,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal(1m)),
            "row_version" => RepairColumn(isRowVersion: true),
            "computed" => RepairColumn(computedColumnSql: "1 + 1", isStored: true),
            "provider" => SafeMigrationExpectedDefinitionFactory.From(providerOperation),
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };

        // Act
        var allowed = SafeMigrationColumnRepairHelper.CanSafelyConvergeExistingColumn(definition);

        // Assert
        Assert.Equal(expected, allowed);
    }

    private static ExpectedColumnDefinition RepairColumn(
        string name = "value",
        Type? clrType = null,
        bool isNullable = false,
        string storeType = "decimal(10,2)",
        bool? isUnicode = false,
        int? maxLength = null,
        bool? isFixedLength = false,
        bool isRowVersion = false,
        int? precision = 10,
        int? scale = 2,
        string? collation = "canonical",
        string? comment = null,
        SafeMigrationDefaultValue? defaultValue = null,
        string? computedColumnSql = null,
        bool? isStored = null
    ) => new(
        name,
        clrType ?? typeof(decimal),
        isNullable,
        storeType,
        isUnicode,
        maxLength,
        isFixedLength,
        isRowVersion,
        precision,
        scale,
        collation is null
            ? null
            : new SafeMigrationCollationIdentifier(collation),
        comment,
        defaultValue ?? SafeMigrationDefaultValue.None,
        computedColumnSql,
        isStored);
}
