namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Verifies only final resource writers supersede earlier checks while owner, row and provider boundaries remain
/// distinct.
/// </summary>
public sealed class SafeMigrationPostflightProjectionTests
{
    /// <summary>
    /// Gets source removal, target removal and final rename contracts for every renamed object family.
    /// </summary>
    public static TheoryData<SafeMigrationIntent, SafeMigrationIntent, SafeMigrationIntent>
        RenameFinalWriterCases => new()
    {
        { new DropTableIntent("source"), new DropTableIntent("target"), new RenameTableIntent("source", "target") },
        {
            new DropColumnIntent("source", "records"), new DropColumnIntent("target", "records"),
            new RenameColumnIntent("source", "records", "target")
        },
        {
            new DropIndexIntent("source", "records"), new DropIndexIntent("target", "records"),
            new RenameIndexIntent("source", "records", "target")
        },
    };

    /// <summary>
    /// Lets the later safe index writer supersede its earlier drop across ordinary metadata-only table and column
    /// DDL.
    /// </summary>
    [Fact]
    public void LaterWriterSupersedesAnEarlierPostconditionAcrossProviderOperations()
    {
        // Arrange
        const string indexName = "ix_records_tenant_id_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "records")),
            new AlterTableOperation
            {
                Name = "records",
                Comment = "target",
            },
            new AlterColumnOperation
            {
                Name = "code",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(180)",
                IsNullable = false,
            },
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        indexName,
                        "records",
                        [
                            new ExpectedIndexKeyDefinition(column: "tenant_id"),
                            new ExpectedIndexKeyDefinition(column: "code", prefixLength: 48),
                        ]))),
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(3));
    }

    /// <summary>
    /// Keeps identically named indexes on different owners as independent final postconditions.
    /// </summary>
    [Fact]
    public void SameObjectNameOnAnotherTableDoesNotSupersedeThePostcondition()
    {
        // Arrange
        const string indexName = "ix_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "source_records")),
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        indexName,
                        "target_records",
                        [new ExpectedIndexKeyDefinition(column: "code")]))),
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Does not treat ordinary provider index DDL as a certified replacement for an earlier safe postcondition.
    /// </summary>
    [Fact]
    public void ProviderOperationCannotSupersedeAnEarlierSafePostcondition()
    {
        // Arrange
        const string indexName = "ix_records_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "records")),
            new CreateIndexOperation
            {
                Name = indexName,
                Table = "records",
                Columns = ["code"],
            },
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Makes only the last safe column writer authoritative across ensure, alter and removal of one physical
    /// member.
    /// </summary>
    [Fact]
    public void OnlyTheFinalWriterRemainsAuthoritativeForOneResource()
    {
        // Arrange
        var operations = new MigrationOperation[]
        {
            Safe(new EnsureColumnIntent("records", Column("code", "varchar(80)"))),
            Safe(new AlterColumnIntent("records", Column("code", "varchar(120)"))),
            Safe(new DropColumnIntent("code", "records")),
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.True(projection.IsSuperseded(1));
        Assert.False(projection.IsSuperseded(2));
    }

    /// <summary>
    /// Retains the final rename's source and target postconditions while superseding earlier writers at both
    /// endpoints.
    /// </summary>
    /// <param name="sourceWriter">
    /// The earlier writer for the rename's source resource.
    /// </param>
    /// <param name="targetWriter">
    /// The earlier writer for the rename's target resource.
    /// </param>
    /// <param name="rename">
    /// The complete final rename operation for the same resource family.
    /// </param>
    [Theory]
    [MemberData(nameof(RenameFinalWriterCases))]
    public void RenameSupersedesEarlierWritersForItsSourceAndTarget(
        SafeMigrationIntent sourceWriter,
        SafeMigrationIntent targetWriter,
        SafeMigrationIntent rename
    )
    {
        // Arrange
        var sourceOperations = new MigrationOperation[] { Safe(sourceWriter), Safe(rename) };
        var targetOperations = new MigrationOperation[] { Safe(targetWriter), Safe(rename) };

        // Act
        var sourceProjection = new SafeMigrationPostflightProjection(sourceOperations);
        var targetProjection = new SafeMigrationPostflightProjection(targetOperations);

        // Assert
        Assert.True(sourceProjection.IsSuperseded(0));
        Assert.False(sourceProjection.IsSuperseded(1));
        Assert.True(targetProjection.IsSuperseded(0));
        Assert.False(targetProjection.IsSuperseded(1));
    }

    /// <summary>
    /// Keeps the rename's source-removal postcondition live when a later writer changes only its target.
    /// </summary>
    /// <param name="_">
    /// The unused counterpart retained by the shared rename-case fixture.
    /// </param>
    /// <param name="targetWriter">
    /// The earlier writer for the rename's target resource.
    /// </param>
    /// <param name="rename">
    /// The complete final rename operation for the same resource family.
    /// </param>
    [Theory]
    [MemberData(nameof(RenameFinalWriterCases))]
    public void LaterTargetWriterDoesNotSupersedeRenameSourcePostcondition(
        SafeMigrationIntent _,
        SafeMigrationIntent targetWriter,
        SafeMigrationIntent rename
    )
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Safe(rename),
            Safe(targetWriter),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Supersedes the rename's source-removal postcondition only when a later writer addresses that same source.
    /// </summary>
    /// <param name="sourceWriter">
    /// The earlier writer for the rename's source resource.
    /// </param>
    /// <param name="_">
    /// The unused counterpart retained by the shared rename-case fixture.
    /// </param>
    /// <param name="rename">
    /// The complete final rename operation for the same resource family.
    /// </param>
    [Theory]
    [MemberData(nameof(RenameFinalWriterCases))]
    public void LaterSourceWriterSupersedesRenameSourcePostcondition(
        SafeMigrationIntent sourceWriter,
        SafeMigrationIntent _,
        SafeMigrationIntent rename
    )
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Safe(rename),
            Safe(sourceWriter),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Normalizes current-database qualifiers for every structural and managed-row resource without reversing final
    /// writers.
    /// </summary>
    /// <param name="earlierIsQualified">
    /// Whether the earlier writer uses the current database qualifier instead of the later writer.
    /// </param>
    /// <param name="resourceOrdinal">The independent table, index, constraint or managed-row resource pair.</param>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    public void ProviderNormalizedIdentitySupersedesAcrossEveryQualifiedResourceFamily(
        bool earlierIsQualified,
        int resourceOrdinal
    )
    {
        // Arrange
        const string database = "application";
        var earlier = CreateResourceWriters(
            earlierIsQualified
                ? database
                : null,
            drop: true);

        var later = CreateResourceWriters(
            earlierIsQualified
                ? null
                : database,
            drop: false);

        var normalizer = new CurrentDatabaseIdentityNormalizer(database);

        // Act
        var projection = new SafeMigrationPostflightProjection(
            [earlier[resourceOrdinal], later[resourceOrdinal]],
            normalizer);

        // Assert
        Assert.Equal(7, earlier.Length);
        Assert.Equal(earlier.Length, later.Length);
        Assert.True(
            projection.IsSuperseded(0),
            $"Resource pair {resourceOrdinal} ({earlier[resourceOrdinal].Intent.GetType().Name}, "
            + $"{later[resourceOrdinal].Intent.GetType().Name}) was not normalized to one physical identity. "
            + $"Earlier={Describe(earlier[resourceOrdinal].Intent)}, "
            + $"later={Describe(later[resourceOrdinal].Intent)}.");

        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Keeps foreign-database resources separate from the current database despite equal local owner and object
    /// names.
    /// </summary>
    [Fact]
    public void ForeignDatabaseIdentityDoesNotSupersedeCurrentDatabaseResource()
    {
        // Arrange
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent("ix_records_code", "records", "application")),
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_records_code",
                        "records",
                        [new ExpectedIndexKeyDefinition(column: "code")],
                        schema: "foreign"))),
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(
            operations,
            new CurrentDatabaseIdentityNormalizer("application"));

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>
    /// Uses the provider's certified case-insensitive identity to select the single final physical index writer.
    /// </summary>
    [Fact]
    public void ProviderIdentifierNormalizationSupersedesCaseVariantPhysicalResource()
    {
        // Arrange
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent("IX_RECORDS_CODE", "RECORDS")),
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_records_code",
                        "records",
                        [new ExpectedIndexKeyDefinition(column: "code")]))),
        };

        // Act
        var projection = new SafeMigrationPostflightProjection(
            operations,
            new CaseInsensitiveIdentityNormalizer());

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    private static SafeMigrationOperation Safe(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static string Describe(
        SafeMigrationIntent intent
    ) => intent switch
    {
        DropTableIntent value => $"{value.Schema}.{value.Table}",
        EnsureTableIntent value => $"{value.Definition.Schema}.{value.Definition.Table}",
        _ => intent.GetType()
            .Name,
    };

    private static ExpectedColumnDefinition Column(
        string name,
        string storeType
    ) => new(name, typeof(string), isNullable: false, storeType: storeType);

    private static SafeMigrationOperation[] CreateResourceWriters(
        string? schema,
        bool drop
    )
    {
        var builder = new MigrationBuilder("Provider");
        if (drop)
        {
            builder.DropTableIfExists("records", schema);
            builder.DropIndexIfExists("ix_records_code", "records", schema);
            builder.DropPrimaryKeyIfExists("pk_records", "records", schema);
            builder.DropUniqueConstraintIfExists("uq_records_code", "records", schema);
            builder.DropCheckConstraintIfExists("ck_records_code", "records", schema);
            builder.DropForeignKeyIfExists("fk_records_parent", "records", schema);
        }
        else
        {
            builder.EnsureTable(
                new ExpectedTableDefinition(
                    "records",
                    [Column("code", "varchar(80)")],
                    schema: schema),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);

            builder.CreateIndexIfNotExists("ix_records_code", "records", ["code"], schema);
            builder.AddPrimaryKeyIfNotExists("pk_records", "records", ["code"], schema);
            builder.AddUniqueConstraintIfNotExists("uq_records_code", "records", ["code"], schema);
            builder.AddCheckConstraintIfNotExists("ck_records_code", "records", "code <> ''", schema);
            builder.AddForeignKeyIfNotExists(
                "fk_records_parent",
                "records",
                ["code"],
                "parents",
                ["code"],
                schema: schema);
        }

        builder.EnsureModelManagedDataFromModel(
            "records",
            ["code"],
            ["varchar(80)"],
            ["code"],
            ["varchar(80)"],
            new object?[,] { { "value" } },
            schema);

        return builder
            .Operations
            .Cast<SafeMigrationOperation>()
            .ToArray();
    }

    private sealed class CurrentDatabaseIdentityNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        private readonly string _currentDatabase;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="currentDatabase">The supplied current database for this fixture.</param>
        public CurrentDatabaseIdentityNormalizer(
            string currentDatabase
        )
        {
            _currentDatabase = currentDatabase;
        }

        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public string NormalizeIdentifier(
            string identifier
        ) => identifier;

        public string? NormalizeSchema(
            string? schema
        ) => StringComparer.Ordinal.Equals(schema, _currentDatabase)
            ? null
            : schema;

        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        ) => false;
    }

    private sealed class CaseInsensitiveIdentityNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.OrdinalIgnoreCase;

        public string NormalizeIdentifier(
            string identifier
        ) => identifier.ToUpperInvariant();

        public string? NormalizeSchema(
            string? schema
        ) => schema;

        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        ) => false;
    }
}
