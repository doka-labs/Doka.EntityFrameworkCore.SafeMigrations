namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlModelManagedDataLiteralTests
{
    [Theory]
    [InlineData("char(36)")]
    [InlineData("binary(16)")]
    public void InlineSqlGenerationUsesClrAwareMappingsForEveryModelManagedOperation(
        string guidStoreType
    )
    {
        using var context = CreateContext();
        var identifier = Guid.Parse("1458f03f-9acb-4902-bca8-280b613a7c92");
        var previousIdentifier = Guid.Parse("67c8a7f5-1264-4fe4-80ba-c6ed2b2492b4");
        var currentIdentifier = Guid.Parse("5568c996-b609-4082-87d8-f261abc9cb09");
        var timestamp = new DateTime(2026, 9, 5, 12, 34, 56, DateTimeKind.Utc);
        var operations = new SafeMigrationOperation[]
        {
            new(
                new EnsureModelManagedDataIntent(
                    "typed_rows",
                    ["id"],
                    [guidStoreType],
                    ["id", "symbol", "state", "day", "time", "timestamp", "payload", "json", "optional", "count"],
                    [
                        guidStoreType,
                        "char(1)",
                        "int",
                        "date",
                        "time(6)",
                        "datetime(6)",
                        "varbinary(8)",
                        "json",
                        "varchar(16)",
                        "int",
                    ],
                    new object?[,]
                    {
                        {
                            identifier,
                            'A',
                            LiteralState.Active,
                            new DateOnly(2026, 9, 5),
                            new TimeOnly(12, 34, 56),
                            timestamp,
                            new byte[] { 0x01, 0x02, 0x03, },
                            "{\"enabled\":true}",
                            null,
                            42,
                        },
                    },
                    schema: null,
                    uniqueKeys: null),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(
                new UpdateModelManagedDataIntent(
                    "typed_rows",
                    ["id"],
                    [guidStoreType],
                    new object?[,] { { identifier, }, },
                    ["symbol", "related_id"],
                    ["char(1)", guidStoreType],
                    new object?[,] { { 'A', previousIdentifier, }, },
                    new object?[,] { { 'B', currentIdentifier, }, },
                    schema: null,
                    uniqueKeys: null),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(
                new DeleteModelManagedDataIntent(
                    "typed_rows",
                    ["id"],
                    [guidStoreType],
                    new object?[,] { { identifier, }, },
                    ["id", "symbol"],
                    [guidStoreType, "char(1)"],
                    new object?[,] { { identifier, 'B', }, },
                    schema: null,
                    foreignKeys: null),
                SafeMigrationPolicy.ThrowIfDifferent),
        };

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(operations, context.Model);

        var payloads = commands
            .SelectMany(command => DecodeHexPayloads(command.CommandText))
            .ToArray();

        Assert.Equal(3, commands.Count);
        Assert.Contains(payloads, payload => payload.Contains("AS `k0`", StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains("AS `t1`", StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains("AS `o0`", StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains("AS `n0`", StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains("'A'", StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains("'B'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedClrAndStoreTypePairFailsClosed()
    {
        using var context = CreateContext();
        var mappingSource = context.GetService<IRelationalTypeMappingSource>();

        var exception = Assert.Throws<NotSupportedException>(() =>
            MySqlCatalogTypeMapping.Resolve(mappingSource, new UnsupportedLiteral(), "varchar(32)"));

        Assert.Contains(typeof(UnsupportedLiteral).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("varchar(32)", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedJsonUsesStructuralComparisonWithoutReclassifyingText(
        bool isMariaDb
    )
    {
        using var context = CreateContext(isMariaDb);
        var operation = new SafeMigrationOperation(
            new EnsureModelManagedDataIntent(
                "documents",
                ["id"],
                ["int"],
                ["id", "payload", "json_like_text"],
                ["int", "json", "varchar(128)"],
                new object?[,]
                {
                    { 1, "{\"b\":2,\"a\":1}", "{\"b\":2,\"a\":1}" },
                },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText).ToArray();
        var structuralComparison = isMariaDb
            ? "JSON_EQUALS(doka_actual.`payload`, doka_expected.`t1`)"
            : "doka_actual.`payload` <=> CAST(doka_expected.`t1` AS JSON)";

        Assert.Contains(
            payloads,
            payload => payload.Contains(structuralComparison, StringComparison.Ordinal));
        Assert.Contains(
            payloads,
            payload => payload.Contains(
                "doka_actual.`json_like_text` <=> doka_expected.`t2`",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderModelDifferPreservesEverySupportedJsonSeedShapeAsSafeProviderLiterals()
    {
        using var context = CreateJsonSeedContext();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source: null, model);
        var inverseOperations = differ.GetDifferences(model, target: null);

        var insert = Assert.Single(
            operations.OfType<InsertDataOperation>(),
            operation => operation.Table == "json_seed_rows");

        var paired = SafeMigrationModelManagedDataPairer.Pair(operations, inverseOperations);
        var safeInsert = Assert.Single(
            paired.OfType<EnsureModelManagedDataScaffoldingOperation>(),
            operation => operation.Table == "json_seed_rows");

        var intent = Assert.IsType<EnsureModelManagedDataIntent>(safeInsert.Intent);

        Assert.All(
            Enumerable.Range(1, insert.Values.GetLength(1) - 1),
            column => Assert.IsType<string>(insert.Values[0, column]));
        Assert.Equal("json", intent.ColumnTypes[1]);
        Assert.Equal(insert.Values.GetLength(1), intent.Values.ColumnCount);
    }

    private static DbContext CreateContext()
        => CreateContext(isMariaDb: false);

    private static DbContext CreateContext(
        bool isMariaDb
    )
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;"
            + "Allow User Variables=true;GuidFormat=Binary16",
            isMariaDb
                ? MySqlServerVersion.MariaDb(new Version(10, 11, 18))
                : MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        return new DbContext(options.Options);
    }

    private static JsonSeedContext CreateJsonSeedContext()
    {
        var options = new DbContextOptionsBuilder<JsonSeedContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        return new JsonSeedContext(options.Options);
    }

    private static IEnumerable<string> DecodeHexPayloads(
        string sql
    )
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";

        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var valueStart = offset + prefix.Length;
            var valueEnd = sql.IndexOf(suffix, valueStart, StringComparison.Ordinal);
            if (valueEnd < 0)
            {
                throw new InvalidOperationException("A generated hexadecimal SQL payload is unterminated.");
            }

            yield return Encoding.UTF8.GetString(Convert.FromHexString(sql[valueStart..valueEnd]));
            offset = valueEnd + suffix.Length;
        }
    }

    private enum LiteralState
    {
        Inactive,
        Active,
    }

    private sealed class UnsupportedLiteral;

    private sealed class JsonSeedContext(
        DbContextOptions<JsonSeedContext> options
    ) : DbContext(options)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<JsonSeedRow>(entity =>
            {
                entity.ToTable("json_seed_rows");
                entity.HasKey(row => row.Id);

                entity.Property(row => row.JsonText).HasColumnType("json");
                entity.Property(row => row.JsonElement).HasColumnType("json");
                entity.Property(row => row.JsonDocument).HasColumnType("json");
                entity.Property(row => row.JsonNode).HasColumnType("json");
                entity.Property(row => row.JsonObject).HasColumnType("json");
                entity.Property(row => row.JsonArray).HasColumnType("json");

                entity.HasData(new
                {
                    Id = 1,
                    JsonText = "{\"kind\":\"text\"}",
                    JsonElement = System.Text.Json.JsonElement.Parse("{\"kind\":\"element\"}"),
                    JsonDocument = System.Text.Json.JsonDocument.Parse("{\"kind\":\"document\"}"),
                    JsonNode = System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"node\"}"),
                    JsonObject = System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"object\"}")!.AsObject(),
                    JsonArray = System.Text.Json.Nodes.JsonNode.Parse("[\"array\",5,true]")!.AsArray(),
                });
            });
        }
    }

    private sealed class JsonSeedRow
    {
        public int Id { get; set; }

        public string JsonText { get; set; } = string.Empty;

        public JsonElement JsonElement { get; set; }

        public JsonDocument JsonDocument { get; set; } = null!;

        public JsonNode JsonNode { get; set; } = null!;

        public JsonObject JsonObject { get; set; } = null!;

        public JsonArray JsonArray { get; set; } = null!;
    }
}
