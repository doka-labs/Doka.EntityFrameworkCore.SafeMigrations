namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies typed model-managed key labels separately from owners, values and row order.</summary>
public sealed partial class SafeMigrationModelManagedKeyIdentityTests
{
    /// <summary>Case-equivalent key labels select one final writer only with certified column identity.</summary>
    /// <param name="insensitive">Whether the provider certifies ordinal-ignore-case column identity.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedPostflightKeyColumnIdentityRequiresProviderOptIn(bool insensitive)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive);
        MigrationOperation[] operations =
        [
            Operation(Ensure("Items", "Id", new object?[,] { { 1, 7 } })),
            Operation(Update("Items", "ID", new object?[,] { { 1 } })),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(insensitive, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Column folding cannot change owners, qualifiers, different columns or typed key values.</summary>
    /// <param name="boundary">The independently different part of the row contract.</param>
    [Theory]
    [InlineData("owner")]
    [InlineData("schema")]
    [InlineData("column")]
    [InlineData("key_value")]
    [InlineData("key_type")]
    public void ModelManagedColumnFoldingRetainsOtherIdentityBoundaries(string boundary)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        var earlier = Ensure("Items", "Id", new object?[,] { { 1, 7 } });
        var later = Update(boundary == "owner" ? "items" : "Items",
            boundary == "column" ? "OtherId" : "ID",
            new object?[,] { { boundary == "key_value" ? 2 : boundary == "key_type" ? (object)1L : 1 } },
            boundary == "schema" ? "OtherSchema" : null);

        MigrationOperation[] operations = [Operation(earlier), Operation(later)];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Final-writer keys are an exact row set rather than an authored row-order contract.</summary>
    /// <param name="changedSet">Whether the later row set includes a different key.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedRowSetRetainsExactValuesButNotAuthoredRowOrder(bool changedSet)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(Ensure("Items", "Id", new object?[,] { { 1, 7 }, { 2, 7 }, { 3, 7 } })),
            Operation(Update("Items", "ID", new object?[,] { { 3 }, { changedSet ? 4 : 1 }, { 2 } })),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(!changedSet, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>The inherited SQLite-style normalizer folds ASCII labels but never Unicode case variants.</summary>
    /// <param name="unicode">Whether the names differ by non-ASCII rather than ASCII case.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedDefaultAsciiNormalizerPreservesUnicodeIdentity(bool unicode)
    {
        // Arrange
        var identity = new AsciiKeyNormalizer();
        MigrationOperation[] operations =
        [
            Operation(Ensure("Items", unicode ? "\u00c4" : "Id", new object?[,] { { 1, 7 } })),
            Operation(Update("items", unicode ? "\u00e4" : "ID", new object?[,] { { 1 } })),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(!unicode, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Normalization occurs once per operation key column, not once for every managed row.</summary>
    [Fact]
    public void ModelManagedPostflightNormalizesColumnsOncePerIntent()
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(Ensure("Items", "Id", new object?[,] { { 1, 7 }, { 2, 7 }, { 3, 7 } })),
            Operation(Update("Items", "ID", new object?[,] { { 3 }, { 1 }, { 2 } })),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(2, identity.ColumnNormalizationCalls);
        Assert.True(projection.IsSuperseded(0));
    }

    /// <summary>Canonical key columns reuse exact input or allocate one independently owned changed array.</summary>
    /// <param name="changed">Whether at least one authored label needs canonicalization.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedCanonicalColumnsAllocateOnlyAfterActualSpellingChange(bool changed)
    {
        // Arrange
        string[] columns = [changed ? "Id" : "ID", "CODE"];
        var identity = new ColumnKeyNormalizer(insensitive: true);

        // Act
        var canonical = SafeMigrationModelManagedKeyIdentity.NormalizeColumns(columns, identity);
        columns[0] = "OTHER";

        // Assert
        Assert.Equal(changed, !ReferenceEquals(columns, canonical));
        Assert.Equal(changed ? "ID" : "OTHER", canonical[0]);
        Assert.Equal("CODE", canonical[1]);
        Assert.Equal(2, identity.ColumnNormalizationCalls);
    }

    /// <summary>Canonicalization agrees with OrdinalIgnoreCase at Unicode and surrogate boundaries.</summary>
    /// <param name="left">The first typed identifier.</param>
    /// <param name="right">The second typed identifier, including possible independently different text.</param>
    [Theory]
    [InlineData("Id", "ID")]
    [InlineData("\u0131tail", "ITAIL")]
    [InlineData("\u0131tail", "\u0131TAIL")]
    [InlineData("\u017ftail", "STAIL")]
    [InlineData("\u017ftail", "\u017fTAIL")]
    [InlineData("\u212atail", "KTAIL")]
    [InlineData("\u212atail", "\u212aTAIL")]
    [InlineData("\u00e4tail", "\u00c4TAIL")]
    [InlineData("\U00010428tail", "\U00010400TAIL")]
    [InlineData("\U00010428tail", "\U00010401TAIL")]
    [InlineData("\ud800tail", "\ud800TAIL")]
    [InlineData("\ud800tail", "\ud801TAIL")]
    public void ModelManagedOrdinalColumnNormalizationHasNoUnicodeOvermerge(string left, string right)
    {
        // Arrange
        var expectedEqual = StringComparer.OrdinalIgnoreCase.Equals(left, right);

        // Act
        var normalizedLeft = SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(left);
        var normalizedRight = SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(right);

        // Assert
        Assert.Equal(expectedEqual, StringComparer.Ordinal.Equals(normalizedLeft, normalizedRight));
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(left, normalizedLeft));
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(right, normalizedRight));
    }

    /// <summary>Preflight resolves key labels and recorded values using certified column identity.</summary>
    /// <param name="insensitive">Whether the provider certifies case-equivalent column labels.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedPreflightKeyAndValueLabelsUseColumnIdentity(bool insensitive)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive);
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: identity);
        Accept(projection, Operation(Ensure("Items", "Id", new object?[,] { { 1, 7 } })));
        var update = Operation(new UpdateModelManagedDataIntent("Items", ["ID"], ["int"],
            new object?[,] { { 1 } }, ["value"], ["int"], new object?[,] { { 7 } }, new object?[,] { { 8 } },
            schema: null, uniqueKeys: null));

        // Act
        var analysis = projection.Project(update, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(insensitive ? SafeMigrationObservedState.TransitionReady
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Typed column canonicalization cannot prove a different pending key's uniqueness.</summary>
    /// <param name="differentColumn">Whether the index targets the deliberately duplicate unproven payload.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedCandidateKeyFingerprintUsesColumnIdentity(bool differentColumn)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnKeyNormalizer(insensitive: true));

        Accept(projection, Operation(new EnsureTableIntent(new ExpectedTableDefinition("Items",
            [Column("Id"), Column("Value"), Column("Payload")]), SafeMigrationTableMode.StrictDefinition)));
        Accept(projection, Operation(new EnsureModelManagedDataIntent("Items", ["Id"], ["int"],
            ["Id", "Value", "Payload"], ["int", "int", "int"], new object?[,] { { 1, 7, 8 }, { 2, 9, 8 } },
            schema: null, uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Value"])])));
        var index = Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("pending_value", "Items",
            [new ExpectedIndexKeyDefinition(differentColumn ? "Payload" : "value")], unique: true)));

        // Act
        var analysis = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(differentColumn ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Unique collision checks use the same physical row owner identity as the stored evidence.</summary>
    [Fact]
    public void ModelManagedUniqueUpdateDoesNotCollideWithAsciiEquivalentSelfOwner()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: new AsciiKeyNormalizer());
        Accept(projection, Operation(new EnsureModelManagedDataIntent("Items", ["Id"], ["int"],
            ["Id", "Value", "Payload"], ["int", "int", "int"], new object?[,] { { 1, 7, 8 } },
            schema: null, uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Value"])])));
        var update = Operation(new UpdateModelManagedDataIntent("items", ["ID"], ["int"],
            new object?[,] { { 1 } }, ["Value", "Payload"], ["int", "int"],
            new object?[,] { { 7, 8 } }, new object?[,] { { 7, 9 } }, schema: null,
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Value"])]));

        // Act
        var analysis = projection.Project(update, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.TransitionReady, analysis.ObservedState);
        Assert.False(analysis.IsModelManagedProjectionUnknown);
    }

    /// <summary>Creates a required integer column without defaults, expressions or provider side effects.</summary>
    private static ExpectedColumnDefinition Column(string name) => new(name, typeof(int), false, "int");

    /// <summary>Creates one immutable managed row-set contract with an explicit typed key label.</summary>
    private static EnsureModelManagedDataIntent Ensure(string table, string key, object?[,] values) =>
        new(table, [key], ["int"], [key, "Value"], ["int", "int"], values, schema: null, uniqueKeys: null);

    /// <summary>Creates a same-row update whose value change does not affect physical row identity.</summary>
    private static UpdateModelManagedDataIntent Update(
        string table,
        string key,
        object?[,] keyValues,
        string? schema = null
    )
    {
        var oldValues = new object?[keyValues.GetLength(0), 1];
        var newValues = new object?[keyValues.GetLength(0), 1];
        for (var row = 0; row < oldValues.GetLength(0); row++)
        {
            oldValues[row, 0] = 7;
            newValues[row, 0] = 8;
        }

        return new UpdateModelManagedDataIntent(table, [key], ["int"], keyValues, ["Value"], ["int"],
            oldValues, newValues, schema, uniqueKeys: null);
    }

    /// <summary>Creates one strict safe operation without inventing provider compatibility facts.</summary>
    private static SafeMigrationOperation Operation(SafeMigrationIntent intent) =>
        new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates immutable native evidence used only when no ordered row evidence exists.</summary>
    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state) =>
        new(state, SafeMigrationRepairCapability.None, false, "test_live");

    /// <summary>Accepts a fixture only through the normal decision and observation pipeline.</summary>
    private static void Accept(SafeMigrationPreflightProjection projection, SafeMigrationOperation operation)
    {
        var live = Live(SafeMigrationObservedState.Missing);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        projection.Observe(operation, live, analysis, decision);
    }

    /// <summary>Separates ordinal owner identity from explicitly opted-in ordinal-ignore-case columns.</summary>
    private sealed class ColumnKeyNormalizer(bool insensitive) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <summary>Gets the number of typed-column canonicalization calls.</summary>
        public int ColumnNormalizationCalls { get; private set; }

        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public StringComparer ColumnIdentifierComparer =>
            insensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <inheritdoc />
        public string NormalizeColumnIdentifier(string identifier)
        {
            ColumnNormalizationCalls++;

            return insensitive
                ? SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(identifier) : identifier;
        }

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }

    /// <summary>Uses inherited typed-column normalization to model SQLite's ASCII-only identity boundary.</summary>
    private sealed class AsciiKeyNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        private static readonly StringComparer s_comparer = new AsciiComparer();

        /// <inheritdoc />
        public StringComparer IdentifierComparer => s_comparer;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => NormalizeAscii(identifier);

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;

        /// <summary>Folds ASCII only; non-ASCII code units remain exactly authored.</summary>
        private static string NormalizeAscii(string value) => string.Create(value.Length, value,
            static (destination, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var character = source[index];
                    destination[index] = character is >= 'A' and <= 'Z'
                        ? (char)(character + ('a' - 'A')) : character;
                }
            });

        /// <summary>Provides matching equality and hashing for the fixture's ASCII-normalized identifiers.</summary>
        private sealed class AsciiComparer : StringComparer
        {
            /// <inheritdoc />
            public override int Compare(string? x, string? y) => StringComparer.Ordinal.Compare(
                x is null ? null : NormalizeAscii(x), y is null ? null : NormalizeAscii(y));

            /// <inheritdoc />
            public override bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(
                x is null ? null : NormalizeAscii(x), y is null ? null : NormalizeAscii(y));

            /// <inheritdoc />
            public override int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(NormalizeAscii(obj));
        }
    }
}
