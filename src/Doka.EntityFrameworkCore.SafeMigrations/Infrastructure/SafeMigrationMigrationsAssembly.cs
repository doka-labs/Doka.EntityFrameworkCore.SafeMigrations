namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed class SafeMigrationMigrationsAssembly : IMigrationsAssembly
{
    private readonly DbContext _context;
    private readonly TypeInfo[] _definedTypes;
    private readonly IMigrationsIdGenerator _idGenerator;
    private readonly Type _migrationContextType;
    private IReadOnlyDictionary<string, TypeInfo>? _migrations;
    private ModelSnapshot? _modelSnapshot;
    private bool _modelSnapshotInitialized;

    public SafeMigrationMigrationsAssembly(
        ICurrentDbContext currentContext,
        IDbContextOptions options,
        IMigrationsIdGenerator idGenerator,
        SafeMigrationCanonicalContextConfiguration canonicalContext
    )
    {
        ArgumentNullException.ThrowIfNull(currentContext);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(canonicalContext);

        _context = currentContext.Context;
        _idGenerator = idGenerator;

        var relationalOptions = RelationalOptionsExtension.Extract(options);
        Assembly = relationalOptions.MigrationsAssemblyObject
            ?? (relationalOptions.MigrationsAssembly is null
                ? _context.GetType()
                    .Assembly
                : Assembly.Load(new AssemblyName(relationalOptions.MigrationsAssembly)));

        _definedTypes = GetDefinedTypes(Assembly);

        _migrationContextType = canonicalContext.ContextType ?? _context.GetType();
        if (!_migrationContextType.IsInstanceOfType(_context))
        {
            throw new InvalidOperationException(
                $"Canonical migration context '{DisplayType(_migrationContextType)}' is not assignable from runtime "
                + $"context '{DisplayType(_context.GetType())}'. Configure a canonical base context compatible "
                + "with this runtime context, or use a separate migration lineage.");
        }
    }

    public IReadOnlyDictionary<string, TypeInfo> Migrations =>
        _migrations ??= _definedTypes
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .Select(type => new
            {
                Type = type,
                Context = type.GetCustomAttribute<DbContextAttribute>()
                    ?.ContextType,
                Migration = type.GetCustomAttribute<MigrationAttribute>(),
            })
            .Where(candidate => candidate.Context == _migrationContextType && candidate.Migration is not null)
            .OrderBy(candidate => candidate.Migration!.Id, StringComparer.Ordinal)
            .ToDictionary(candidate => candidate.Migration!.Id, candidate => candidate.Type, StringComparer.Ordinal);

    public ModelSnapshot? ModelSnapshot
    {
        get
        {
            if (_modelSnapshotInitialized)
            {
                return _modelSnapshot;
            }

            var snapshots = _definedTypes
                .Where(type => !type.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(type))
                .Where(type => type.GetCustomAttribute<DbContextAttribute>()
                        ?.ContextType
                    == _migrationContextType)
                .ToArray();

            if (snapshots.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple migration model snapshots are registered for '{DisplayType(_migrationContextType)}'. "
                    + "Keep exactly one snapshot for this canonical context in the configured migrations assembly.");
            }

            _modelSnapshot = snapshots.Length == 0
                ? null
                : Activator.CreateInstance(
                    snapshots[0]
                        .AsType(),
                    nonPublic: true) as ModelSnapshot
                ?? throw new InvalidOperationException(
                    $"The migration model snapshot for '{DisplayType(_migrationContextType)}' could not be "
                    + "constructed. Check that the snapshot derives from ModelSnapshot and has a usable constructor.");

            _modelSnapshotInitialized = true;

            return _modelSnapshot;
        }
    }

    public Assembly Assembly { get; }

    public string? FindMigrationId(
        string nameOrId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        return Migrations.Keys.FirstOrDefault(id =>
            _idGenerator.IsValidId(nameOrId)
                ? StringComparer.OrdinalIgnoreCase.Equals(id, nameOrId)
                : StringComparer.OrdinalIgnoreCase.Equals(_idGenerator.GetName(id), nameOrId));
    }

    public Migration CreateMigration(
        TypeInfo migrationClass,
        string activeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(migrationClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProvider);

        var migration = Activator.CreateInstance(migrationClass.AsType(), nonPublic: true) as Migration
            ?? throw new InvalidOperationException(
                $"Migration '{DisplayType(migrationClass.AsType())}' could not be constructed. Check that the "
                + "selected type derives from Migration and has a usable constructor.");

        migration.ActiveProvider = activeProvider;

        return migration;
    }

    /// <summary>Formats only bounded structural type metadata, never reflection loader payloads.</summary>
    private static string DisplayType(
        Type type
    ) => SafeMigrationDiagnosticMessages.BoundForDisplay(type.FullName ?? type.Name);

    private static TypeInfo[] GetDefinedTypes(
        Assembly assembly
    )
    {
        try
        {
            return assembly.DefinedTypes.ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw new InvalidOperationException(
                "Migration types could not be loaded from assembly "
                + $"'{SafeMigrationDiagnosticMessages.BoundForDisplay(assembly.GetName().Name ?? "<unnamed>")}'. "
                + "Check the deployed dependency versions and migrations assembly. The original loader exception "
                + "is preserved as InnerException for diagnosis.",
                exception);
        }
    }
}
