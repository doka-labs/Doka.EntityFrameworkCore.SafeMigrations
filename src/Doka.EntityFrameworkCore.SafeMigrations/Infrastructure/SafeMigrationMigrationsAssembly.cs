namespace Doka.EntityFrameworkCore.SafeMigrations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SafeMigrationMigrationsAssembly : IMigrationsAssembly
{
    private readonly TypeInfo[] _definedTypes;
    private readonly IMigrationsIdGenerator _idGenerator;

    private readonly Type _migrationContextType;

    // WHY: Keep the invocation-owned lazy cache visibly separate from its public non-null discovery result.
    // ReSharper disable once ReplaceWithFieldKeyword
    private IReadOnlyDictionary<string, TypeInfo>? _migrations;

    // WHY: Snapshot construction and the initialized-null sentinel are a paired discovery state machine.
    // ReSharper disable once ReplaceWithFieldKeyword
    private ModelSnapshot? _modelSnapshot;
    private bool _modelSnapshotInitialized;
    private List<(Assembly Assembly, TypeInfo[] Types)>? _additionalAssemblies;

    /// <summary>Initializes migration discovery for the configured canonical context boundary.</summary>
    /// <param name="currentContext">The active EF context.</param>
    /// <param name="options">The configured EF migration assembly options.</param>
    /// <param name="idGenerator">The provider's migration identifier contract.</param>
    /// <param name="canonicalContext">The explicitly configured migration ownership boundary.</param>
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

        var context = currentContext.Context;
        _idGenerator = idGenerator;

        var relationalOptions = RelationalOptionsExtension.Extract(options);
        Assembly = relationalOptions.MigrationsAssemblyObject
            ?? (relationalOptions.MigrationsAssembly is null
                ? context.GetType()
                    .Assembly
                : Assembly.Load(new AssemblyName(relationalOptions.MigrationsAssembly)));

        _definedTypes = GetDefinedTypes(Assembly);

        _migrationContextType = canonicalContext.ContextType ?? context.GetType();
        if (!_migrationContextType.IsInstanceOfType(context))
        {
            throw new InvalidOperationException(
                $"Canonical migration context '{DisplayType(_migrationContextType)}' is not assignable from runtime "
                + $"context '{DisplayType(context.GetType())}'. Configure a canonical base context compatible "
                + "with this runtime context, or use a separate migration lineage.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, TypeInfo> Migrations => _migrations ??=
        GetMigrationTypes()
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

    /// <inheritdoc />
    public ModelSnapshot? ModelSnapshot
    {
        get
        {
            if (_modelSnapshotInitialized)
            {
                return _modelSnapshot;
            }

            var snapshotTypes = _definedTypes;
            // WHY: EF11 selects the latest added assembly for the snapshot, not the
            // union of historical snapshots. Canonical ownership and ambiguity checks still apply.
            if (_additionalAssemblies is { Count: > 0 })
            {
                snapshotTypes = _additionalAssemblies[^1].Types;
            }

            var snapshots = snapshotTypes
                .Where(type => !type.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(type))
                .Where(type => type.GetCustomAttribute<DbContextAttribute>()
                        ?.ContextType
                    == _migrationContextType)

                // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based
                // materialization.
                // ReSharper disable once UseCollectionExpression
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

    /// <inheritdoc />
    public Assembly Assembly { get; }

    /// <inheritdoc />
    public void AddMigrations(
        Assembly additionalMigrationsAssembly
    )
    {
        ArgumentNullException.ThrowIfNull(additionalMigrationsAssembly);

        // WHY: Validate loading before replacing either cached view. Repeated assembly
        // registration keeps one migration inventory while selecting its snapshot last.
        var types = GetDefinedTypes(additionalMigrationsAssembly);
        _additionalAssemblies ??= [];
        for (var index = _additionalAssemblies.Count - 1; index >= 0; index--)
        {
            if (_additionalAssemblies[index].Assembly == additionalMigrationsAssembly)
            {
                _additionalAssemblies.RemoveAt(index);
            }
        }

        _additionalAssemblies.Add((additionalMigrationsAssembly, types));
        _migrations = null;
        _modelSnapshot = null;
        _modelSnapshotInitialized = false;
    }
    /// <summary>Enumerates owned migration types while retaining duplicate-ID rejection.</summary>
    private IEnumerable<TypeInfo> GetMigrationTypes()
    {
        if (_additionalAssemblies is { Count: > 0 })
        {
            // WHY: Adding the configured assembly must not duplicate its migration IDs.
            return _additionalAssemblies.Any(value => value.Assembly == Assembly)
                ? _additionalAssemblies.SelectMany(static value => value.Types)
                : _definedTypes.Concat(_additionalAssemblies.SelectMany(static value => value.Types));
        }

        return _definedTypes;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
            // ReSharper disable once UseCollectionExpression
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
