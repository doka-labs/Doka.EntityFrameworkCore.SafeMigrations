namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects projected physical key budgets and fail-closed width evidence.</summary>
public sealed class MySqlProjectedIndexPhysicalShapeTests : IDisposable
{
    private readonly DbContext _context = CreateContext();
    private ServiceProvider? _nativeServices;

    /// <inheritdoc />
    public void Dispose()
    {
        _context.Dispose();
        _nativeServices?.Dispose();
    }

    /// <summary>Accepts a target prefix within the accepted widened character-column domain.</summary>
    [Fact]
    public void WidenedCharacterColumnAcceptsAValidTargetPrefix()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 800));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.Feasible, result);
    }

    /// <summary>Distinguishes a prefix beyond the projected column from a representable physical key.</summary>
    [Fact]
    public void PrefixLongerThanTheProjectedColumnIsRejected()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 767));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.PrefixExceedsTargetColumn, result);
    }

    /// <summary>A TEXT prefix can prove physical width without a declared character ceiling.</summary>
    [Fact]
    public void TextPrefixDoesNotRequireDeclaredCharacterLength()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            new ExpectedColumnDefinition("property", typeof(string), isNullable: false, storeType: "text"));

        var index = Index(new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.Feasible, result);
    }

    /// <summary>
    /// Requires an explicit prefix when the full projected character domain exceeds the native key budget.
    /// </summary>
    [Fact]
    public void OverlongUnprefixedProjectedColumnRequiresAnExplicitPrefix()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 800));

        var index = Index(new ExpectedIndexKeyDefinition(column: "property"));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix, result);
    }

    /// <summary>Applies the captured page-size and row-format budgets to the projected key width.</summary>
    /// <param name="maximumKeyBytes">The captured native byte budget for the complete ordered key.</param>
    /// <param name="prefixLength">The explicit native key-part prefix length.</param>
    /// <param name="isFeasible">Whether the exact native key shape is physically representable.</param>
    [Theory]
    [InlineData(768, 192, true)]
    [InlineData(768, 193, false)]
    [InlineData(1536, 384, true)]
    [InlineData(1536, 385, false)]
    public void ProjectedPrefixesRespectPageAndRowFormatLimits(
        int maximumKeyBytes,
        int prefixLength,
        bool isFeasible
    )
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 800));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "property", prefixLength: prefixLength));

        // Act
        var result = Validate(index, columns, maximumKeyBytes);

        // Assert
        Assert.Equal(
            isFeasible
                ? MySqlProjectedIndexPhysicalShapeResult.Feasible
                : MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit,
            result);
    }

    /// <summary>Includes every ordered composite member when calculating the projected key budget.</summary>
    [Fact]
    public void CompositeProjectedKeyUsesEveryOrderedColumnWidth()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("left_value", length: 500),
            CharacterColumn("right_value", length: 500));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "left_value", prefixLength: 384),
            new ExpectedIndexKeyDefinition(column: "right_value", prefixLength: 385));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit, result);
    }

    /// <summary>Measures a binary prefix in bytes rather than applying a character-set multiplier.</summary>
    /// <param name="columnLength">The declared character or byte length used by the physical-width proof.</param>
    /// <param name="prefixLength">The explicit native key-part prefix length.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData(768, 768, (int)MySqlProjectedIndexPhysicalShapeResult.Feasible)]
    [InlineData(767, 768, (int)MySqlProjectedIndexPhysicalShapeResult.PrefixExceedsTargetColumn)]
    [InlineData(800, 769, (int)MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit)]
    public void ProjectedBinaryPrefixesUseByteUnits(
        int columnLength,
        int prefixLength,
        int expected
    )
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            new ExpectedColumnDefinition(
                "payload",
                typeof(byte[]),
                isNullable: false,
                storeType: $"varbinary({columnLength.ToString(CultureInfo.InvariantCulture)})",
                maxLength: columnLength));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "payload", prefixLength: prefixLength));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 768);

        // Assert
        Assert.Equal((MySqlProjectedIndexPhysicalShapeResult)expected, result);
    }

    /// <summary>Includes scalar key members when checking the total composite budget.</summary>
    [Fact]
    public void ProjectedScalarWidthParticipatesInTheCompositeLimit()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 800),
            new ExpectedColumnDefinition("sequence", typeof(int), isNullable: false, storeType: "int"));

        var index = Index(
            new ExpectedIndexKeyDefinition(column: "property", prefixLength: 191),
            new ExpectedIndexKeyDefinition(column: "sequence"));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 767);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit, result);
    }

    /// <summary>
    /// Keeps scalar prefixes and unavailable source columns unprovable instead of guessing their width.
    /// </summary>
    /// <param name="column">The existing scalar or unavailable column whose prefix is not provable.</param>
    /// <param name="prefixLength">The invalid scalar prefix or absent-column unprefixed request.</param>
    [Theory]
    [InlineData("sequence", 1)]
    [InlineData("missing", null)]
    public void ScalarPrefixAndUnknownColumnRemainUnverifiable(
        string column,
        int? prefixLength
    )
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            new ExpectedColumnDefinition("sequence", typeof(int), isNullable: false, storeType: "int"));

        var index = Index(new ExpectedIndexKeyDefinition(column: column, prefixLength: prefixLength));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 3072);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.Unverifiable, result);
    }

    /// <summary>Rejects a projected key on an unsupported storage engine.</summary>
    [Fact]
    public void UnsupportedStorageEngineRemainsFailClosed()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("property", length: 80));

        var index = Index(new ExpectedIndexKeyDefinition(column: "property"));

        // Act
        var result = Validate(index, columns, maximumKeyBytes: 0);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.UnsupportedStorageEngine, result);
    }

    /// <summary>Checks the full projected primary-key domain rather than assuming a truncated prefix.</summary>
    [Fact]
    public void PrimaryKeyUsesTheFullProjectedColumnWidth()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("external_id", length: 192));

        var primaryKey = new ExpectedPrimaryKeyDefinition(
            "pk_records",
            "records",
            ["external_id"]);

        // Act
        var result = MySqlProjectedIndexPhysicalShape.Validate(
            primaryKey,
            columns,
            new SafeMigrationIndexPhysicalEnvironment(MaximumKeyBytes: 767),
            _context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit, result);
    }

    /// <summary>Includes every unique-constraint member in its projected physical key width.</summary>
    [Fact]
    public void UniqueConstraintUsesEveryCompositeColumnWidth()
    {
        // Arrange
        var columns = ProjectedColumns.Create(
            CharacterColumn("left_value", length: 96),
            CharacterColumn("right_value", length: 96));

        var uniqueConstraint = new ExpectedUniqueConstraintDefinition(
            "uq_records_values",
            "records",
            ["left_value", "right_value"]);

        // Act
        var result = MySqlProjectedIndexPhysicalShape.Validate(
            uniqueConstraint,
            columns,
            new SafeMigrationIndexPhysicalEnvironment(MaximumKeyBytes: 767),
            _context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit, result);
    }

    /// <summary>Native UUID columns consume sixteen bytes in every ordinary physical key.</summary>
    /// <param name="kind">Zero for an index, one for a primary key, or two for a unique constraint.</param>
    /// <param name="projected">Whether the key columns were accepted earlier in the operation stream.</param>
    /// <param name="maximumKeyBytes">The independently captured physical key limit.</param>
    /// <param name="expected">Whether both native UUID columns fit within that limit.</param>
    [Theory]
    [InlineData(0, false, 32, true)]
    [InlineData(0, false, 31, false)]
    [InlineData(0, true, 32, true)]
    [InlineData(0, true, 31, false)]
    [InlineData(1, false, 32, true)]
    [InlineData(1, false, 31, false)]
    [InlineData(1, true, 32, true)]
    [InlineData(1, true, 31, false)]
    [InlineData(2, false, 32, true)]
    [InlineData(2, false, 31, false)]
    [InlineData(2, true, 32, true)]
    [InlineData(2, true, 31, false)]
    public void NativeUuidCompositeKeysRespectFixedPhysicalWidth(
        int kind,
        bool projected,
        int maximumKeyBytes,
        bool expected
    )
    {
        // Arrange
        using var context = CreateNativeUuidContext();
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var columns = projected
            ? ProjectedColumns.Create(NativeUuidColumn("left_id"), NativeUuidColumn("right_id"))
            : ProjectedColumns.Create();

        var shape = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape("uuid", null, null, null, null, null);
        var environment = new SafeMigrationIndexPhysicalEnvironment(
            maximumKeyBytes,
            Columns: new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal)
            {
                ["left_id"] = shape,
                ["right_id"] = shape,
            });

        // Act
        var result = kind switch
        {
            0 => MySqlProjectedIndexPhysicalShape.Validate(
                Index(new ExpectedIndexKeyDefinition("left_id"), new ExpectedIndexKeyDefinition("right_id")),
                columns,
                environment,
                mappings),
            1 => MySqlProjectedIndexPhysicalShape.Validate(
                new ExpectedPrimaryKeyDefinition("pk_records", "records", ["left_id", "right_id"]),
                columns,
                environment,
                mappings),
            2 => MySqlProjectedIndexPhysicalShape.Validate(
                new ExpectedUniqueConstraintDefinition("uq_records", "records", ["left_id", "right_id"]),
                columns,
                environment,
                mappings),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Assert
        Assert.Equal(
            expected
                ? MySqlProjectedIndexPhysicalShapeResult.Feasible
                : MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit,
            result);
    }

    /// <summary>A native UUID's textual wire representation does not make index prefixes legal.</summary>
    /// <param name="projected">Whether the UUID column was accepted earlier in the operation stream.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUuidPrefixRemainsUnverifiable(
        bool projected
    )
    {
        // Arrange
        using var context = CreateNativeUuidContext();
        var columns = projected
            ? ProjectedColumns.Create(NativeUuidColumn("id"))
            : ProjectedColumns.Create();

        var index = Index(new ExpectedIndexKeyDefinition("id", prefixLength: 1));
        var environment = new SafeMigrationIndexPhysicalEnvironment(
            3072,
            Columns: new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal)
            {
                ["id"] = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
                    "uuid",
                    null,
                    null,
                    null,
                    null,
                    null),
            });

        // Act
        var result = MySqlProjectedIndexPhysicalShape.Validate(
            index,
            columns,
            environment,
            context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.Unverifiable, result);
    }

    /// <summary>Catalog UUID widths are independent of unavailable character metadata.</summary>
    [Fact]
    public void NativeUuidCatalogShapeDoesNotAcquireCharacterFacets()
    {
        // Arrange
        const string dataType = "UUID";

        // Act
        var shape = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
            dataType,
            null,
            null,
            null,
            null,
            null);

        // Assert
        Assert.Equal(16L, shape.FullWidthBytes);
        Assert.False(shape.SupportsPrefix);
        Assert.Null(shape.MaximumPrefixUnits);
        Assert.Equal(0L, shape.BytesPerPrefixUnit);
    }

    /// <summary>BIT widths retain nullable catalog precision and round up partial bytes.</summary>
    /// <param name="precision">The available native bit precision, or null when unreported.</param>
    /// <param name="expectedWidth">The fixed key width, or null when it cannot be proved.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData(1, 1L)]
    [InlineData(8, 1L)]
    [InlineData(9, 2L)]
    [InlineData(64, 8L)]
    public void BitCatalogPrecisionPreservesNullableRoundedWidth(
        int? precision,
        long? expectedWidth
    )
    {
        // Arrange
        const string dataType = "bit";

        // Act
        var shape = MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
            dataType,
            null,
            null,
            precision,
            null,
            null);

        // Assert
        Assert.Equal(expectedWidth, shape.FullWidthBytes);
        Assert.False(shape.SupportsPrefix);
    }

    /// <summary>Rejects excess key parts consistently for indexes, primary keys, and unique constraints.</summary>
    [Fact]
    public void EveryProjectedPhysicalKeyRejectsMoreThanSixteenParts()
    {
        // Arrange
        var columnNames = Enumerable
            .Range(0, 17)
            .Select(index => $"value_{index.ToString(CultureInfo.InvariantCulture)}")

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var columns = ProjectedColumns.Create(
            columnNames
                .Select(name => CharacterColumn(name, length: 1))
                .ToArray());

        var index = Index(
            columnNames
                .Select(static name => new ExpectedIndexKeyDefinition(column: name))

                // WHY: Retain this explicit materialization/copy path and its collection lowering.
                // ReSharper disable once UseCollectionExpression
                .ToArray());

        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_records", "records", columnNames);
        var uniqueConstraint = new ExpectedUniqueConstraintDefinition("uq_records", "records", columnNames);
        var environment = new SafeMigrationIndexPhysicalEnvironment(MaximumKeyBytes: 3072);
        var typeMappingSource = _context.GetService<IRelationalTypeMappingSource>();

        // Act
        var indexResult = MySqlProjectedIndexPhysicalShape.Validate(
            index,
            columns,
            environment,
            typeMappingSource);

        var primaryKeyResult = MySqlProjectedIndexPhysicalShape.Validate(
            primaryKey,
            columns,
            environment,
            typeMappingSource);

        var uniqueConstraintResult = MySqlProjectedIndexPhysicalShape.Validate(
            uniqueConstraint,
            columns,
            environment,
            typeMappingSource);

        // Assert
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts, indexResult);
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts, primaryKeyResult);
        Assert.Equal(MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts, uniqueConstraintResult);
    }

    private MySqlProjectedIndexPhysicalShapeResult Validate(
        ExpectedIndexDefinition index,
        ISafeMigrationProjectedColumnSource columns,
        int maximumKeyBytes
    ) => MySqlProjectedIndexPhysicalShape.Validate(
        index,
        columns,
        new SafeMigrationIndexPhysicalEnvironment(maximumKeyBytes),
        _context.GetService<IRelationalTypeMappingSource>());

    private static ExpectedColumnDefinition CharacterColumn(
        string name,
        int length
    ) => new(
        name,
        typeof(string),
        isNullable: false,
        storeType: $"varchar({length.ToString(CultureInfo.InvariantCulture)})",
        maxLength: length);

    /// <summary>Describes native storage without a character or binary GUID representation.</summary>
    /// <param name="name">The exact column name.</param>
    /// <returns>A native UUID column with no textual length facets.</returns>
    private static ExpectedColumnDefinition NativeUuidColumn(
        string name
    ) => new(
        name,
        typeof(Guid),
        isNullable: false,
        storeType: "uuid");

    private static ExpectedIndexDefinition Index(
        params ExpectedIndexKeyDefinition[] keys
    ) => new("ix_records", "records", keys);

    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;"
            + "Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        return new DbContext(options.Options);
    }

    /// <summary>Resolves native UUID mappings under an explicitly capable MariaDB profile.</summary>
    /// <returns>A context whose mappings can be inspected without opening a server connection.</returns>
    private DbContext CreateNativeUuidContext()
    {
        // WHY: Capability-profile probes own their provider lifetime instead
        // of retaining additional configurations in EF's process-wide cache.
        _nativeServices ??= new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .BuildServiceProvider(validateScopes: true);

        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseInternalServiceProvider(_nativeServices);
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)));

        return new DbContext(options.Options);
    }

    private sealed class ProjectedColumns : ISafeMigrationProjectedColumnSource
    {
        private readonly Dictionary<string, ExpectedColumnDefinition> _definitions;

        private ProjectedColumns(
            Dictionary<string, ExpectedColumnDefinition> definitions
        )
        {
            _definitions = definitions;
        }

        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => _definitions.TryGetValue(column, out definition);

        public static ProjectedColumns Create(
            params ExpectedColumnDefinition[] definitions
        ) => new(definitions.ToDictionary(static definition => definition.Name, StringComparer.Ordinal));
    }
}
