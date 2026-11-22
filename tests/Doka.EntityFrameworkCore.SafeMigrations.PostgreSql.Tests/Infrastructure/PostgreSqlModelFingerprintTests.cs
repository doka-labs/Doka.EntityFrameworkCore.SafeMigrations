namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies PostgreSQL Model Fingerprint contracts in independently owned test state.</summary>
public sealed class PostgreSqlModelFingerprintTests
{
    private const string ProviderContract = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Create Is Stable Across Relational Declaration Order.</summary>
    [Fact]
    public void Create_IsStableAcrossRelationalDeclarationOrder()
    {
        // Arrange
        using var first = new FirstOrderContext();
        using var second = new SecondOrderContext();

        // Act
        var firstFingerprint = Fingerprint(first);
        var secondFingerprint = Fingerprint(second);

        // Assert
        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.StartsWith(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:",
            firstFingerprint,
            StringComparison.Ordinal);

        Assert.Equal(64, firstFingerprint[(firstFingerprint.LastIndexOf(':') + 1)..].Length);

        // WHY: The unchanged v1 encoding includes the inverse-navigation convention dictionary's
        // generic type identity. Its framework assembly version advances from 10 to 11, not its schema facets.
        Assert.Equal(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:"
            + "75910589c5d53a83f1712f806cee925164bb7e7bffb817e62cda88d1b1830f20",
            firstFingerprint);
    }

    /// <summary>Create Is Culture Invariant.</summary>
    [Fact]
    public void Create_IsCultureInvariant()
    {
        // Arrange
        using var context = new FirstOrderContext();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        string turkish;
        string german;

        // Act
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            turkish = Fingerprint(context);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            german = Fingerprint(context);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        // Assert
        Assert.Equal(turkish, german);
    }

    /// <summary>Create Changes When Relational Facet Changes.</summary>
    [Fact]
    public void Create_ChangesWhenRelationalFacetChanges()
    {
        // Arrange
        using var baseline = new FirstOrderContext();
        using var changed = new ChangedFacetContext();

        // Act
        var baselineFingerprint = Fingerprint(baseline);
        var changedFingerprint = Fingerprint(changed);

        // Assert
        Assert.NotEqual(baselineFingerprint, changedFingerprint);
    }

    /// <summary>Create Changes When Provider Contract Changes.</summary>
    [Fact]
    public void Create_ChangesWhenProviderContractChanges()
    {
        // Arrange
        using var context = new FirstOrderContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        // Act
        var baseline = SafeMigrationModelFingerprint.Create(model, ProviderContract);
        var changed = SafeMigrationModelFingerprint.Create(model, "example.provider");

        // Assert
        Assert.NotEqual(baseline, changed);
    }

    /// <summary>Create Handles Nested Json Container Without Scalar Property Mappings.</summary>
    [Fact]
    public void Create_HandlesNestedJsonContainerWithoutScalarPropertyMappings()
    {
        // Arrange
        using var context = new JsonArtifactContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var jsonColumn = model
            .GetRelationalModel()
            .Tables
            .Single(static table => table.Name == "json_artifacts")
            .Columns
            .Single(static column => column.Name == "payload");

        // Act
        var first = SafeMigrationModelFingerprint.Create(model, ProviderContract);
        var second = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        // Assert
        Assert.Empty(jsonColumn.PropertyMappings);
        Assert.Equal(first, second);
        Assert.StartsWith(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:",
            first,
            StringComparison.Ordinal);
    }

    /// <summary>Validate Expected Rejects Legacy Bare Hash.</summary>
    [Fact]
    public void ValidateExpected_RejectsLegacyBareHash()
    {
        // Arrange
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);

        // Act
        var failure = Record.Exception(() =>
            SafeMigrationModelFingerprint.ValidateExpected(actual, new string('a', 64)));

        // Assert
        var exception = Assert.IsType<ArgumentException>(failure);
        Assert.Equal("expectedFingerprint", exception.ParamName);
    }

    /// <summary>Validate Expected Uses Ordinal Wire Equality.</summary>
    [Fact]
    public void ValidateExpected_UsesOrdinalWireEquality()
    {
        // Arrange
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);
        var upperHex = actual[..(actual.LastIndexOf(':') + 1)]
            + actual[(actual.LastIndexOf(':') + 1)..]
                .ToUpperInvariant();

        // Act
        var failure = Record.Exception(() => SafeMigrationModelFingerprint.ValidateExpected(actual, upperHex));

        // Assert
        Assert.IsType<ArgumentException>(failure);
    }

    /// <summary>Create Is Stable Across Repeated And Parallel Calls.</summary>
    [Fact]
    public void Create_IsStableAcrossRepeatedAndParallelCalls()
    {
        // Arrange
        using var context = new FirstOrderContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var expected = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        // Act
        var actual = Enumerable
            .Range(0, 32)
            .AsParallel()
            .Select(_ => SafeMigrationModelFingerprint.Create(model, ProviderContract))
            .ToArray();

        // Assert
        Assert.All(actual, value => Assert.Equal(expected, value));
    }

    /// <summary>Validate Expected Accepts Null And An Exact Fingerprint.</summary>
    /// <param name="useExactFingerprint">Whether the optional contract is present and exactly equal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateExpected_AcceptsNullAndAnExactFingerprint(
        bool useExactFingerprint
    )
    {
        // Arrange
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);
        var expected = useExactFingerprint
            ? actual
            : null;

        // Act
        var failure = Record.Exception(() => SafeMigrationModelFingerprint.ValidateExpected(actual, expected));

        // Assert
        Assert.Null(failure);
    }

    /// <summary>Create Rejects Unknown Migration Annotation Value Types.</summary>
    [Fact]
    public void Create_RejectsUnknownMigrationAnnotationValueTypes()
    {
        // Arrange
        using var context = new UnsupportedAnnotationContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        // Act
        var failure = Record.Exception(() => SafeMigrationModelFingerprint.Create(model, ProviderContract));

        // Assert
        var exception = Assert.IsType<NotSupportedException>(failure);
        Assert.Contains("Doka:UnsupportedFingerprintValue", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Create Canonicalizes Every Supported Annotation Value Kind.</summary>
    [Fact]
    public void Create_CanonicalizesEverySupportedAnnotationValueKind()
    {
        // Arrange
        using var context = new SupportedAnnotationContext();

        // Act
        var first = Fingerprint(context);
        var second = Fingerprint(context);

        // Assert
        Assert.Equal(first, second);
    }

    /// <summary>Create Rejects Unknown Dictionary Key Types.</summary>
    [Fact]
    public void Create_RejectsUnknownDictionaryKeyTypes()
    {
        // Arrange
        using var context = new UnsupportedDictionaryKeyContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        // Act
        var failure = Record.Exception(() => SafeMigrationModelFingerprint.Create(model, ProviderContract));

        // Assert
        var exception = Assert.IsType<NotSupportedException>(failure);
        Assert.Contains("dictionary key type", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Create Disposes Dictionary Annotation Enumerator.</summary>
    [Fact]
    public void Create_DisposesDictionaryAnnotationEnumerator()
    {
        // Arrange
        var dictionary = new DisposableEnumeratorDictionary
        {
            ["alpha"] = 1,
        };

        using var context = new DisposableDictionaryAnnotationContext(dictionary);
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        dictionary.ResetDisposalState();

        // Act
        _ = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        // Assert
        Assert.True(dictionary.EnumeratorDisposed);
    }

    private static string Fingerprint(
        DbContext context
    ) => SafeMigrationModelFingerprint.Create(
        context.GetService<IDesignTimeModel>()
            .Model,
        ProviderContract);

    private abstract class FingerprintContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseNpgsql("Host=localhost;Database=fingerprint;Username=test;Password=test");

        protected static void ConfigureAlpha(
            ModelBuilder modelBuilder,
            int maxLength
        )
        {
            modelBuilder.Entity<Alpha>(entity =>
            {
                entity.ToTable("alpha", "review");
                entity.HasKey(static value => value.Id);
                entity
                    .Property(static value => value.Name)
                    .HasMaxLength(maxLength)
                    .HasDefaultValue("default");

                entity
                    .HasIndex(static value => value.Name)
                    .HasDatabaseName("ix_alpha_name");
            });
        }

        protected static void ConfigureBeta(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<Beta>(entity =>
            {
                entity.ToTable("beta", "review");
                entity.HasKey(static value => value.Id);
                entity
                    .Property(static value => value.Enabled)
                    .HasDefaultValue(true);
            });
        }
    }

    private sealed class FirstOrderContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            ConfigureBeta(modelBuilder);
        }
    }

    private sealed class SecondOrderContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureBeta(modelBuilder);
            ConfigureAlpha(modelBuilder, 100);
        }
    }

    private sealed class ChangedFacetContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 101);
            ConfigureBeta(modelBuilder);
        }
    }

    private sealed class JsonArtifactContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseNpgsql(
            "Host=localhost;Database=fingerprint;Username=test;Password=test");

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<JsonArtifact>(entity =>
            {
                entity.ToTable("json_artifacts", "review");
                entity.HasKey(static artifact => artifact.Id);
                entity.OwnsOne(
                    static artifact => artifact.Payload,
                    owned =>
                    {
                        owned.ToJson("payload");
                        owned.OwnsOne(static payload => payload.Details);
                    });
            });
        }
    }

    private sealed class UnsupportedAnnotationContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            modelBuilder.Model.SetAnnotation("Doka:UnsupportedFingerprintValue", new object());
        }
    }

    private sealed class SupportedAnnotationContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            var dictionary = new Hashtable
            {
                ["string"] = 1,
                [typeof(string)] = 2,
                [DayOfWeek.Monday] = 3,
                [42] = 4,
            };

            modelBuilder.Model.SetAnnotation(
                "Doka:FingerprintValueMatrix",
                new object?[]
                {
                    DBNull.Value,
                    "text",
                    'x',
                    true,
                    (byte)1,
                    (sbyte)-1,
                    (short)-2,
                    (ushort)2,
                    -3,
                    (uint)3,
                    -4L,
                    4UL,
                    5.25M,
                    6.5F,
                    7.75D,
                    new DateOnly(2026, 8, 20),
                    new TimeOnly(12, 34, 56),
                    new DateTime(
                        2026,
                        8,
                        20,
                        12,
                        34,
                        56,
                        DateTimeKind.Utc),
                    new DateTimeOffset(
                        2026,
                        8,
                        20,
                        12,
                        34,
                        56,
                        TimeSpan.FromHours(2)),
                    TimeSpan.FromMinutes(5),
                    Guid.Parse("12345678-1234-1234-1234-123456789abc"),
                    new byte[] { 1, 2, 3 },
                    typeof(string),
                    DayOfWeek.Friday,
                    dictionary,
                    new Dictionary<string, object?>
                    {
                        ["alpha"] = 1,
                        ["beta"] = new object?[] { true, null },
                    },
                    new object?[] { null, "nested" },
                });

            modelBuilder.Model.SetAnnotation("DebugOnly", new object());
        }
    }

    private sealed class UnsupportedDictionaryKeyContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            modelBuilder.Model.SetAnnotation(
                "Doka:UnsupportedDictionaryKey",
                new Hashtable { [new object()] = "value" });
        }
    }

    private sealed class DisposableDictionaryAnnotationContext
        : FingerprintContext
    {
        private readonly DisposableEnumeratorDictionary _dictionary;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public DisposableDictionaryAnnotationContext(
            DisposableEnumeratorDictionary dictionary
        )
        {
            _dictionary = dictionary;
        }

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            modelBuilder.Model.SetAnnotation("Doka:DisposableDictionary", _dictionary);
        }
    }

    private sealed class DisposableEnumeratorDictionary : Hashtable
    {
        public bool EnumeratorDisposed { get; private set; }

        public override IDictionaryEnumerator GetEnumerator()
        {
            var entries = new DictionaryEntry[Count];
            var entryIndex = 0;

            // Close the base enumerator before the disposable test enumerator escapes.
            var enumerator = base.GetEnumerator();

            try
            {
                while (enumerator.MoveNext())
                {
                    entries[entryIndex++] = enumerator.Entry;
                }
            }
            finally
            {
                if (enumerator is IDisposable disposableEnumerator)
                {
                    disposableEnumerator.Dispose();
                }
            }

            return new DisposableDictionaryEnumerator(entries, () => EnumeratorDisposed = true);
        }

        public void ResetDisposalState() => EnumeratorDisposed = false;
    }

    private sealed class DisposableDictionaryEnumerator : IDictionaryEnumerator, IDisposable
    {
        private readonly IReadOnlyList<DictionaryEntry> _entries;
        private readonly Action _onDispose;

        private bool _disposed;
        private int _index = -1;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public DisposableDictionaryEnumerator(
            IReadOnlyList<DictionaryEntry> entries,
            Action onDispose
        )
        {
            _entries = entries;
            _onDispose = onDispose;
        }

        public DictionaryEntry Entry => _entries[_index];

        public object Key => Entry.Key;

        public object? Value => Entry.Value;

        public object Current => Entry;

        public bool MoveNext()
        {
            if (_index < _entries.Count)
            {
                _index++;
            }

            return _index < _entries.Count;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _onDispose();
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class Alpha
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class Beta
    {
        public int Id { get; set; }

        public bool Enabled { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifact
    {
        public int Id { get; set; }

        public JsonArtifactPayload Payload { get; set; } = new();
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifactPayload
    {
        public string Name { get; set; } = string.Empty;

        public JsonArtifactDetails Details { get; } = new();
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifactDetails
    {
        public int Revision { get; set; }
    }
}
