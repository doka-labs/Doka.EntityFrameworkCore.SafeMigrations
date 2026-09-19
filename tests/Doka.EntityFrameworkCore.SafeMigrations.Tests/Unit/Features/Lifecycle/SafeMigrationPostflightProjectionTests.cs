namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationPostflightProjectionTests
{
    [Fact]
    public void LaterWriterSupersedesAnEarlierPostconditionAcrossProviderOperations()
    {
        const string indexName = "ix_records_tenant_id_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "records")),
            new AlterTableOperation { Name = "records", Comment = "target" },
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

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(3));
    }

    [Fact]
    public void SameObjectNameOnAnotherTableDoesNotSupersedeThePostcondition()
    {
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

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    [Fact]
    public void ProviderOperationCannotSupersedeAnEarlierSafePostcondition()
    {
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

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    [Fact]
    public void OnlyTheFinalWriterRemainsAuthoritativeForOneResource()
    {
        var operations = new MigrationOperation[]
        {
            Safe(new EnsureColumnIntent("records", Column("code", "varchar(80)"))),
            Safe(new AlterColumnIntent("records", Column("code", "varchar(120)"))),
            Safe(new DropColumnIntent("code", "records")),
        };

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.True(projection.IsSuperseded(0));
        Assert.True(projection.IsSuperseded(1));
        Assert.False(projection.IsSuperseded(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderNormalizedIdentitySupersedesAcrossEveryQualifiedResourceFamily(
        bool earlierIsQualified
    )
    {
        // Arrange
        const string database = "application";
        var earlier = CreateResourceWriters(earlierIsQualified ? database : null, drop: true);
        var later = CreateResourceWriters(earlierIsQualified ? null : database, drop: false);
        var normalizer = new CurrentDatabaseIdentityNormalizer(database);

        // Act
        Assert.Equal(earlier.Length, later.Length);
        var projections = new SafeMigrationPostflightProjection[earlier.Length];
        for (var index = 0; index < earlier.Length; index++)
        {
            projections[index] = new SafeMigrationPostflightProjection(
                [earlier[index], later[index]],
                normalizer);
        }

        // Assert
        for (var index = 0; index < projections.Length; index++)
        {
            Assert.True(
                projections[index].IsSuperseded(0),
                $"Resource pair {index} ({earlier[index].Intent.GetType().Name}, "
                + $"{later[index].Intent.GetType().Name}) was not normalized to one physical identity. "
                + $"Earlier={Describe(earlier[index].Intent)}, later={Describe(later[index].Intent)}.");
            Assert.False(projections[index].IsSuperseded(1));
        }
    }

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

    [Fact]
    public void ProviderIdentifierNormalizationSupersedesCaseVariantPhysicalResource()
    {
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

        var projection = new SafeMigrationPostflightProjection(
            operations,
            new CaseInsensitiveIdentityNormalizer());

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
        _ => intent.GetType().Name,
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

        return builder.Operations.Cast<SafeMigrationOperation>().ToArray();
    }

    private sealed class CurrentDatabaseIdentityNormalizer(
        string currentDatabase
    ) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public string NormalizeIdentifier(
            string identifier
        ) => identifier;

        public string? NormalizeSchema(
            string? schema
        ) => StringComparer.Ordinal.Equals(schema, currentDatabase) ? null : schema;

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
