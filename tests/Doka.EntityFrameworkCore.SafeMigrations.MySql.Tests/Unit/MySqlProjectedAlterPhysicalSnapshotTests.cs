namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies cumulative row and key budgets without weakening source-bound row proofs.</summary>
public sealed class MySqlProjectedAlterPhysicalSnapshotTests : IDisposable
{
    private readonly DbContext _context = CreateContext();
    private ServiceProvider? _nativeServices;

    /// <summary>Five accepted text conversions preserve an ordinary GUID key and bounded log-like columns.</summary>
    /// <param name="rowFormat">The independently captured InnoDB storage format.</param>
    [Theory]
    [InlineData("Dynamic")]
    [InlineData("Compact")]
    [InlineData("Redundant")]
    public void MultipleAcceptedTextTransitionsFitWithGuidAndPrefixKeys(string rowFormat)
    {
        var original = new Dictionary<string, MySqlAlterColumnPhysicalShape>(StringComparer.Ordinal)
        {
            ["id"] = Catalog("char", length: 36),
            ["property"] = Catalog("longtext"),
            ["context"] = Catalog("longtext"),
            ["previous"] = Catalog("longtext"),
            ["current"] = Catalog("longtext"),
            ["hash"] = Catalog("longtext"),
        };

        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(original,
            [new MySqlAlterIndexPhysicalShape(false, [new MySqlAlterIndexPart("id", null)], true)],
            Environment(rowFormat == "Dynamic" ? 3072 : 767, rowFormat));

        var table = new TableView(
            Character("id", "char(36)"), Varchar("property", 800), Varchar("context", 800),
            Varchar("previous", 5000), Varchar("current", 5000), Character("hash", "longtext"))
        {
            Indexes = [new ExpectedIndexDefinition("ix_property", "records",
                [new ExpectedIndexKeyDefinition("property", prefixLength: rowFormat == "Dynamic" ? 768 : 191)])],
        };

        var result = snapshot.IsSafe(Alter("hash", 64), table, Mappings);

        Assert.True(result);
    }

    /// <summary>Individually valid conversions cannot collectively exceed the declared row budget.</summary>
    [Fact]
    public void SiblingTransitionsCannotIndependentlyReuseTheWholeRowBudget()
    {
        var snapshot = Snapshot(("left_value", Catalog("longtext")), ("right_value", Catalog("longtext")));
        var table = new TableView(Varchar("left_value", 8000), Character("right_value", "longtext"));

        var result = snapshot.IsSafe(Alter("right_value", 9000), table, Mappings);

        Assert.False(result);
    }

    /// <summary>
    /// Short VARCHAR fields cannot be assumed to move off-page when their combined inline row is too wide.
    /// </summary>
    /// <param name="rowFormat">The captured physical row format.</param>
    /// <param name="pageSize">The actual storage page size.</param>
    /// <param name="count">The number of independently accepted short VARCHAR targets.</param>
    /// <param name="expected">Whether the conservative in-page record fits.</param>
    [Theory]
    [InlineData("Dynamic", 16384, 30, true)]
    [InlineData("Dynamic", 16384, 33, false)]
    [InlineData("Compact", 16384, 33, false)]
    [InlineData("Redundant", 16384, 33, false)]
    [InlineData("Dynamic", 4096, 6, true)]
    [InlineData("Dynamic", 4096, 8, false)]
    public void CumulativeInlineBudgetIsIndependentOfDeclaredRowLimit(
        string rowFormat,
        int pageSize,
        int count,
        bool expected
    )
    {
        var columns = Enumerable.Range(0, count).ToDictionary(
            index => $"value_{index.ToString(CultureInfo.InvariantCulture)}",
            _ => Catalog("longtext"), StringComparer.Ordinal);

        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(
            columns, [], Environment(rowFormat: rowFormat, pageSize: pageSize));

        var table = new TableView(columns.Keys.Select(name => Varchar(name, 63)).ToArray());

        var result = snapshot.IsSafe(Alter("value_0", 63), table, Mappings);

        Assert.Equal(expected, result);
    }

    /// <summary>Inline storage cannot externalize a primary-key column even when it is variable length.</summary>
    [Fact]
    public void PrimaryKeyColumnRetainsItsFullInlineWidth()
    {
        var columns = Enumerable.Range(0, 21).ToDictionary(
            index => $"value_{index.ToString(CultureInfo.InvariantCulture)}",
            _ => Catalog("longtext"), StringComparer.Ordinal);

        columns.Add("id", Catalog("varchar", 700));
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(columns,
            [new MySqlAlterIndexPhysicalShape(false, [new MySqlAlterIndexPart("id", null)], true)], Environment());

        var definitions = columns.Keys.Select(name => name == "id" ? Varchar(name, 700) : Varchar(name, 63)).ToArray();
        var table = new TableView(definitions);

        var result = snapshot.IsSafe(Alter("value_0", 63), table, Mappings);

        Assert.False(result);
    }

    /// <summary>A unique NOT NULL fallback clustered key must retain its complete value inline.</summary>
    /// <param name="projected">Whether the unique key was created earlier in the stream.</param>
    /// <param name="retainedPrimary">Whether the immutable catalog retains a possibly removed primary key.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UniqueClusteredCandidateCannotUseOverflowBudget(
        bool projected,
        bool retainedPrimary
    )
    {
        var columns = Enumerable.Range(0, 750).ToDictionary(
            index => $"value_{index.ToString(CultureInfo.InvariantCulture)}",
            _ => Catalog("double"), StringComparer.Ordinal);

        columns.Add("id", Catalog("varchar", 100));
        var definitions = columns.Keys.Select(name => name == "id" ? Varchar(name, 100)
            : new ExpectedColumnDefinition(name, typeof(double), false, "double")).ToArray();

        var table = new TableView(definitions)
        {
            Indexes = projected ? [new ExpectedIndexDefinition("uq_id", "records",
                [new ExpectedIndexKeyDefinition("id")], unique: true)] : [],
        };

        var indexes = new List<MySqlAlterIndexPhysicalShape>();
        if (!projected)
        {
            indexes.Add(new MySqlAlterIndexPhysicalShape(false, [new MySqlAlterIndexPart("id", null)], IsUnique: true));
        }

        if (retainedPrimary)
        {
            indexes.Add(new MySqlAlterIndexPhysicalShape(
                false, [new MySqlAlterIndexPart("value_0", null)], IsPrimary: true));
        }

        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(columns, indexes, Environment());

        var result = snapshot.IsSafe(
            new AlterColumnIntent("records", Varchar("id", 768), Varchar("id", 100)), table, Mappings);

        Assert.False(result);
    }

    /// <summary>A fulltext index is not evaluated against the ordinary BTREE byte limit.</summary>
    [Fact]
    public void RetainedFullTextIndexDoesNotConsumeBtreeKeyBudget()
    {
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(
            new Dictionary<string, MySqlAlterColumnPhysicalShape> { ["value"] = Catalog("longtext") },
            [new MySqlAlterIndexPhysicalShape(true, [new MySqlAlterIndexPart("value", null)])], Environment());

        var table = new TableView(Character("value", "longtext"));

        var result = snapshot.IsSafe(Alter("value", 5000), table, Mappings);

        Assert.True(result);
    }

    /// <summary>Empty-table projection applies the same literal and SQL-mode backfill guard as runtime.</summary>
    /// <param name="strict">Whether the captured session uses strict mode.</param>
    /// <param name="literal">The proposed backfill literal.</param>
    /// <param name="expected">Whether both old and new domains accept the literal.</param>
    [Theory]
    [InlineData(true, "seed", true)]
    [InlineData(false, "seed", false)]
    [InlineData(true, "too long", false)]
    public void EmptyTableBackfillUsesRuntimeRepresentability(
        bool strict,
        string literal,
        bool expected
    )
    {
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(4)");
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(80)",
            defaultValue: SafeMigrationDefaultValue.Literal(literal));

        var environment = Environment() with { DefaultCharacterSet = "utf8mb4", StrictSqlMode = strict };
        var table = new TableView(source);

        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, table, environment, Mappings,
            canReuseCreationCharacterSet: true);

        Assert.Equal(expected, result);
    }

    /// <summary>Empty-table projection preserves the target expression-default SQL-mode boundary.</summary>
    /// <param name="supported">Whether the engine and session can preserve quoted expression defaults.</param>
    /// <param name="structured">Whether the target uses an expression instead of a plain VARCHAR literal.</param>
    /// <param name="expected">Whether the exact target can be emitted safely.</param>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void EmptyTableBackfillRequiresQuotedExpressionCapability(
        bool supported,
        bool structured,
        bool expected
    )
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(4)");
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(80)",
            defaultValue: structured ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("a'b"))
                : SafeMigrationDefaultValue.Literal("a'b"));

        var environment = Environment() with { SupportsQuotedExpressionDefaults = supported };

        // Act
        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, new TableView(source), environment, Mappings,
            canReuseCreationCharacterSet: true);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Empty-table projection requires TEXT default fidelity even when no backfill is emitted.</summary>
    /// <param name="supported">Whether TEXT expressions preserve control-character literal payloads.</param>
    /// <param name="backfill">Whether the type transition also tightens nullability.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EmptyTableRequiresTextDefaultControlCapability(
        bool supported,
        bool backfill
    )
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)");
        var target = new ExpectedColumnDefinition("value", typeof(string), !backfill, "text",
            defaultValue: SafeMigrationDefaultValue.Literal("a\\b"));

        var environment = Environment() with { SupportsTextExpressionControlCharacters = supported };

        // Act
        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, new TableView(source), environment, Mappings,
            canReuseCreationCharacterSet: true);

        // Assert
        Assert.Equal(supported, result);
    }

    /// <summary>Explicit collations require captured identities even on newly created empty tables.</summary>
    /// <param name="resolved">Whether the provider actually found the requested collation.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyTableBackfillDoesNotGuessExplicitCollation(bool resolved)
    {
        var collation = new SafeMigrationCollationIdentifier("latin1_bin");
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(4)", collation: collation);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(80)", collation: collation,
            defaultValue: SafeMigrationDefaultValue.Literal("seed"));

        var environment = Environment() with
        {
            DefaultCharacterSet = "utf8mb4",
            StrictSqlMode = true,
            CollationCharacterSets = resolved ? new Dictionary<string, string> { ["latin1_bin"] = "latin1" } : null,
        };

        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, new TableView(source), environment, Mappings,
            canReuseCreationCharacterSet: true);

        Assert.Equal(resolved, result);
    }

    /// <summary>A database-default change invalidates only implicit creation-charset backfill evidence.</summary>
    /// <param name="reusableDefault">Whether the table was created while captured database defaults were valid.</param>
    /// <param name="explicitCollation">Whether the column has an independently captured explicit collation.</param>
    /// <param name="backfill">Whether this transition needs literal-encoding evidence.</param>
    /// <param name="expected">Whether the provider can prove the transition without guessing a new default.</param>
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, true)]
    public void CreationDefaultLifetimeDoesNotOutliveAnAlterDatabase(
        bool reusableDefault,
        bool explicitCollation,
        bool backfill,
        bool expected
    )
    {
        var collation = explicitCollation ? new SafeMigrationCollationIdentifier("utf8mb4_bin") : null;
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "longtext", collation: collation);
        var target = new ExpectedColumnDefinition("value", typeof(string), !backfill, "varchar(10)",
            collation: collation,
            defaultValue: backfill ? SafeMigrationDefaultValue.Literal("\u00E9") : SafeMigrationDefaultValue.None);

        var environment = Environment() with
        {
            CollationCharacterSets = new Dictionary<string, string> { ["utf8mb4_bin"] = "utf8mb4" },
        };

        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, new TableView(source), environment, Mappings,
            canReuseCreationCharacterSet: reusableDefault);

        Assert.Equal(expected, result);
    }

    /// <summary>Certified widening can retain an original domain but cannot authorize a smaller target.</summary>
    /// <param name="originalLength">The immutable VARCHAR character limit.</param>
    /// <param name="targetLength">The later target character limit.</param>
    /// <param name="expected">Whether the original domain is contained in the target.</param>
    [Theory]
    [InlineData(200, 500, true)]
    [InlineData(500, 500, true)]
    [InlineData(501, 500, false)]
    public void OriginalDeclaredDomainSurvivesOnlyWithinItsCapturedBoundary(
        int originalLength,
        int targetLength,
        bool expected
    )
    {
        var snapshot = Snapshot(("value", Catalog("varchar", originalLength)));
        var source = Character("value", "longtext");

        var result = snapshot.PreservesOriginalValueDomain(Alter("value", targetLength), source);

        Assert.Equal(expected, result);
    }

    /// <summary>Unknown or nullable original domains cannot prove a required VARCHAR target.</summary>
    /// <param name="nullable">Whether to retain an originally nullable VARCHAR instead of an unbounded TEXT.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalUnboundedOrNullableDomainDoesNotProveRequiredTarget(bool nullable)
    {
        var original = nullable ? Catalog("varchar", 200) with { IsNullable = true } : Catalog("longtext");
        var snapshot = Snapshot(("value", original));

        var result = snapshot.PreservesOriginalValueDomain(Alter("value", 500), Character("value", "longtext"));

        Assert.False(result);
    }

    /// <summary>Original-domain reuse cannot bypass retained live foreign-key dependencies.</summary>
    [Fact]
    public void OriginalDomainDoesNotOverrideLiveForeignKeyDependency()
    {
        var snapshot = Snapshot(("value", Catalog("varchar", 200) with { HasForeignKey = true }));

        var result = snapshot.PreservesOriginalValueDomain(Alter("value", 500), Character("value", "longtext"));

        Assert.False(result);
    }

    /// <summary>A projected nullable source retains the physical source encoding, not the database default.</summary>
    /// <param name="literal">The proposed literal under the captured ASCII column encoding.</param>
    /// <param name="expected">Whether the literal is representable.</param>
    [Theory]
    [InlineData("seed", true)]
    [InlineData("\u00E9", false)]
    public void OriginalDomainUsesCapturedColumnEncoding(
        string literal,
        bool expected
    )
    {
        var snapshot = Snapshot(("value", Catalog("varchar", 200, 1) with
        {
            CharacterSet = "ascii",
            Collation = "ascii_bin",
        }));

        var source = new ExpectedColumnDefinition("value", typeof(string), true, "longtext");
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(500)",
            defaultValue: SafeMigrationDefaultValue.Literal(literal));

        var result = snapshot.PreservesOriginalValueDomain(new AlterColumnIntent("records", target, source), source);

        Assert.Equal(expected, result);
    }

    /// <summary>Recreated tables use captured creation defaults instead of a removed table's key limit.</summary>
    /// <param name="defaultCompact">Whether the new table is created under restrictive COMPACT defaults.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyRecreatedTableUsesCreationEnvironment(bool defaultCompact)
    {
        var source = Varchar("value", 100);
        var target = Varchar("value", 200);
        var table = new TableView(source)
        {
            Indexes = [new ExpectedIndexDefinition("ix_value", "records", [new ExpectedIndexKeyDefinition("value")])],
        };

        var environment = Environment(defaultCompact ? 3072 : 767, defaultCompact ? "Dynamic" : "Compact") with
        {
            NewTableEnvironment = Environment(defaultCompact ? 767 : 3072, defaultCompact ? "Compact" : "Dynamic"),
        };

        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, table, environment, Mappings,
            canReuseCreationCharacterSet: true);

        Assert.Equal(!defaultCompact, result);
    }

    /// <summary>Empty narrowing must retain runtime's strict SQL mode requirement.</summary>
    [Fact]
    public void EmptyNarrowingWithoutStrictModeRemainsUnproven()
    {
        var source = Character("value", "longtext");
        var table = new TableView(source);
        var environment = Environment() with { StrictSqlMode = false };

        var result = MySqlProjectedColumnTransition.IsSafe(
            Alter("value", 500), source, table, environment, Mappings, canReuseCreationCharacterSet: true);

        Assert.False(result);
    }

    /// <summary>Inferred store types obey the same strict-mode guard as explicitly authored types.</summary>
    /// <param name="strict">Whether strict SQL mode was captured.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyInferredNarrowingUsesResolvedStoreTypes(bool strict)
    {
        var source = new ExpectedColumnDefinition("value", typeof(string), false, maxLength: 1000);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, maxLength: 500);
        var environment = Environment() with { StrictSqlMode = strict };

        var result = MySqlProjectedColumnTransition.IsSafe(
            new AlterColumnIntent("records", target, source), source, new TableView(source), environment, Mappings,
            canReuseCreationCharacterSet: true);

        Assert.Equal(strict, result);
    }

    /// <summary>Compressed or unknown storage is not guessed from its otherwise sufficient index budget.</summary>
    /// <param name="rowFormat">The unsupported inline storage shape.</param>
    [Theory]
    [InlineData("Compressed")]
    [InlineData("Unknown")]
    public void UnprovenInlineEnvironmentRejectsTransition(string rowFormat)
    {
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(
            new Dictionary<string, MySqlAlterColumnPhysicalShape> { ["value"] = Catalog("longtext") },
            [], Environment(rowFormat: rowFormat));

        var table = new TableView(Character("value", "longtext"));

        var result = snapshot.IsSafe(Alter("value", 80), table, Mappings);

        Assert.False(result);
    }

    /// <summary>Inline bounds include overflow prefixes, pointers and field directories at transition edges.</summary>
    /// <param name="format">The captured format.</param>
    /// <param name="type">The physical source family.</param>
    /// <param name="length">The declared byte capacity for the one-byte test encoding.</param>
    /// <param name="primaryKey">Whether overflow is forbidden by the clustered key.</param>
    /// <param name="expected">The conservative field contribution including its directory entry.</param>
    [Theory]
    [InlineData("Dynamic", "varchar", 255, false, 257)]
    [InlineData("Dynamic", "varchar", 256, false, 257)]
    [InlineData("Dynamic", "varchar", 700, true, 704)]
    [InlineData("Dynamic", "longtext", 1000, false, 42)]
    [InlineData("Compact", "varbinary", 768, false, 770)]
    [InlineData("Compact", "varbinary", 769, false, 790)]
    [InlineData("Redundant", "varbinary", 787, false, 790)]
    [InlineData("Compact", "longtext", 1000, false, 790)]
    public void InlineFieldBoundsRemainConservativeAtOverflowThresholds(
        string format,
        string type,
        int length,
        bool primaryKey,
        long expected
    )
    {
        var shape = Catalog(type, length, characterBytes: 1);

        var known = MySqlProjectedAlterPhysicalSnapshot.TryGetInlineBytes(
            shape, primaryKey, Environment(rowFormat: format), out var bytes);

        Assert.True(known);
        Assert.Equal(expected, bytes);
    }

    /// <summary>Accepted newly added columns contribute even though they were absent from the catalog.</summary>
    [Fact]
    public void AddedSiblingColumnConsumesDeclaredRowBudget()
    {
        var snapshot = Snapshot(("value", Catalog("longtext")));
        var table = new TableView(Varchar("added_value", 8000), Character("value", "longtext"));

        var result = snapshot.IsSafe(Alter("value", 9000), table, Mappings);

        Assert.False(result);
    }

    /// <summary>Native UUID row and key contributions do not depend on textual wire encoding.</summary>
    /// <param name="projected">Whether to derive the shape from an accepted definition instead of the catalog.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUuidPhysicalShapeUsesSixteenFixedBytes(bool projected)
    {
        // Arrange
        using var context = CreateNativeUuidContext();
        var definition = NativeUuid("id");

        // Act
        var known = true;
        MySqlAlterColumnPhysicalShape shape;
        if (projected)
        {
            known = MySqlProjectedAlterPhysicalSnapshot.TryCreateProjectedColumn(
                definition, context.GetService<IRelationalTypeMappingSource>(), 4, out shape);
        }
        else
        {
            shape = Catalog("uuid");
        }

        // Assert
        Assert.True(known);
        Assert.Equal(16L, shape.DeclaredRowBytes);
        Assert.Equal(16L, shape.DynamicInlineBytes);
        Assert.Equal(16L, shape.CompactInlineBytes);
        Assert.Equal(16L, shape.IndexShape.FullWidthBytes);
        Assert.False(shape.IndexShape.SupportsPrefix);
        Assert.Null(shape.IndexShape.MaximumPrefixUnits);
    }

    /// <summary>Unrelated repairs include live and newly added UUID key columns without relaxing key limits.</summary>
    /// <param name="kind">
    /// Zero for a live index, one for a projected index, two for a primary key, or three for a unique key.
    /// </param>
    /// <param name="projectedUuid">Whether the UUID column was added earlier in the operation stream.</param>
    /// <param name="targetLength">The unrelated target VARCHAR width under its captured one-byte encoding.</param>
    /// <param name="expected">Whether the complete composite key fits the captured limit.</param>
    [Theory]
    [InlineData(0, false, 752, true)]
    [InlineData(0, false, 753, false)]
    [InlineData(0, true, 752, true)]
    [InlineData(0, true, 753, false)]
    [InlineData(1, false, 752, true)]
    [InlineData(1, false, 753, false)]
    [InlineData(1, true, 752, true)]
    [InlineData(1, true, 753, false)]
    [InlineData(2, false, 752, true)]
    [InlineData(2, false, 753, false)]
    [InlineData(2, true, 752, true)]
    [InlineData(2, true, 753, false)]
    [InlineData(3, false, 752, true)]
    [InlineData(3, false, 753, false)]
    [InlineData(3, true, 752, true)]
    [InlineData(3, true, 753, false)]
    public void NativeUuidSiblingRetainsCompositeKeyBoundaries(
        int kind,
        bool projectedUuid,
        int targetLength,
        bool expected
    )
    {
        // Arrange
        using var context = CreateNativeUuidContext();
        var original = new Dictionary<string, MySqlAlterColumnPhysicalShape>(StringComparer.Ordinal)
        {
            ["value"] = Catalog("longtext", characterBytes: 1),
        };

        if (!projectedUuid)
        {
            original.Add("id", Catalog("uuid"));
        }

        var table = new TableView(projectedUuid
            ? [NativeUuid("id"), Character("value", "longtext")]
            : [Character("value", "longtext")])
        {
            Indexes = kind == 1 ? [new ExpectedIndexDefinition("ix_values", "records",
                [new ExpectedIndexKeyDefinition("id"), new ExpectedIndexKeyDefinition("value")])] : [],
            PrimaryKey = kind == 2 ? new ExpectedPrimaryKeyDefinition("pk_records", "records", ["id", "value"]) : null,
            UniqueConstraints = kind == 3
                ? [new ExpectedUniqueConstraintDefinition("uq_records", "records", ["id", "value"])] : [],
        };

        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(original,
            kind == 0 ? [new MySqlAlterIndexPhysicalShape(false,
                [new MySqlAlterIndexPart("id", null), new MySqlAlterIndexPart("value", null)])] : [],
            Environment(maximumKeyBytes: 768));

        // Act
        var result = snapshot.IsSafe(
            Alter("value", targetLength), table, context.GetService<IRelationalTypeMappingSource>());

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A native UUID sibling consumes its full fixed width at the declared SQL row boundary.</summary>
    /// <param name="binaryLength">The retained binary sibling's declared capacity.</param>
    /// <param name="expected">Whether the total declared row stays at or below 65,535 bytes.</param>
    [Theory]
    [InlineData(65515, true)]
    [InlineData(65516, false)]
    public void NativeUuidSiblingParticipatesInDeclaredRowBoundary(int binaryLength, bool expected)
    {
        // Arrange
        var snapshot = Snapshot(("id", Catalog("uuid")), ("payload", Catalog("varbinary", binaryLength, 1)),
            ("value", Catalog("longtext", characterBytes: 1)));

        var table = new TableView(Character("value", "longtext"));

        // Act
        var result = snapshot.IsSafe(Alter("value", 1), table, Mappings);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Fixed UUID fields remain inline and cannot borrow variable-field overflow capacity.</summary>
    /// <param name="targetLength">The unrelated one-byte VARCHAR target's capacity.</param>
    /// <param name="expected">Whether the complete in-page record fits the conservative ceiling.</param>
    [Theory]
    [InlineData(26, true)]
    [InlineData(27, false)]
    public void NativeUuidSiblingsParticipateInInlineRowBoundary(int targetLength, bool expected)
    {
        // Arrange
        var columns = Enumerable.Range(0, 98).ToDictionary(
            index => $"id_{index.ToString(CultureInfo.InvariantCulture)}",
            _ => Catalog("uuid"), StringComparer.Ordinal);

        columns.Add("value", Catalog("longtext", characterBytes: 1));
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(columns, [], Environment(pageSize: 4096));
        var table = new TableView(Character("value", "longtext"));

        // Act
        var result = snapshot.IsSafe(Alter("value", targetLength), table, Mappings);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Native UUID indexes cannot acquire prefix semantics during an unrelated repair.</summary>
    /// <param name="projectedIndex">Whether the invalid index was accepted earlier in the stream.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUuidSiblingPrefixDoesNotAuthorizeRepair(bool projectedIndex)
    {
        // Arrange
        var columns = new Dictionary<string, MySqlAlterColumnPhysicalShape>(StringComparer.Ordinal)
        {
            ["id"] = Catalog("uuid"),
            ["value"] = Catalog("longtext"),
        };

        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(columns,
            projectedIndex ? [] : [new MySqlAlterIndexPhysicalShape(false, [new MySqlAlterIndexPart("id", 1)])],
            Environment());

        var table = new TableView(Character("value", "longtext"))
        {
            Indexes = projectedIndex ? [new ExpectedIndexDefinition("ix_id", "records",
                [new ExpectedIndexKeyDefinition("id", prefixLength: 1)])] : [],
        };

        // Act
        var result = snapshot.IsSafe(Alter("value", 80), table, Mappings);

        // Assert
        Assert.False(result);
    }

    /// <summary>All accepted widths participate in the same captured composite index.</summary>
    [Fact]
    public void CompositeLiveIndexRejectsCumulativeWidthOverflow()
    {
        var snapshot = CompositeSnapshot(3072, 4);
        var table = new TableView(Varchar("left_value", 400), Varchar("right_value", 200));

        var result = snapshot.IsSafe(Alter("right_value", 400), table, Mappings);

        Assert.False(result);
    }

    /// <summary>The physical source character set survives table renaming.</summary>
    [Fact]
    public void CompositeLiveIndexRetainsActualAsciiWidthAcrossRename()
    {
        var snapshot = CompositeSnapshot(3072, 1);
        var table = new TableView(Varchar("left_value", 1000, "latin1_bin"), Varchar("right_value", 200));

        var result = snapshot.IsSafe(Alter("right_value", 1000), table, Mappings);

        Assert.True(result);
    }

    /// <summary>An accepted encoding change cannot reuse the source column's one-byte budget.</summary>
    [Fact]
    public void ChangedSiblingCollationInvalidatesCapturedCharacterWidth()
    {
        var snapshot = CompositeSnapshot(3072, 1);
        var table = new TableView(Varchar("left_value", 800, "utf8mb4_bin"), Varchar("right_value", 200));

        var result = snapshot.IsSafe(Alter("right_value", 200), table, Mappings);

        Assert.False(result);
    }

    /// <summary>A COMPACT source must not inherit the more permissive default row format.</summary>
    [Fact]
    public void CompactSourceKeepsItsRestrictiveCompositeKeyBudget()
    {
        var snapshot = CompositeSnapshot(767, 4);
        var table = new TableView(Varchar("left_value", 96), Varchar("right_value", 80));

        var result = snapshot.IsSafe(Alter("right_value", 96), table, Mappings);

        Assert.False(result);
    }

    /// <summary>An accepted index added before this Alter is included without a new database roundtrip.</summary>
    [Fact]
    public void ProjectedIndexParticipatesInCumulativeWidthValidation()
    {
        var snapshot = Snapshot(("left_value", Catalog("varchar", 200)), ("right_value", Catalog("varchar", 200)));
        var table = new TableView(Varchar("left_value", 400), Varchar("right_value", 200))
        {
            Indexes = [new ExpectedIndexDefinition("ix_values", "records",
                [new ExpectedIndexKeyDefinition("left_value"), new ExpectedIndexKeyDefinition("right_value")])],
        };

        var result = snapshot.IsSafe(Alter("right_value", 400), table, Mappings);

        Assert.False(result);
    }

    /// <summary>Missing and unknown physical contributors fail closed.</summary>
    /// <param name="missingTarget">Whether the current source column is absent.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompletePhysicalEvidenceCannotApproveAnAlter(bool missingTarget)
    {
        var snapshot = missingTarget
            ? Snapshot(("other", Catalog("longtext")))
            : Snapshot(("value", Catalog("longtext")), ("other", Catalog("unknown_family")));

        var table = new TableView(Character("value", "longtext"));

        var result = snapshot.IsSafe(Alter("value", 800), table, Mappings);

        Assert.False(result);
    }

    /// <summary>A retained prefix cannot exceed the target column's character capacity.</summary>
    [Fact]
    public void RetainedPrefixLongerThanTargetIsRejected()
    {
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(
            new Dictionary<string, MySqlAlterColumnPhysicalShape> { ["value"] = Catalog("longtext") },
            [new MySqlAlterIndexPhysicalShape(false, [new MySqlAlterIndexPart("value", 128)])], Environment());

        var table = new TableView(Character("value", "longtext"));

        var result = snapshot.IsSafe(Alter("value", 64), table, Mappings);

        Assert.False(result);
    }

    /// <summary>Common fixed and binary columns have conservative bounded declared widths.</summary>
    /// <param name="storeType">The supported physical family.</param>
    /// <param name="width">The expected upper-bound row contribution.</param>
    [Theory]
    [InlineData("char(36)", 144)]
    [InlineData("binary(16)", 16)]
    [InlineData("varbinary(255)", 256)]
    [InlineData("varbinary(256)", 258)]
    [InlineData("varchar(63)", 253)]
    [InlineData("varchar(64)", 258)]
    [InlineData("int unsigned", 4)]
    [InlineData("float(53)", 8)]
    public void SharedDeclaredWidthSupportsCommonCompanionColumns(
        string storeType,
        long width
    )
    {
        var definition = Character("value", storeType);

        var known = MySqlProjectedAlterPhysicalSnapshot.TryCreateProjectedColumn(
            definition, Mappings, 4, out var shape);

        Assert.True(known);
        Assert.Equal(width, shape.DeclaredRowBytes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _context.Dispose();
        _nativeServices?.Dispose();
    }

    /// <summary>Sibling signed widenings also share the captured inline-row budget, not just key capacity.</summary>
    /// <param name="earlierWidening">Whether an earlier accepted sibling already consumed four bytes.</param>
    /// <param name="columnAliases">Whether accepted and current native member references use alternate casing.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IndexDropWideningsRetainTheCumulativeInlineRowBudget(bool earlierWidening, bool columnAliases)
    {
        // Arrange
        var original = Enumerable.Range(0, 10).ToDictionary(
            static index => "padding_" + index.ToString(CultureInfo.InvariantCulture),
            static _ => Catalog("char", 251), StringComparer.OrdinalIgnoreCase);

        original.Add("binary_padding", Catalog("binary", 220, 1));
        original.Add("first", Catalog("int"));
        original.Add("second", Catalog("int"));
        original.Add("third", Catalog("int"));
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(original, [], Environment());
        var source = new TableView(new ExpectedColumnDefinition(columnAliases ? "FIRST" : "first",
            earlierWidening ? typeof(long) : typeof(int), false, earlierWidening ? "bigint" : "int"));

        var intent = new AlterColumnIntent("records",
            new ExpectedColumnDefinition(columnAliases ? "SECOND" : "second", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition(columnAliases ? "SECOND" : "second", typeof(int), false, "int"));

        // Act
        var result = snapshot.IsSafeAfterIndexDrops(intent, source, Mappings);

        // Assert
        Assert.Equal(!earlierWidening, result);
    }

    /// <summary>Complete-table member aliases retain the target's full row and key costs.</summary>
    /// <param name="columnAliases">Whether the target has alternate valid column-member casing.</param>
    /// <param name="boundary">The independent complete-table capacity boundary.</param>
    [Theory]
    [InlineData(false, "fits")]
    [InlineData(true, "fits")]
    [InlineData(false, "row")]
    [InlineData(true, "row")]
    [InlineData(false, "key")]
    [InlineData(true, "key")]
    public void CompleteProjectedColumnAliasesRetainRowAndKeyBudgets(bool columnAliases, string boundary)
    {
        // Arrange
        var source = Varchar("value", 100);
        var target = Varchar(columnAliases ? "VALUE" : "value",
            boundary == "row" ? 8000 : boundary == "key" ? 900 : 200);
        var table = new TableView(source, Varchar("companion", boundary == "row" ? 9000 : 100))
        {
            Indexes = boundary == "key"
                ? [new ExpectedIndexDefinition("ix_value", "records", [new ExpectedIndexKeyDefinition("value")])]
                : [],
        };

        var intent = new AlterColumnIntent("records", target, Varchar(target.Name, 100));

        // Act
        var result = MySqlProjectedColumnTransition.IsSafe(intent, source, table, Environment(), Mappings,
            canReuseCreationCharacterSet: true);

        // Assert
        Assert.Equal(boundary == "fits", result);
    }

    private IRelationalTypeMappingSource Mappings => _context.GetService<IRelationalTypeMappingSource>();

    private static MySqlProjectedAlterPhysicalSnapshot Snapshot(
        params (string Name, MySqlAlterColumnPhysicalShape Shape)[] columns
    ) => new(columns.ToDictionary(static column => column.Name, static column => column.Shape, StringComparer.Ordinal),
        [], Environment());

    private static MySqlProjectedAlterPhysicalSnapshot CompositeSnapshot(
        int maximumKeyBytes,
        int characterBytes
    ) => new(
        new Dictionary<string, MySqlAlterColumnPhysicalShape>
        {
            ["left_value"] = Catalog("varchar", 200, characterBytes),
            ["right_value"] = Catalog("varchar", 200, characterBytes),
        },
        [new MySqlAlterIndexPhysicalShape(false,
            [new MySqlAlterIndexPart("left_value", null), new MySqlAlterIndexPart("right_value", null)])],
        Environment(maximumKeyBytes, maximumKeyBytes == 767 ? "Compact" : "Dynamic"));

    private static SafeMigrationIndexPhysicalEnvironment Environment(
        int maximumKeyBytes = 3072,
        string rowFormat = "Dynamic",
        int pageSize = 16384
    ) => new(maximumKeyBytes, StorageRowFormat: rowFormat, StoragePageSize: pageSize,
        DefaultCharacterSet: "utf8mb4", StrictSqlMode: true);

    private static MySqlAlterColumnPhysicalShape Catalog(
        string type,
        long? length = null,
        int characterBytes = 4
    ) =>
        MySqlProjectedAlterPhysicalSnapshot.CreateCatalogColumn(type, false, length, length * characterBytes,
            null, null, null, characterBytes, characterBytes == 1 ? "latin1_bin" : "utf8mb4_bin",
            characterBytes == 1 ? "latin1" : "utf8mb4");

    private static ExpectedColumnDefinition Character(
        string name,
        string storeType
    ) => new(
        name, typeof(string), isNullable: false, storeType: storeType);

    /// <summary>Describes fixed native UUID storage without character or binary length facets.</summary>
    /// <param name="name">The exact column name.</param>
    /// <returns>A native UUID column with no textual length facets.</returns>
    private static ExpectedColumnDefinition NativeUuid(string name) => new(
        name, typeof(Guid), isNullable: false, storeType: "uuid");

    private static ExpectedColumnDefinition Varchar(
        string name,
        int length,
        string? collation = null
    ) => new(
        name, typeof(string), isNullable: false,
        storeType: $"varchar({length.ToString(CultureInfo.InvariantCulture)})", maxLength: length,
        collation: collation is null ? null : new SafeMigrationCollationIdentifier(collation));

    private static AlterColumnIntent Alter(
        string column,
        int length
    ) => new(
        "renamed_records", Varchar(column, length), Character(column, "longtext"));

    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        return new DbContext(options.Options);
    }

    /// <summary>Resolves native UUID mappings without opening a server connection.</summary>
    /// <returns>A context configured for a capable MariaDB profile.</returns>
    private DbContext CreateNativeUuidContext()
    {
        // WHY: Capability-profile probes own their provider lifetime instead
        // of retaining additional configurations in EF's process-wide cache.
        _nativeServices ??= new ServiceCollection().AddEntityFrameworkDokaMySql()
            .BuildServiceProvider(validateScopes: true);

        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseInternalServiceProvider(_nativeServices);
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)));

        return new DbContext(options.Options);
    }

    private sealed class TableView(params ExpectedColumnDefinition[] definitions) : ISafeMigrationProjectedAlterTable
    {
        private readonly Dictionary<string, ExpectedColumnDefinition> _columns = definitions
            .ToDictionary(static definition => definition.Name, StringComparer.OrdinalIgnoreCase);

        public IEnumerable<ExpectedColumnDefinition> Columns => _columns.Values;

        /// <inheritdoc />
        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; init; }

        /// <inheritdoc />
        public IEnumerable<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; init; } = [];

        public IEnumerable<ExpectedIndexDefinition> Indexes { get; init; } = [];

        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => _columns.TryGetValue(column, out definition);
    }
}
