namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Locks the published Doka native UUID metadata and transport contracts.</summary>
public sealed class MySqlNativeUuidContractTests : IDisposable
{
    private static readonly Guid s_value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private readonly ServiceProvider _services;

    /// <summary>Owns one provider for this test case without retaining configuration variants globally.</summary>
    public MySqlNativeUuidContractTests()
    {
        // WHY: Engine and GUID-format variants are independent contracts, not
        // application service-provider configurations to retain in EF's cache.
        _services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();
    }

    /// <summary>Releases the test-owned provider after its locally owned contexts have been disposed.</summary>
    public void Dispose() => _services.Dispose();

    /// <summary>Native metadata accepts nullable GUIDs and normalized native store types.</summary>
    /// <param name="clrType">The model-side GUID type.</param>
    /// <param name="storeType">The explicit or inferred native store type.</param>
    [Theory]
    [InlineData(typeof(Guid), "uuid")]
    [InlineData(typeof(Guid?), "uuid")]
    [InlineData(typeof(Guid), " UUID ")]
    [InlineData(typeof(Guid), null)]
    [InlineData(typeof(Guid), " ")]
    public void TypedMetadataAcceptsNativeGuidShape(
        Type clrType,
        string? storeType
    )
    {
        var operation = CreateColumn(clrType, storeType, DokaMySqlGuidFormat.Uuid);

        var metadata = operation.GetMySqlMigrationMetadata();

        Assert.Equal(DokaMySqlGuidFormat.Uuid, metadata.GuidFormat);
    }

    /// <summary>Malformed native annotations cannot be mistaken for safe native storage.</summary>
    /// <param name="clrType">The declared column CLR type.</param>
    /// <param name="storeType">The declared column storage.</param>
    /// <param name="annotation">The untrusted annotation value.</param>
    [Theory]
    [InlineData(typeof(Guid), "uuid", (DokaMySqlGuidFormat)int.MaxValue)]
    [InlineData(typeof(Guid), "uuid", "Uuid")]
    [InlineData(typeof(Guid), "uuid", 2)]
    [InlineData(typeof(string), "uuid", DokaMySqlGuidFormat.Uuid)]
    [InlineData(typeof(byte[]), "uuid", DokaMySqlGuidFormat.Uuid)]
    [InlineData(typeof(Guid), "char(36)", DokaMySqlGuidFormat.Uuid)]
    [InlineData(typeof(Guid), "binary(16)", DokaMySqlGuidFormat.Uuid)]
    [InlineData(typeof(Guid), "uuid(16)", DokaMySqlGuidFormat.Uuid)]
    [InlineData(typeof(Guid), "uuid", DokaMySqlGuidFormat.Binary16)]
    [InlineData(typeof(Guid), "uuid", DokaMySqlGuidFormat.Char36)]
    public void TypedMetadataRejectsContradictoryNativeShape(
        Type clrType,
        string storeType,
        object annotation
    )
    {
        var operation = CreateColumn(clrType, storeType, annotation);
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        var exception = Record.Exception(() => operation.GetMySqlMigrationMetadata());
        var supported = MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _);

        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(supported);
    }

    /// <summary>SafeMigrations consumes the typed UUID contract without erasing provider metadata.</summary>
    /// <param name="nullable">Whether the native column permits nulls.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGuidDefinitionRetainsTypedMetadata(
        bool nullable
    )
    {
        var operation = CreateColumn(typeof(Guid), "uuid", DokaMySqlGuidFormat.Uuid);
        operation.IsNullable = nullable;
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        var supported = MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata);

        Assert.True(supported);
        Assert.Equal(DokaMySqlGuidFormat.Uuid, metadata.GuidFormat);
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
        Assert.True(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition));
    }

    /// <summary>An annotation alone retains the ordinary provider's default CLR-to-store inference.</summary>
    [Fact]
    public void NativeAnnotationWithoutStoreTypeMatchesOrdinaryProviderInference()
    {
        using var nativeContext = new PlainContext(CreateOptions<PlainContext>(registerSafeMigrations: false));
        using var safeContext = new PlainContext(CreateOptions<PlainContext>());
        var original = CreateColumn(typeof(Guid), storeType: null, DokaMySqlGuidFormat.Uuid);
        original.IsNullable = true;
        var builder = new MigrationBuilder(safeContext.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationFactory: operationBuilder => operationBuilder
            .AddColumn<Guid>("native_id", "native_uuid_contract", nullable: true)
            .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid));
        // WHY: Typed metadata permits an inferred store type, but the ordinary
        // Doka column renderer delegates that inference to its Binary16 default.
        // Real property opt-in supplies an explicit uuid type through the model.
        const string inferredColumn = "`native_id` binary(16)";

        var nativeSql = string.Concat(nativeContext.GetService<IMigrationsSqlGenerator>()
            .Generate([original], nativeContext.Model).Select(static command => command.CommandText));

        var safeSql = string.Concat(safeContext.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, safeContext.Model).Select(static command => command.CommandText));

        Assert.Equal(DokaMySqlGuidFormat.Uuid, original.GetMySqlMigrationMetadata().GuidFormat);
        Assert.Contains(inferredColumn, nativeSql, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(inferredColumn)), safeSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(Encoding.UTF8.GetBytes("`native_id` uuid")), safeSql, StringComparison.Ordinal);
    }

    /// <summary>The native mapping uses canonical text transport, not the connector's binary GUID reader.</summary>
    /// <param name="nullable">Whether to inspect the nullable GUID property.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGuidMappingUsesUnsizedTextTransport(
        bool nullable
    )
    {
        using var context = new NativeGuidContext(CreateOptions<NativeGuidContext>());
        var property = context.Model.FindEntityType(typeof(NativeGuidRecord))!
            .FindProperty(nullable ? nameof(NativeGuidRecord.OptionalId) : nameof(NativeGuidRecord.Id))!;

        using var command = new MySqlCommand();
        var mapping = property.GetRelationalTypeMapping();

        var parameter = mapping.CreateParameter(command, "id", s_value, nullable);
        var literal = mapping.GenerateSqlLiteral(s_value);

        Assert.Equal("uuid", mapping.StoreType);
        Assert.Null(property.GetMaxLength());
        Assert.Null(property.IsFixedLength());
        // WHY: GuidToStringConverter contributes a 36-character transport
        // hint; StoreTypePostfix.None keeps the database type unsized UUID.
        Assert.Equal(36, mapping.Size);
        Assert.Equal(typeof(string), mapping.Converter?.ProviderClrType);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(s_value.ToString("D"), parameter.Value);
        Assert.Equal("'00112233-4455-6677-8899-aabbccddeeff'", literal);
    }

    /// <summary>Property opt-in leaves implicit and explicit legacy GUID storage unchanged.</summary>
    [Fact]
    public void NativePropertyOptInPreservesBinaryDefaultAndExplicitChar36()
    {
        using var context = new NativeGuidContext(CreateOptions<NativeGuidContext>());

        var entity = context.Model.FindEntityType(typeof(NativeGuidRecord))!;
        var implicitProperty = entity.FindProperty(nameof(NativeGuidRecord.UnconfiguredId))!;
        var binaryProperty = entity.FindProperty(nameof(NativeGuidRecord.BinaryId))!;
        var characterProperty = entity.FindProperty(nameof(NativeGuidRecord.CharacterId))!;

        Assert.Equal("binary(16)", implicitProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Binary16, implicitProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
        Assert.Equal("binary(16)", binaryProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Binary16, binaryProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
        Assert.Equal("char(36)", characterProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Char36, characterProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
    }

    /// <summary>A global native default does not rewrite explicitly configured legacy GUID properties.</summary>
    [Fact]
    public void NativeDefaultPreservesExplicitBinary16AndChar36()
    {
        using var context = new NativeGuidContext(CreateOptions<NativeGuidContext>(DokaMySqlGuidFormat.Uuid));

        var entity = context.Model.FindEntityType(typeof(NativeGuidRecord))!;
        var implicitProperty = entity.FindProperty(nameof(NativeGuidRecord.UnconfiguredId))!;
        var binaryProperty = entity.FindProperty(nameof(NativeGuidRecord.BinaryId))!;
        var characterProperty = entity.FindProperty(nameof(NativeGuidRecord.CharacterId))!;

        Assert.Equal("uuid", implicitProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Uuid, implicitProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
        Assert.Equal("binary(16)", binaryProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Binary16, binaryProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
        Assert.Equal("char(36)", characterProperty.GetColumnType());
        Assert.Equal(DokaMySqlGuidFormat.Char36, characterProperty.FindAnnotation("Doka:MySql:GuidFormat")?.Value);
    }

    /// <summary>Application-owned 16-byte converters remain byte values under every GUID default.</summary>
    /// <param name="defaultFormat">The independent provider-owned GUID default.</param>
    [Theory]
    [InlineData(DokaMySqlGuidFormat.Binary16)]
    [InlineData(DokaMySqlGuidFormat.Char36)]
    [InlineData(DokaMySqlGuidFormat.Uuid)]
    public void ApplicationByteConverterSurfacesNewImplicitWidth(
        DokaMySqlGuidFormat defaultFormat
    )
    {
        using var context = new ConvertedGuidContext(CreateOptions<ConvertedGuidContext>(defaultFormat));

        var property = context.Model.FindEntityType(typeof(ConvertedGuidRecord))!
            .FindProperty(nameof(ConvertedGuidRecord.ConvertedId))!;

        Assert.Equal(typeof(byte[]), property.GetProviderClrType());
        Assert.Equal("varbinary(17)", property.GetColumnType());
        Assert.Null(property.FindAnnotation("Doka:MySql:GuidFormat"));
    }

    /// <summary>Explicit native store types fail before SQL on engines without the UUID type.</summary>
    /// <param name="useMariaDb">Whether the unsupported profile is MariaDB 10.6 instead of MySQL 8.4.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedProfileRejectsNativeTypeMapping(
        bool useMariaDb
    )
    {
        using var context = new PlainContext(
            CreateOptions<PlainContext>(serverVersion: UnsupportedVersion(useMariaDb)));

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();

        var exception = Record.Exception(() => mappingSource.FindMapping(typeof(Guid), "uuid"));

        Assert.Contains(
            "MariaDB 10.7",
            Assert.IsType<NotSupportedException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>SafeMigrations retains an owned unsupported guard instead of rendering native UUID DDL.</summary>
    /// <param name="useMariaDb">Whether the unsupported engine is MariaDB 10.6 instead of MySQL 8.4.</param>
    /// <param name="annotateFormat">Whether the author also supplies the native UUID annotation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsupportedProfileGeneratesOwnedUnsupportedGuardForNativeColumn(
        bool useMariaDb,
        bool annotateFormat
    )
    {
        using var context = new PlainContext(
            CreateOptions<PlainContext>(serverVersion: UnsupportedVersion(useMariaDb)));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationFactory: operationBuilder =>
        {
            var column = operationBuilder.AddColumn<Guid>(
                "native_id",
                "native_uuid_contract",
                type: "uuid",
                nullable: true);

            if (annotateFormat)
            {
                column.Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid);
            }

        });

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Concat(commands.Select(static command => command.CommandText));

        Assert.NotEmpty(commands);
        Assert.Contains("doka_sm_unsupported", sql, StringComparison.Ordinal);
        Assert.Contains("'unsupported'", sql, StringComparison.Ordinal);
        Assert.Contains("'DO 0'", sql, StringComparison.Ordinal);
    }

    /// <summary>Neither model-level nor property-level native opt-in bypasses engine capability validation.</summary>
    /// <param name="useMariaDb">Whether to select the older MariaDB compatibility profile.</param>
    /// <param name="globalDefault">Whether to configure the native format as the context default.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsupportedProfileRejectsNativeModelBeforeDdl(
        bool useMariaDb,
        bool globalDefault
    )
    {
        var version = UnsupportedVersion(useMariaDb);

        var exception = Record.Exception(() =>
        {
            using var context = globalDefault
                ? (DbContext)new DefaultGuidContext(
                    CreateOptions<DefaultGuidContext>(DokaMySqlGuidFormat.Uuid, version))
                : new NativeGuidContext(CreateOptions<NativeGuidContext>(serverVersion: version));

            _ = context.Model;
        });

        Assert.Contains(
            "MariaDB 10.7",
            Assert.IsType<NotSupportedException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Builds one provider-annotated column without consulting provider internals.</summary>
    private static AddColumnOperation CreateColumn(
        Type clrType,
        string? storeType,
        object annotation
    )
    {
        var operation = new AddColumnOperation
        {
            Name = "native_id",
            Table = "native_uuid_contract",
            ClrType = clrType,
            ColumnType = storeType,
            IsNullable = Nullable.GetUnderlyingType(clrType) is not null,
        };

        operation["Doka:MySql:GuidFormat"] = annotation;

        return operation;
    }

    /// <summary>Creates offline options using the published provider and SafeMigrations registration.</summary>
    private DbContextOptions<TContext> CreateOptions<TContext>(
        DokaMySqlGuidFormat? defaultFormat = null,
        MySqlServerVersion? serverVersion = null,
        bool registerSafeMigrations = true
    ) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseInternalServiceProvider(_services);
        builder.UseMySql(
            "Server=localhost;Database=native_uuid_contract;User ID=test;",
            serverVersion ?? MySqlServerVersion.MariaDb(new Version(11, 8, 0)),
            provider =>
            {
                if (defaultFormat is { } format)
                {
                    provider.DefaultGuidFormat(format);
                }
            });

        if (registerSafeMigrations)
        {
            builder.UseMySqlSafeMigrations();
        }

        return builder.Options;
    }

    /// <summary>The compatibility escape hatch must not invent the pre-10.7 UUID capability.</summary>
    private static MySqlServerVersion UnsupportedVersion(
        bool useMariaDb
    ) => useMariaDb
        ? MySqlServerVersion.MariaDb(new Version(10, 6, 20), MySqlServerVersionCompatibilityMode.AllowUnsupported)
        : MySqlServerVersion.MySql(new Version(8, 4, 0));

    /// <summary>Exposes native, legacy, and implicit mappings in the same finalized model.</summary>
    private sealed class NativeGuidContext(DbContextOptions<NativeGuidContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            var entity = modelBuilder.Entity<NativeGuidRecord>();
            entity.Property(value => value.Id).HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid);
            entity.Property(value => value.OptionalId).HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid);
            entity.Property(value => value.BinaryId).HasMySqlGuidFormat(DokaMySqlGuidFormat.Binary16);
            entity.Property(value => value.CharacterId).HasMySqlGuidFormat(DokaMySqlGuidFormat.Char36);
        }
    }

    /// <summary>Exercises only the context-wide GUID default.</summary>
    private sealed class DefaultGuidContext(DbContextOptions<DefaultGuidContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<NativeGuidRecord>();
    }

    /// <summary>Separates an application converter from provider-owned GUID mappings.</summary>
    private sealed class ConvertedGuidContext(DbContextOptions<ConvertedGuidContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder.Entity<ConvertedGuidRecord>().Property(value => value.ConvertedId).HasConversion<byte[]>();
    }

    /// <summary>Supplies a valid model for direct public type-mapping capability probes.</summary>
    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<PlainRecord>();
    }

    /// <summary>Stores the independently configured GUID properties.</summary>
    private sealed class NativeGuidRecord
    {
        /// <summary>Gets or sets the primary native UUID.</summary>
        public Guid Id { get; set; }

        /// <summary>Gets or sets a nullable native UUID.</summary>
        public Guid? OptionalId { get; set; }

        /// <summary>Gets or sets a GUID using the context default.</summary>
        public Guid UnconfiguredId { get; set; }

        /// <summary>Gets or sets an explicitly binary GUID.</summary>
        public Guid BinaryId { get; set; }

        /// <summary>Gets or sets an explicitly textual GUID.</summary>
        public Guid CharacterId { get; set; }
    }

    /// <summary>Stores an application-owned GUID-to-bytes conversion.</summary>
    private sealed class ConvertedGuidRecord
    {
        /// <summary>Gets or sets the independent entity key.</summary>
        public int Id { get; set; }

        /// <summary>Gets or sets the application-converted value.</summary>
        public Guid ConvertedId { get; set; }
    }

    /// <summary>A capability probe must not require a native UUID model to initialize.</summary>
    private sealed class PlainRecord
    {
        /// <summary>Gets or sets the ordinary integer key.</summary>
        public int Id { get; set; }
    }
}
