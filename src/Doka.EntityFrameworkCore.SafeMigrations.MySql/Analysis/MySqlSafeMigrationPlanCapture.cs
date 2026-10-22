namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Captures the runtime catalog plans emitted for one ordered analysis batch.
/// </summary>
/// <remarks>
/// Analysis activates the capture before invoking Doka's operation handlers.
/// Runtime generation uses the same scoped service without an active capture.
/// </remarks>
internal sealed class MySqlSafeMigrationPlanCapture
{
    private static readonly IReadOnlyList<ExpectedIndexDefinition> s_emptyUniqueIndexes = [];

    private static readonly IReadOnlySet<SafeMigrationOperation> s_emptyPrimaryKeyReplacements =
        new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance);

    private static readonly IReadOnlyDictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>
        s_emptyUniqueIndexCatalog = new Dictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>();

    private SafeMigrationOperation[]? _expected;
    private IReadOnlySet<SafeMigrationOperation>? _expectedPrimaryKeyReplacements;

    private IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>?
        _expectedTableConstraints;

    private IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>?
        _generationTableConstraints;

    private IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>?
        _generationFoldedTableConstraints;

    private IReadOnlyList<string>? _generationDatabaseQualifiers;
    private MySqlExpectedUniqueIndexCatalog? _generationUniqueIndexes;
    private MySqlExpectedUniqueIndexCatalog? _generationFoldedUniqueIndexes;
    private MySqlExpectedUniqueIndexCatalog? _expectedUniqueIndexes;
    private MySqlBackfillConstraintCatalog? _expectedBackfillConstraints;
    private MySqlBackfillConstraintCatalog? _generationBackfillConstraints;
    private MySqlBackfillConstraintCatalog? _generationFoldedBackfillConstraints;
    private MySqlGeneratedColumnDependencyContractCatalog? _expectedGeneratedColumnDependencies;
    private MySqlGeneratedColumnDependencyContractCatalog? _generationGeneratedColumnDependencies;
    private MySqlGeneratedColumnDependencyContractCatalog? _generationFoldedGeneratedColumnDependencies;
    private bool _generationFoldedTableContractInvalid;
    private MySqlSafeMigrationRuntimePlan?[]? _plans;
    private bool _includeAnalysisEvidence;
    private bool _includeTransitionEvidence;
    private bool _completed;
    private Dictionary<int, MySqlSafeMigrationRuntimeDiagnostic>? _runtimeDiagnostics;
    private int _runtimeDiagnosticCapacity;

    /// <summary>Records authored native-guard context only while the outer generator owns the stream.</summary>
    /// <param name="diagnostic">The exact handler ordinal and bounded correlation.</param>
    internal void RecordRuntimeDiagnostic(
        MySqlSafeMigrationRuntimeDiagnostic diagnostic
    )
    {
        if (HasGenerationContract)
        {
            // WHY: The complete authored safe-operation count is already known.
            // Allocate its lookup once, lazily, rather than repeatedly growing
            // a large migration's dictionary or allocating for ordinary EF SQL.
            (_runtimeDiagnostics ??= new Dictionary<int, MySqlSafeMigrationRuntimeDiagnostic>(
                _runtimeDiagnosticCapacity)).Add(diagnostic.Ordinal, diagnostic);
        }
    }

    /// <summary>Finds context only when both the native command marker and its generation correlation agree.</summary>
    /// <param name="commandText">The unchanged original native command text.</param>
    /// <returns>The owning diagnostic, or null for ordinary provider operations.</returns>
    internal MySqlSafeMigrationRuntimeDiagnostic? FindRuntimeDiagnostic(
        string commandText
    )
    {
        if (_runtimeDiagnostics is not null
            && MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(commandText, out var ordinal, out var correlation)
            && _runtimeDiagnostics.TryGetValue(ordinal, out var diagnostic)
            && correlation.Equals(diagnostic.Correlation, StringComparison.Ordinal))
        {
            return diagnostic;
        }

        return null;
    }

    /// <summary>Gets whether an incomplete analysis capture is active.</summary>
    public bool IsActive => _expected is not null && !_completed;

    /// <summary>Gets whether runtime SQL generation owns an ordered transition catalog.</summary>
    public bool HasGenerationContract =>
        _generationTableConstraints is not null
        && _generationUniqueIndexes is not null
        && _generationDatabaseQualifiers is not null;

    /// <summary>Gets every explicit database identity required by runtime generation.</summary>
    public IReadOnlyList<string> GenerationDatabaseQualifiers =>
        _generationDatabaseQualifiers
        ?? throw new InvalidOperationException("No MySQL SafeMigrations runtime generation scope is active.");

    /// <summary>Gets whether the active capture requests detailed diagnostic SQL.</summary>
    public bool IncludeAnalysisEvidence => IsActive && _includeAnalysisEvidence;

    /// <summary>Gets whether the active capture requests physical transition SQL.</summary>
    public bool IncludeTransitionEvidence => IsActive && _includeTransitionEvidence;

    /// <summary>Requests independent replacement rows only for a planned old-PK removal in active analysis.</summary>
    /// <param name="operation">The exact immutable target being captured.</param>
    /// <returns>Whether this capture, never ordinary runtime SQL, needs replacement-row evidence.</returns>
    internal bool RequiresPrimaryKeyReplacementRows(
        SafeMigrationOperation operation
    ) => IsActive && _expectedPrimaryKeyReplacements?.Contains(operation) == true;

    /// <summary>Begins an ordered capture for one immutable operation batch.</summary>
    /// <param name="operations">The safe operations expected from Doka's handler pipeline.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        return Begin(
            operations,
            CreateExpectedUniqueIndexes(operations),
            SafeMigrationExpectedTableConstraints.FromOperations(
                operations,
                static schema => schema,
                StringComparer.Ordinal,
                '`',
                false,
                StringComparer.OrdinalIgnoreCase),
            includeAnalysisEvidence: false,
            includeTransitionEvidence: false);
    }

    /// <summary>Begins one bounded capture against a complete expected-index catalog.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <param name="includeAnalysisEvidence">Whether to build detailed diagnostic SQL.</param>
    /// <param name="includeTransitionEvidence">Whether to build physical transition SQL.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false
    ) => Begin(
        operations,
        expectedUniqueIndexes,
        SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            expectedUniqueIndexes.NormalizeSchema,
            expectedUniqueIndexes.TableComparer,
            '`',
            false,
            StringComparer.OrdinalIgnoreCase),
        includeAnalysisEvidence,
        includeTransitionEvidence);

    /// <summary>Begins one bounded capture against complete expected table catalogs.</summary>
    /// <param name="operations">The bounded operation window captured in order.</param>
    /// <param name="expectedUniqueIndexes">The complete migration's expected unique-index catalog.</param>
    /// <param name="expectedTableConstraints">The complete migration's table-constraint transition catalog.</param>
    /// <param name="includeAnalysisEvidence">Whether to build detailed diagnostic SQL.</param>
    /// <param name="includeTransitionEvidence">Whether to build physical transition SQL.</param>
    /// <param name="backfillConstraints">Complete-stream constraint hazards retained across bounded windows.</param>
    /// <param name="generatedColumnDependencies">Untrusted candidates for exact rename/drop instances.</param>
    /// <param name="primaryKeyReplacements">Full-stream replacement targets retained across bounded windows.</param>
    /// <returns>A lease that owns capture completion and cleanup.</returns>
    public Lease Begin(
        IReadOnlyList<SafeMigrationOperation> operations,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
            expectedTableConstraints,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false,
        MySqlBackfillConstraintCatalog? backfillConstraints = null,
        MySqlGeneratedColumnDependencyContractCatalog? generatedColumnDependencies = null,
        IReadOnlySet<SafeMigrationOperation>? primaryKeyReplacements = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(expectedUniqueIndexes);
        ArgumentNullException.ThrowIfNull(expectedTableConstraints);

        if (_expected is not null || HasGenerationContract)
        {
            throw new InvalidOperationException("A MySQL SafeMigrations plan capture is already active in this scope.");
        }

        _expected = operations.ToArray();
        if (_expected.Any(static operation => operation is null))
        {
            Clear();
            throw new ArgumentException("The operation batch cannot contain null entries.", nameof(operations));
        }

        _plans = new MySqlSafeMigrationRuntimePlan?[_expected.Length];
        _expectedUniqueIndexes = expectedUniqueIndexes;
        _expectedPrimaryKeyReplacements = primaryKeyReplacements
            ?? CreatePrimaryKeyReplacementTargets(operations, expectedUniqueIndexes.CurrentDatabase,
                expectedUniqueIndexes.TableComparer);

        _expectedTableConstraints = expectedTableConstraints;
        _expectedBackfillConstraints = backfillConstraints
            ?? MySqlBackfillConstraintCatalog.Create(
                operations,
                expectedUniqueIndexes.CurrentDatabase,
                expectedUniqueIndexes.TableComparer);

        _expectedGeneratedColumnDependencies = generatedColumnDependencies
            ?? MySqlGeneratedColumnDependencyContractCatalog.Create(
                operations,
                expectedUniqueIndexes.CurrentDatabase,
                expectedUniqueIndexes.TableComparer);

        _includeAnalysisEvidence = includeAnalysisEvidence;
        _includeTransitionEvidence = includeTransitionEvidence;

        _completed = false;

        return new Lease(this);
    }

    /// <summary>Begins runtime generation for one complete ordered operation stream.</summary>
    /// <param name="operations">The migration operations passed to the provider generator.</param>
    /// <param name="targetModelCandidates">
    /// Operation-bound whole model shapes requiring native full-set equality.
    /// </param>
    /// <returns>A lease that clears the transition catalog after generation.</returns>
    public GenerationLease BeginGeneration(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyDictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>>? targetModelCandidates =
            null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (IsActive || HasGenerationContract)
        {
            throw new InvalidOperationException(
                "A MySQL SafeMigrations analysis or generation scope is already active.");
        }

        ClearGeneration();
        var safeOperations = operations
            .OfType<SafeMigrationOperation>()
            .ToArray();

        _runtimeDiagnosticCapacity = safeOperations.Length;
        var databaseQualifiers = MySqlSafeMigrationCatalogSqlBuilder.GetDatabaseQualifiers(operations);
        var guardedDatabase = databaseQualifiers.Length == 1 ? databaseQualifiers[0] : null;
        var tableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            schema => MySqlTableIdentity.NormalizeDatabase(schema, guardedDatabase),
            StringComparer.Ordinal,
            '`',
            false,
            StringComparer.OrdinalIgnoreCase);

        var uniqueIndexes = CreateExpectedUniqueIndexes(safeOperations, guardedDatabase);
        var hasNativeAlternatives =
            (safeOperations.Any(static operation =>
                    operation.Intent is EnsureTableIntent { Mode: SafeMigrationTableMode.StrictDefinition })
                || safeOperations.Any(RequiresAlterBackfillContract))
            && HasCaseAmbiguousOwners(safeOperations, guardedDatabase);

        if (hasNativeAlternatives)
        {
            // WHY: Generation cannot read the server profile. Keep both
            // owner interpretations and let guarded native SQL select one;
            // an invalid folded stream must not reject a valid mode-zero one.
            try
            {
                _generationFoldedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
                    operations,
                    schema => MySqlTableIdentity.NormalizeDatabase(schema, guardedDatabase),
                    StringComparer.OrdinalIgnoreCase,
                    '`',
                    false,
                    StringComparer.OrdinalIgnoreCase);

                _generationFoldedUniqueIndexes = CreateExpectedUniqueIndexes(
                    safeOperations,
                    guardedDatabase,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException)
            {
                _generationFoldedTableContractInvalid = true;
            }

            _generationFoldedBackfillConstraints = MySqlBackfillConstraintCatalog.Create(
                safeOperations,
                guardedDatabase,
                StringComparer.OrdinalIgnoreCase);
        }

        // WHY: Generation has no connection from which to prove DATABASE().
        // A single candidate may merge with unqualified identities only
        // because every generated SafeMigrations command receives the same
        // runtime guard. Multiple candidates remain distinct and make that
        // shared guard fail before any command mutates either database.
        _generationDatabaseQualifiers = databaseQualifiers;
        _generationTableConstraints = tableConstraints;
        _generationUniqueIndexes = uniqueIndexes;
        _generationBackfillConstraints = MySqlBackfillConstraintCatalog.Create(safeOperations, guardedDatabase);
        _generationGeneratedColumnDependencies = MySqlGeneratedColumnDependencyContractCatalog.Create(
            operations,
            guardedDatabase,
            targetModelCandidates: targetModelCandidates);

        _generationFoldedGeneratedColumnDependencies =
            safeOperations.Any(static operation => operation.Intent is RenameColumnIntent or DropColumnIntent)
            && (targetModelCandidates is { Count: > 0 } || HasCaseAmbiguousOwners(safeOperations, guardedDatabase))
                ? MySqlGeneratedColumnDependencyContractCatalog.Create(
                    operations,
                    guardedDatabase,
                    StringComparer.OrdinalIgnoreCase,
                    targetModelCandidates)
                : _generationGeneratedColumnDependencies;

        return new GenerationLease(this);
    }

    /// <summary>Builds the unique-index catalog shared by every bounded capture window.</summary>
    /// <param name="operations">The complete ordered SafeMigrations operation set.</param>
    /// <param name="currentDatabase">
    /// The selected database whose explicit qualifier is equivalent to an unqualified table,
    /// or null when runtime identity normalization is unavailable.
    /// </param>
    /// <param name="tableComparer">
    /// Explicitly qualified owner equality; connection-free callers retain exact names.
    /// </param>
    /// <returns>The expected unique-index definitions keyed by physical table identity.</returns>
    public static MySqlExpectedUniqueIndexCatalog CreateExpectedUniqueIndexes(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase = null,
        StringComparer? tableComparer = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        var comparer = tableComparer ?? StringComparer.Ordinal;

        // Only EnsureTable consumes this cross-operation catalog. Avoid
        // materializing the complete expected schema for unrelated batches.
        if (!operations.Any(static operation => operation.Intent is EnsureTableIntent))
        {
            SafeMigrationExpectedIndexTransitions.Validate(
                operations,
                schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase),
                comparer);

            return new MySqlExpectedUniqueIndexCatalog(s_emptyUniqueIndexCatalog, currentDatabase, comparer);
        }

        var definitions = new Dictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>>(
            new MySqlTableIdentityComparer(comparer));

        foreach (var table in SafeMigrationExpectedCatalog.Create(
                     operations,
                     schema => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase),
                     comparer))
        {
            if (table.UniqueIndexes.Count == 0)
            {
                continue;
            }

            var key = MySqlTableIdentity.Create(table.Table, table.Schema, currentDatabase);
            var indexes = table
                .IndexDefinitions
                .Values
                .Where(static index => index.Unique)
                .OrderBy(static index => index.Name, StringComparer.Ordinal)
                .ToArray();

            if (!definitions.TryAdd(key, indexes))
            {
                throw new InvalidOperationException(
                    "The migration contains multiple expected table definitions for one MySQL database object.");
            }
        }

        return new MySqlExpectedUniqueIndexCatalog(definitions, currentDatabase, comparer);
    }

    /// <summary>Identifies newly admitted alterations whose backfill must honor ordered constraints.</summary>
    /// <param name="operation">The operation being captured or rendered.</param>
    /// <returns>Whether declared constraint participation must be retained.</returns>
    internal static bool RequiresAlterBackfillContract(
        SafeMigrationOperation operation
    ) => operation is
        {
            Policy: SafeMigrationPolicy.RepairIfSafe,
            Intent: AlterColumnIntent
            {
                OldDefinition: { IsNullable: true } source, Definition: { IsNullable: false } target,
            },
        }
        && !SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, target)
        && SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(target.DefaultValue);

    /// <summary>Gets whether declared constraints require a NULL-free backfill proof for this operation.</summary>
    /// <param name="operation">The exact operation captured or rendered.</param>
    /// <returns>Whether complete-stream declarations constrain this backfill.</returns>
    public bool RequiresNullFreeBackfill(
        SafeMigrationOperation operation
    ) => (IsActive ? _expectedBackfillConstraints : _generationBackfillConstraints)?.RequiresNullFreeRows(operation)
        == true;

    /// <summary>Gets an additional backfill obligation selected only by the actual runtime owner mode.</summary>
    internal string? GetGenerationNullFreeBackfillCondition(
        SafeMigrationOperation operation
    ) => !IsActive
        && !RequiresNullFreeBackfill(operation)
        && _generationFoldedBackfillConstraints?.RequiresNullFreeRows(operation) == true
            ? "@@lower_case_table_names IN (1, 2)"
            : null;

    /// <summary>Gets the untrusted exact-owner candidate for this capture or generation scope.</summary>
    /// <param name="operation">The original rename/drop instance, not a source-unbound replacement.</param>
    /// <returns>The candidate requiring fresh native full-set equality, or null when none was captured.</returns>
    internal MySqlGeneratedColumnDependencyContract? GetGenerationGeneratedColumnDependencyContract(
        SafeMigrationOperation operation
    )
    {
        if (!IsActive
            && !HasGenerationContract)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations capture or generation scope is active.");
        }

        var catalog = IsActive ? _expectedGeneratedColumnDependencies : _generationGeneratedColumnDependencies;

        return catalog?.Get(operation);
    }

    /// <summary>Gets the runtime folded-owner alternative without assuming the executing server profile.</summary>
    /// <param name="operation">The exact authored rename/drop instance.</param>
    /// <returns>The folded candidate for native-guarded selection, or null during native-qualified analysis.</returns>
    internal MySqlGeneratedColumnDependencyContract? GetGenerationFoldedGeneratedColumnDependencyContract(
        SafeMigrationOperation operation
    ) => IsActive ? null : _generationFoldedGeneratedColumnDependencies?.Get(operation);

    /// <summary>Gets this operation's independent whole model candidate without granting prefix authority.</summary>
    internal MySqlGeneratedColumnDependencyContract? GetGenerationGeneratedColumnModelCandidate(
        SafeMigrationOperation operation
    )
    {
        if (!IsActive
            && !HasGenerationContract)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations capture or generation scope is active.");
        }

        var catalog = IsActive ? _expectedGeneratedColumnDependencies : _generationGeneratedColumnDependencies;

        return catalog?.GetModelCandidate(operation);
    }

    /// <summary>Gets only the folded-owner model alternative for native runtime profile selection.</summary>
    internal MySqlGeneratedColumnDependencyContract? GetGenerationFoldedGeneratedColumnModelCandidate(
        SafeMigrationOperation operation
    ) => IsActive ? null : _generationFoldedGeneratedColumnDependencies?.GetModelCandidate(operation);

    /// <summary>Gets the folded strict-table alternative without assuming the executing server's profile.</summary>
    internal MySqlTableContractAlternative? GetGenerationTableAlternative(
        EnsureTableIntent intent
    )
    {
        if (!HasGenerationContract
            || intent.Mode != SafeMigrationTableMode.StrictDefinition)
        {
            return null;
        }

        if (_generationFoldedTableContractInvalid)
        {
            return new MySqlTableContractAlternative(false, s_emptyUniqueIndexes, null);
        }

        return _generationFoldedTableConstraints is null || _generationFoldedUniqueIndexes is null
            ? null
            : new MySqlTableContractAlternative(
                true,
                _generationFoldedUniqueIndexes.Get(intent.Definition.Table, intent.Definition.Schema)
                ?? s_emptyUniqueIndexes,
                _generationFoldedTableConstraints.GetValueOrDefault(
                    (intent.Definition.Schema, intent.Definition.Table)));
    }

    /// <summary>Detects mixed spellings which can change a strict-table or backfill owner contract.</summary>
    private static bool HasCaseAmbiguousOwners(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase
    )
    {
        var owners = new Dictionary<MySqlTableIdentity, string>(
            new MySqlTableIdentityComparer(StringComparer.OrdinalIgnoreCase));

        foreach (var operation in operations)
        {
            (string? Table, string? Schema) owner = operation.Intent switch
            {
                EnsureTableIntent value => (value.Definition.Table, value.Definition.Schema),
                DropTableIntent value => (value.Table, value.Schema),
                RenameTableIntent value => (value.Name, value.Schema),
                EnsureColumnIntent value => (value.Table, value.Schema),
                AlterColumnIntent value => (value.Table, value.Schema),
                DropColumnIntent value => (value.Table, value.Schema),
                RenameColumnIntent value => (value.Table, value.Schema),
                EnsureIndexIntent value => (value.Definition.Table, value.Definition.Schema),
                DropIndexIntent value => (value.Table, value.Schema),
                RenameIndexIntent value => (value.Table, value.Schema),
                EnsurePrimaryKeyIntent value => (value.Definition.Table, value.Definition.Schema),
                DropPrimaryKeyIntent value => (value.Table, value.Schema),
                EnsureUniqueConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
                DropUniqueConstraintIntent value => (value.Table, value.Schema),
                EnsureCheckConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
                DropCheckConstraintIntent value => (value.Table, value.Schema),
                EnsureForeignKeyIntent value => (value.Definition.Table, value.Definition.Schema),
                DropForeignKeyIntent value => (value.Table, value.Schema),
                _ => ((string?)null, (string?)null),
            };

            if (owner.Table is not null
                && Add(owner.Table, owner.Schema))
            {
                return true;
            }

            if (operation.Intent is RenameTableIntent rename
                && Add(rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema))
            {
                return true;
            }
        }

        return false;

        bool Add(
            string table,
            string? schema
        )
        {
            var identity = MySqlTableIdentity.Create(table, schema, currentDatabase);
            if (owners.TryGetValue(identity, out var previous))
            {
                return !StringComparer.Ordinal.Equals(previous, table);
            }

            owners.Add(identity, table);

            return false;
        }
    }

    /// <summary>Gets expected unique-index definitions for a table in the active batch.</summary>
    /// <param name="table">The unqualified MySQL or MariaDB table name.</param>
    /// <param name="schema">The optional database qualifier.</param>
    /// <returns>The expected unique-index definitions, or an empty list.</returns>
    public IReadOnlyList<ExpectedIndexDefinition> GetExpectedUniqueIndexes(
        string table,
        string? schema = null
    )
    {
        if (!IsActive
            || _expectedUniqueIndexes is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        return _expectedUniqueIndexes.Get(table, schema) ?? s_emptyUniqueIndexes;
    }

    /// <summary>Gets the table-constraint transition contract for the active batch.</summary>
    /// <param name="intent">The table operation being planned.</param>
    /// <returns>The transition contract, or null when the batch has no table owner.</returns>
    public SafeMigrationExpectedTableConstraints? GetExpectedTableConstraints(
        EnsureTableIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (!IsActive
            || _expectedTableConstraints is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        return _expectedTableConstraints.GetValueOrDefault((intent.Definition.Schema, intent.Definition.Table));
    }

    /// <summary>Gets the ordered transition contract used by runtime SQL generation.</summary>
    /// <param name="intent">The table operation being generated.</param>
    /// <returns>The transition contract, or null when the stream has no table owner.</returns>
    public SafeMigrationExpectedTableConstraints? GetGenerationTableConstraints(
        EnsureTableIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        return _generationTableConstraints is not null
            ? _generationTableConstraints.GetValueOrDefault((intent.Definition.Schema, intent.Definition.Table))
            : throw new InvalidOperationException("No MySQL SafeMigrations runtime generation scope is active.");
    }

    /// <summary>Gets expected unique indexes for runtime SQL generation.</summary>
    /// <param name="table">The MySQL or MariaDB table name.</param>
    /// <param name="schema">The optional database qualifier.</param>
    /// <returns>The expected unique indexes, or an empty list.</returns>
    public IReadOnlyList<ExpectedIndexDefinition> GetGenerationUniqueIndexes(
        string table,
        string? schema = null
    ) => _generationUniqueIndexes is not null
        ? _generationUniqueIndexes.Get(table, schema) ?? s_emptyUniqueIndexes
        : throw new InvalidOperationException("No MySQL SafeMigrations runtime generation scope is active.");

    /// <summary>Records the provider plan emitted for one expected operation ordinal.</summary>
    /// <param name="ordinal">The zero-based operation ordinal.</param>
    /// <param name="operation">The exact expected operation instance.</param>
    /// <param name="plan">The emitted runtime catalog plan.</param>
    public void Record(
        int ordinal,
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);

        if (!IsActive
            || _expected is null
            || _plans is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        if ((uint)ordinal >= (uint)_expected.Length)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler produced an invalid capture ordinal.");
        }

        if (!ReferenceEquals(_expected[ordinal], operation))
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler captured a different operation instance.");
        }

        if (_plans[ordinal] is not null)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations handler captured an operation more than once.");
        }

        _plans[ordinal] = plan;
    }

    private MySqlSafeMigrationRuntimePlan[] Complete()
    {
        if (!IsActive
            || _plans is null)
        {
            throw new InvalidOperationException("No MySQL SafeMigrations plan capture is active.");
        }

        if (_plans.Any(static plan => plan is null))
        {
            throw new InvalidOperationException("The MySQL SafeMigrations handler did not capture every operation.");
        }

        var result = _plans
            .Cast<MySqlSafeMigrationRuntimePlan>()
            .ToArray();

        _completed = true;

        return result;
    }

    /// <summary>Requests row evidence only for exact replacement targets preceded by an old-PK removal.</summary>
    /// <param name="operations">The complete ordered safe-operation analysis stream.</param>
    /// <param name="currentDatabase">The native selected database used to normalize explicit qualifiers.</param>
    /// <param name="tableComparer">The independently captured physical owner identity.</param>
    /// <returns>The bounded exact target set, shared across capture windows without copying.</returns>
    internal static IReadOnlySet<SafeMigrationOperation> CreatePrimaryKeyReplacementTargets(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase,
        StringComparer tableComparer
    )
    {
        HashSet<MySqlTableIdentity>? dropped = null;
        HashSet<SafeMigrationOperation>? result = null;
        foreach (var operation in operations)
        {
            switch (operation.Intent)
            {
                case DropPrimaryKeyIntent drop:
                    (dropped ??= new HashSet<MySqlTableIdentity>(new MySqlTableIdentityComparer(tableComparer))).Add(
                        MySqlTableIdentity.Create(drop.Table, drop.Schema, currentDatabase));
                    break;
                case EnsurePrimaryKeyIntent ensure when dropped is not null:
                    if (dropped.Remove(MySqlTableIdentity.Create(
                            ensure.Definition.Table, ensure.Definition.Schema, currentDatabase)))
                    {
                        // WHY: This is probe selection, not acceptance. Core
                        // independently requires the actual removal decision
                        // before the captured rows can authorize a new key.
                        (result ??= new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance)).Add(operation);
                    }

                    break;
                case DropTableIntent drop when dropped is not null:
                    dropped.Remove(MySqlTableIdentity.Create(drop.Table, drop.Schema, currentDatabase));
                    break;
                case EnsureTableIntent ensure when dropped is not null:
                    dropped.Remove(MySqlTableIdentity.Create(
                        ensure.Definition.Table, ensure.Definition.Schema, currentDatabase));
                    break;
                case RenameTableIntent rename when dropped is not null:
                    dropped.Remove(MySqlTableIdentity.Create(rename.Name, rename.Schema, currentDatabase));
                    dropped.Remove(MySqlTableIdentity.Create(rename.NewName ?? rename.Name,
                        rename.NewSchema ?? rename.Schema, currentDatabase));
                    break;
            }
        }

        return result ?? s_emptyPrimaryKeyReplacements;
    }

    private void Clear()
    {
        _expectedPrimaryKeyReplacements = null;
        _expectedBackfillConstraints = null;
        _expectedGeneratedColumnDependencies = null;
        if (_expected is not null)
        {
            Array.Clear(_expected);
        }

        if (_plans is not null)
        {
            Array.Clear(_plans);
        }

        _expected = null;
        _expectedTableConstraints = null;
        _expectedUniqueIndexes = null;
        _plans = null;
        _includeAnalysisEvidence = false;
        _includeTransitionEvidence = false;
        _completed = false;
    }

    private void ClearGeneration()
    {
        _runtimeDiagnostics = null;
        _runtimeDiagnosticCapacity = 0;
        _generationBackfillConstraints = null;
        _generationFoldedBackfillConstraints = null;
        _generationGeneratedColumnDependencies = null;
        _generationFoldedGeneratedColumnDependencies = null;
        _generationDatabaseQualifiers = null;
        _generationTableConstraints = null;
        _generationFoldedTableConstraints = null;
        _generationUniqueIndexes = null;
        _generationFoldedUniqueIndexes = null;
        _generationFoldedTableContractInvalid = false;
    }

    /// <summary>Owns the lifetime of one active plan capture.</summary>
    internal sealed class Lease : IDisposable
    {
        private MySqlSafeMigrationPlanCapture? _owner;

        /// <summary>Initializes a lease for an active capture owner.</summary>
        /// <param name="owner">The capture owner to complete or clear.</param>
        public Lease(
            MySqlSafeMigrationPlanCapture owner
        )
        {
            _owner = owner;
        }

        /// <summary>Completes the capture and returns every plan in operation order.</summary>
        /// <returns>The complete ordered runtime-plan batch.</returns>
        public MySqlSafeMigrationRuntimePlan[] Complete()
        {
            ObjectDisposedException.ThrowIf(_owner is null, this);

            return _owner.Complete();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner?.Clear();
            _owner = null;
        }
    }

    /// <summary>Owns one runtime SQL-generation transition catalog.</summary>
    internal sealed class GenerationLease : IDisposable
    {
        private MySqlSafeMigrationPlanCapture? _owner;

        /// <summary>Initializes a lease for one runtime generation scope.</summary>
        /// <param name="owner">The generation scope owner.</param>
        public GenerationLease(
            MySqlSafeMigrationPlanCapture owner
        )
        {
            _owner = owner;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner?.ClearGeneration();
            _owner = null;
        }
    }
}

internal sealed class MySqlExpectedUniqueIndexCatalog(
    IReadOnlyDictionary<MySqlTableIdentity, IReadOnlyList<ExpectedIndexDefinition>> definitions,
    string? currentDatabase,
    StringComparer? tableComparer = null
)
{
    /// <summary>Gets owner equality explicitly selected by the capture's proven native profile.</summary>
    internal StringComparer TableComparer { get; } = tableComparer ?? StringComparer.Ordinal;

    /// <summary>Gets the independently qualified current database, or null when no such proof is available.</summary>
    internal string? CurrentDatabase => currentDatabase;

    /// <summary>Normalizes only the existing current-database qualifier contract.</summary>
    internal string? NormalizeSchema(
        string? schema
    ) => MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase);

    public IReadOnlyList<ExpectedIndexDefinition>? Get(
        string table,
        string? schema
    ) => definitions.GetValueOrDefault(MySqlTableIdentity.Create(table, schema, currentDatabase));
}

/// <summary>Stores the folded interpretation used only behind a native execution-time mode guard.</summary>
/// <param name="IsValid">Whether the folded ordered stream has a coherent contract.</param>
/// <param name="UniqueIndexes">Unique indexes declared for the folded physical owner.</param>
/// <param name="Constraints">Ordered table constraints declared for that owner.</param>
internal sealed record MySqlTableContractAlternative(
    bool IsValid,
    IReadOnlyList<ExpectedIndexDefinition> UniqueIndexes,
    SafeMigrationExpectedTableConstraints? Constraints
);

/// <summary>Keeps native table equality separate from the existing exact database qualifier contract.</summary>
internal sealed class MySqlTableIdentityComparer(StringComparer tableComparer) : IEqualityComparer<MySqlTableIdentity>
{
    /// <inheritdoc />
    public bool Equals(
        MySqlTableIdentity first,
        MySqlTableIdentity second
    ) => tableComparer.Equals(first.Table, second.Table)
        && StringComparer.Ordinal.Equals(first.Database, second.Database);

    /// <inheritdoc />
    public int GetHashCode(
        MySqlTableIdentity identity
    ) => HashCode.Combine(
        tableComparer.GetHashCode(identity.Table),
        identity.Database is null ? 0 : StringComparer.Ordinal.GetHashCode(identity.Database));
}

internal readonly record struct MySqlTableIdentity(
    string Table,
    string? Database
)
{
    public static MySqlTableIdentity Create(
        string table,
        string? database,
        string? currentDatabase
    ) => new(table, NormalizeDatabase(database, currentDatabase));

    public static string? NormalizeDatabase(
        string? database,
        string? currentDatabase
    ) => database is not null && StringComparer.Ordinal.Equals(database, currentDatabase) ? null : database;
}
