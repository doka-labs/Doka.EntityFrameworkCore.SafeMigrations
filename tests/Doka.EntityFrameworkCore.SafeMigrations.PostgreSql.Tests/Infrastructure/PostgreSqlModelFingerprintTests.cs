namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlModelFingerprintTests
{
    private const string ProviderContract = "Npgsql.EntityFrameworkCore.PostgreSQL";

    [Fact]
    public void Create_IsStableAcrossRelationalDeclarationOrder()
    {
        using var first = new FirstOrderContext();
        using var second = new SecondOrderContext();

        var firstFingerprint = Fingerprint(first);
        var secondFingerprint = Fingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.StartsWith(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:",
            firstFingerprint,
            StringComparison.Ordinal);
        Assert.Equal(64, firstFingerprint[(firstFingerprint.LastIndexOf(':') + 1)..].Length);
        Assert.Equal(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:"
            + "2654dc3b5f9db76cd83374dc360b4d912c6bda461653206bab4c2bb2820f3e26",
            firstFingerprint);
    }

    [Fact]
    public void Create_IsCultureInvariant()
    {
        using var context = new FirstOrderContext();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = Fingerprint(context);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var german = Fingerprint(context);

            Assert.Equal(turkish, german);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Create_ChangesWhenRelationalFacetChanges()
    {
        using var baseline = new FirstOrderContext();
        using var changed = new ChangedFacetContext();

        Assert.NotEqual(Fingerprint(baseline), Fingerprint(changed));
    }

    [Fact]
    public void Create_ChangesWhenProviderContractChanges()
    {
        using var context = new FirstOrderContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var baseline = SafeMigrationModelFingerprint.Create(model, ProviderContract);
        var changed = SafeMigrationModelFingerprint.Create(model, "example.provider");

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Create_HandlesNestedJsonContainerWithoutScalarPropertyMappings()
    {
        using var context = new JsonArtifactContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;
        var jsonColumn = model
            .GetRelationalModel()
            .Tables
            .Single(static table => table.Name == "json_artifacts")
            .Columns
            .Single(static column => column.Name == "payload");

        var first = SafeMigrationModelFingerprint.Create(model, ProviderContract);
        var second = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        Assert.Empty(jsonColumn.PropertyMappings);
        Assert.Equal(first, second);
        Assert.StartsWith(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:",
            first,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateExpected_RejectsLegacyBareHash()
    {
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);

        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationModelFingerprint.ValidateExpected(actual, new string('a', 64)));

        Assert.Equal("expectedFingerprint", exception.ParamName);
    }

    [Fact]
    public void ValidateExpected_UsesOrdinalWireEquality()
    {
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);
        var upperHex = actual[..(actual.LastIndexOf(':') + 1)]
            + actual[(actual.LastIndexOf(':') + 1)..]
                .ToUpperInvariant();

        Assert.Throws<ArgumentException>(() => SafeMigrationModelFingerprint.ValidateExpected(actual, upperHex));
    }

    [Fact]
    public void Create_IsStableAcrossRepeatedAndParallelCalls()
    {
        using var context = new FirstOrderContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;
        var expected = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        var actual = Enumerable
            .Range(0, 32)
            .AsParallel()
            .Select(_ => SafeMigrationModelFingerprint.Create(model, ProviderContract))
            .ToArray();

        Assert.All(actual, value => Assert.Equal(expected, value));
    }

    [Fact]
    public void ValidateExpected_AcceptsNullAndAnExactFingerprint()
    {
        using var context = new FirstOrderContext();
        var actual = Fingerprint(context);

        SafeMigrationModelFingerprint.ValidateExpected(actual, expectedFingerprint: null);
        SafeMigrationModelFingerprint.ValidateExpected(actual, actual);
    }

    [Fact]
    public void Create_RejectsUnknownMigrationAnnotationValueTypes()
    {
        using var context = new UnsupportedAnnotationContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var exception =
            Assert.Throws<NotSupportedException>(() => SafeMigrationModelFingerprint.Create(model, ProviderContract));

        Assert.Contains("Doka:UnsupportedFingerprintValue", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_CanonicalizesEverySupportedAnnotationValueKind()
    {
        using var context = new SupportedAnnotationContext();

        var first = Fingerprint(context);
        var second = Fingerprint(context);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_RejectsUnknownDictionaryKeyTypes()
    {
        using var context = new UnsupportedDictionaryKeyContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var exception =
            Assert.Throws<NotSupportedException>(() => SafeMigrationModelFingerprint.Create(model, ProviderContract));

        Assert.Contains("dictionary key type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_DisposesDictionaryAnnotationEnumerator()
    {
        var dictionary = new DisposableEnumeratorDictionary
        {
            ["alpha"] = 1,
        };
        using var context = new DisposableDictionaryAnnotationContext(dictionary);
        var model = context.GetService<IDesignTimeModel>()
            .Model;
        dictionary.ResetDisposalState();

        _ = SafeMigrationModelFingerprint.Create(model, ProviderContract);

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
                    DBNull.Value, "text", 'x', true,
                    (byte)1, (sbyte)-1, (short)-2, (ushort)2,
                    -3, (uint)3, -4L, 4UL,
                    5.25M, 6.5F, 7.75D, new DateOnly(2026, 8, 20),
                    new TimeOnly(12, 34, 56), new DateTime(
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
                    TimeSpan.FromMinutes(5), Guid.Parse("12345678-1234-1234-1234-123456789abc"), new byte[] { 1, 2, 3 },
                    typeof(string), DayOfWeek.Friday, dictionary, new Dictionary<string, object?>
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

    private sealed class DisposableDictionaryAnnotationContext(DisposableEnumeratorDictionary dictionary)
        : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            modelBuilder.Model.SetAnnotation("Doka:DisposableDictionary", dictionary);
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

    private sealed class DisposableDictionaryEnumerator(
        IReadOnlyList<DictionaryEntry> entries,
        Action onDispose
    ) : IDictionaryEnumerator, IDisposable
    {
        private bool _disposed;
        private int _index = -1;

        public DictionaryEntry Entry => entries[_index];

        public object Key => Entry.Key;

        public object? Value => Entry.Value;

        public object Current => Entry;

        public bool MoveNext()
        {
            if (_index < entries.Count)
            {
                _index++;
            }

            return _index < entries.Count;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            onDispose();
        }
    }

    private sealed class Alpha
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Beta
    {
        public int Id { get; set; }

        public bool Enabled { get; set; }
    }

    private sealed class JsonArtifact
    {
        public int Id { get; set; }

        public JsonArtifactPayload Payload { get; set; } = new();
    }

    private sealed class JsonArtifactPayload
    {
        public string Name { get; set; } = string.Empty;

        public JsonArtifactDetails Details { get; set; } = new();
    }

    private sealed class JsonArtifactDetails
    {
        public int Revision { get; set; }
    }
}
