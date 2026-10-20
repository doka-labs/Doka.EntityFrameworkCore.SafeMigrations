namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private Dictionary<IndexInputRequest, bool?>? _indexInputProofs;
    private Dictionary<int, string>? _indexInputShortCircuitCodes;
    private HashSet<int>? _indexInputOrderedReplacementOrdinals;
    private Dictionary<int, List<ProviderIndexColumnProposal>>? _providerIndexColumnProposals;

    /// <summary>
    /// Qualifies newly executable physical index inputs against accepted definitions or real live types.
    /// </summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedIndexTypes(
        SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns)
    {
        if (analysis.ObservedState != SafeMigrationObservedState.Missing
            || operation.Intent is not EnsureIndexIntent intent)
        {
            return null;
        }

        var definition = intent.Definition;
        var physical = false;
        var unproven = false;
        foreach (var key in definition.Keys)
        {
            if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is not { } member)
            {
                continue;
            }

            physical = true;
            if (!IndexMemberTableMatches(definition, member)
                || !TryGetCurrentForeignKeyStorage(definition.Table, definition.Schema,
                    member.Name, columns, out var storage)
                || _indexInputProofs?.TryGetValue(IndexInputIdentity(storage.TypeOid,
                    definition, key, member), out var proof) != true || proof is null)
            {
                unproven = true;

                continue;
            }

            if (proof == false)
            {
                return IndexInputRejected(unsupported: true);
            }
        }

        if (!physical && PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(definition)
            != PostgreSqlIndexRequiredFlags.None)
        {
            if (_indexInputProofs?.TryGetValue(IndexInputIdentity(0, definition, null, null),
                out var proof) != true || proof is null)
            {
                unproven = true;
            }
            else if (proof == false)
            {
                return IndexInputRejected(unsupported: true);
            }
        }

        // WHY: Computed keys retain their separate return-type/execution
        // contract. Their underlying column OID is never substituted as an
        // expression-result certificate. INCLUDE is not an opclass input.
        return unproven ? IndexInputRejected(unsupported: false) : null;
    }

    /// <summary>Captures just real native input alternatives referenced before a requested index's ordinal.</summary>
    private async Task<Dictionary<IndexInputRequest, bool?>?> CaptureIndexInputProofsAsync(
        DbConnection connection, IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results,
        Dictionary<LiveColumnIdentity, LiveColumnStorage>? liveColumns,
        Dictionary<LiveColumnTypeIdentity, LiveColumnStorage>? liveTypes,
        int? commandTimeout, CancellationToken cancellationToken)
    {
        HashSet<LiveColumnIdentity>? relevant = null;
        var hasIndex = false;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operations[ordinal].Intent is not EnsureIndexIntent index
                || !NeedsIndexInputCapture(results[ordinal], ordinal))
            {
                continue;
            }

            hasIndex = true;
            foreach (var key in index.Definition.Keys)
            {
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is { } member)
                {
                    (relevant ??= []).Add(LiveColumnKey(
                        index.Definition.Table, index.Definition.Schema, member.Name));
                }
            }
        }

        if (!hasIndex)
        {
            return null;
        }

        var prior = new Dictionary<LiveColumnIdentity, HashSet<long>>();
        HashSet<LiveColumnIdentity>? priorCases = null;
        var requested = new HashSet<IndexInputRequest>();
        var requests = new List<IndexInputRequest>();
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_providerIndexColumnProposals?.TryGetValue(ordinal, out var proposals) == true)
            {
                foreach (var proposal in proposals)
                {
                    Record(proposal.Table, proposal.Schema, proposal.Definition);
                }
            }

            var intent = operations[ordinal].Intent;
            if (intent is EnsureTableIntent table)
            {
                foreach (var column in table.Definition.Columns)
                {
                    Record(table.Definition.Table, table.Definition.Schema, column);
                }
            }
            else if (intent is EnsureColumnIntent column)
            {
                Record(column.Table, column.Schema, column.Definition);
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresGeneratedCaseInputCapture(column))
                {
                    (priorCases ??= []).Add(LiveColumnKey(column.Table, column.Schema, column.Definition.Name));
                }
            }
            else if (intent is AlterColumnIntent altered)
            {
                Record(altered.Table, altered.Schema, altered.Definition);
            }
            else if (intent is RenameColumnIntent rename
                && prior.TryGetValue(LiveColumnKey(rename.Table, rename.Schema, rename.Name), out var types))
            {
                var name = LiveColumnKey(rename.Table, rename.Schema, rename.NewName);
                if (!prior.TryGetValue(name, out var renamed))
                {
                    renamed = [];
                    prior.Add(name, renamed);
                }

                renamed.UnionWith(types);
            }

            if (intent is not EnsureIndexIntent index || !NeedsIndexInputCapture(results[ordinal], ordinal))
            {
                continue;
            }

            Add(IndexInputIdentity(0, index.Definition, null, null));
            if (!index.Definition.Unique && index.Definition.Keys.Any(key =>
                PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is { } member
                && priorCases?.Contains(LiveColumnKey(index.Definition.Table,
                    index.Definition.Schema, member.Name)) == true))
            {
                // WHY: Member absence cannot prove availability in PostgreSQL's
                // shared schema relation namespace. Capture that separate fact
                // in the existing bounded metadata batch, not another roundtrip.
                Add(GeneratedIndexAbsenceIdentity(index.Definition));
            }
            foreach (var key in index.Definition.Keys)
            {
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is not { } member
                    || !IndexMemberTableMatches(index.Definition, member))
                {
                    continue;
                }

                var name = LiveColumnKey(index.Definition.Table, index.Definition.Schema, member.Name);
                if (liveColumns?.TryGetValue(name, out var storage) == true)
                {
                    Add(IndexInputIdentity(storage.TypeOid, index.Definition, key, member));
                }

                if (prior.TryGetValue(name, out var alternatives))
                {
                    foreach (var type in alternatives)
                    {
                        Add(IndexInputIdentity(type, index.Definition, key, member));
                    }
                }
            }
        }

        var proofs = new Dictionary<IndexInputRequest, bool?>(requests.Count);
        await SafeMigrationCatalogProbeBatch.ReadAsync(connection, requests.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, offset) => BuildIndexInputStatement(command, requests, offset),
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 2)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }

                var next = offset;
                while (await reader.ReadAsync(token))
                {
                    if (reader.GetValue(0) is not int ordinal || ordinal != next || ordinal >= offset + count
                        || !reader.IsDBNull(1) && reader.GetValue(1) is not bool)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    proofs.Add(requests[ordinal], reader.IsDBNull(1) ? null : reader.GetBoolean(1));
                    next++;
                }

                if (next != offset + count)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }
            }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return proofs;

        void Add(IndexInputRequest request)
        {
            if (requested.Add(request))
            {
                requests.Add(request);
            }
        }

        void Record(string table, string? schema, ExpectedColumnDefinition column)
        {
            var name = LiveColumnKey(table, schema, column.Name);
            if (relevant?.Contains(name) != true || ProjectedColumnTypeIdentity(column) is not { } type
                || liveTypes?.TryGetValue(type, out var storage) != true)
            {
                return;
            }

            if (!prior.TryGetValue(name, out var types))
            {
                types = [];
                prior.Add(name, types);
            }

            types.Add(storage.TypeOid);
        }
    }

    /// <summary>Uses the existing statement, native batch, parameter, timeout and UTF-8 payload limits.</summary>
    private SafeMigrationCatalogProbeStatement BuildIndexInputStatement(SafeMigrationCatalogCommand command,
        IReadOnlyList<IndexInputRequest> requests, int offset)
    {
        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
        var selections = new List<string>();
        var sqlBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (offset + selections.Count < requests.Count
            && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            var ordinal = offset + selections.Count;
            var request = requests[ordinal];
            var checkpoint = parameters.Capture();
            var input = new PostgreSqlIndexInputSql(request.TypeOid.ToString(CultureInfo.InvariantCulture) + "::oid",
                parameters.AddString(request.Method), request.OperatorClassName is null ? null
                    : parameters.AddString(request.OperatorClassName), request.OperatorClassSchema is null ? null
                    : parameters.AddString(request.OperatorClassSchema), request.RequiredFlags);

            var proof = PostgreSqlIndexInputEligibilitySql.Render(input);
            if (request.AbsentRelationName is not null)
            {
                proof = "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class rel JOIN pg_catalog.pg_namespace ns "
                    + "ON ns.oid = rel.relnamespace WHERE ns.nspname = "
                    + (request.OwnerSchema is null ? "pg_catalog.current_schema()"
                        : parameters.AddString(request.OwnerSchema))
                    + " AND rel.relname = " + parameters.AddString(request.AbsentRelationName) + ")";
            }
            if (request.MemberSchema is not null)
            {
                var owner = request.OwnerSchema is null
                    ? "current_schema()" : parameters.AddString(request.OwnerSchema);
                proof = "CASE WHEN " + parameters.AddString(request.MemberSchema) + " = " + owner
                    + " THEN (" + proof + ") ELSE NULL::boolean END";
            }

            var selection = "SELECT " + ordinal.ToString(CultureInfo.InvariantCulture) + ", (" + proof + ")";
            var nextBytes = sqlBytes + Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator));

            if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, nextBytes + parameters.Utf8PayloadBytes,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                var rejectedParameters = parameters.Count;
                var rejectedBytes = nextBytes + parameters.Utf8PayloadBytes;
                parameters.Rollback(checkpoint);
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        ordinal, rejectedParameters, rejectedBytes);
                }

                break;
            }

            selections.Add(selection);
            sqlBytes = nextBytes;
        }

        command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
            + SafeMigrationCatalogQueryLimits.Trailer;

        return new SafeMigrationCatalogProbeStatement(selections.Count, parameters.Utf8PayloadBytes, offset);
    }

    /// <summary>
    /// Deduplicates native type alternatives under their requested method/opclass and qualifier contract.
    /// </summary>
    private static IndexInputRequest IndexInputIdentity(long type, ExpectedIndexDefinition definition,
        ExpectedIndexKeyDefinition? key, PostgreSqlIndexPhysicalMember? member)
    {
        var path = key?.OperatorClass?.Split('.', StringSplitOptions.None);
        if (path is { Length: < 1 or > 2 } || path?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new NotSupportedException("An operator class must be an unqualified or schema-qualified name.");
        }

        return new IndexInputRequest(type, definition.Method ?? "btree", path?[^1],
            path is { Length: 2 } ? path[0] : null,
            PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(definition),
            member?.PartCount == 3 ? definition.Schema : null,
            member?.PartCount == 3 ? member.Value.Schema : null);
    }

    /// <summary>Keys a native schema-relation-name absence proof inside the existing input protocol.</summary>
    private IndexInputRequest GeneratedIndexAbsenceIdentity(ExpectedIndexDefinition definition)
        => IndexInputIdentity(0, definition, null, null) with
        {
            OwnerSchema = PostgreSqlSafeMigrationSchemaIdentity.Normalize(definition.Schema, _currentSchema),
            AbsentRelationName = definition.Name,
        };

    /// <summary>Rejects another table's syntactic qualifier without inferring a same-named local member.</summary>
    private static bool IndexMemberTableMatches(ExpectedIndexDefinition definition,
        PostgreSqlIndexPhysicalMember member)
        => member.PartCount == 1 || member.PartCount is 2 or 3 && member.Table == definition.Table;

    /// <summary>
    /// Preserves independent Matching and stronger blockers while retaining accepted ordered replacements.
    /// </summary>
    private bool NeedsIndexInputCapture(SafeMigrationProviderAnalysis result, int ordinal)
        => result.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.PrerequisiteMissing
            or SafeMigrationObservedState.Different || result.Code == "index_input_operator_class"
            || _indexInputOrderedReplacementOrdinals?.Contains(ordinal) == true;

    /// <summary>Captures Matching inputs only after an earlier relevant typed drop.</summary>
    private void SelectOrderedIndexInputRequests(IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results, CancellationToken cancellationToken)
    {
        HashSet<LiveColumnIdentity>? droppedOwners = null;
        HashSet<string>? droppedSchemas = null;
        var stream = _analysisOperationStream ?? operations;
        var ordinal = 0;
        foreach (var operation in stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation is DropSchemaOperation nativeSchema)
            {
                (droppedSchemas ??= new(StringComparer.Ordinal)).Add(nativeSchema.Name);

                continue;
            }

            var safe = operation as SafeMigrationOperation;
            if (safe is not null
                && (ordinal >= operations.Count || !ReferenceEquals(safe, operations[ordinal])))
            {
                // WHY: Recursive standalone source analysis has no identity
                // in the outer stream and cannot borrow its later proposals.
                _indexInputOrderedReplacementOrdinals = null;

                return;
            }

            if (safe?.Intent is DropSchemaIntent schema)
            {
                (droppedSchemas ??= new(StringComparer.Ordinal)).Add(schema.Name);
                ordinal++;

                continue;
            }

            var owner = safe?.Intent switch
            {
                DropIndexIntent value => LiveColumnKey(value.Table, value.Schema, string.Empty),
                DropColumnIntent value => LiveColumnKey(value.Table, value.Schema, string.Empty),
                DropTableIntent value => LiveColumnKey(value.Table, value.Schema, string.Empty),
                _ => operation switch
                {
                    DropIndexOperation { Table: { } table } value
                        => LiveColumnKey(table, value.Schema, string.Empty),
                    DropColumnOperation value => LiveColumnKey(value.Table, value.Schema, string.Empty),
                    DropTableOperation value => LiveColumnKey(value.Name, value.Schema, string.Empty),
                    _ => (LiveColumnIdentity?)null,
                },
            };

            if (owner is { } removed)
            {
                (droppedOwners ??= []).Add(removed);
            }
            else if (safe?.Intent is EnsureIndexIntent index
                && results[ordinal].ObservedState == SafeMigrationObservedState.Matching
                && (droppedOwners?.Contains(LiveColumnKey(
                        index.Definition.Table, index.Definition.Schema, string.Empty)) == true
                    || PostgreSqlSafeMigrationSchemaIdentity.Normalize(index.Definition.Schema, _currentSchema)
                        is { } currentSchema && droppedSchemas?.Contains(currentSchema) == true))
            {
                // WHY: This is a capture request, not acceptance of the DROP.
                // Only Core's current ordered lifetime can later consume it.
                // Normal independent Matching streams retain no extra capture.
                (_indexInputOrderedReplacementOrdinals ??= []).Add(ordinal);
            }

            if (safe is not null)
            {
                ordinal++;
            }
        }

        if (ordinal != operations.Count)
        {
            _indexInputOrderedReplacementOrdinals = null;
        }
    }

    /// <summary>Captures only earlier declarations through the existing EF column factory.</summary>
    private void CaptureProviderIndexColumnProposals(IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results, CancellationToken cancellationToken)
    {
        if (_analysisOperationStream is null)
        {
            return;
        }

        HashSet<LiveColumnIdentity>? relevant = null;
        for (var requestedOrdinal = 0; requestedOrdinal < operations.Count; requestedOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operations[requestedOrdinal].Intent is not EnsureIndexIntent index
                || !NeedsIndexInputCapture(results[requestedOrdinal], requestedOrdinal))
            {
                continue;
            }

            foreach (var key in index.Definition.Keys)
            {
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is { } member)
                {
                    (relevant ??= []).Add(LiveColumnKey(
                        index.Definition.Table, index.Definition.Schema, member.Name));
                }
            }
        }

        if (relevant is null)
        {
            return;
        }

        Dictionary<int, List<ProviderIndexColumnProposal>>? captured = null;
        List<ProviderIndexColumnProposal>? pending = null;
        var ordinal = 0;
        foreach (var operation in _analysisOperationStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordinal == operations.Count)
            {
                if (operation is SafeMigrationOperation)
                {
                    return;
                }

                continue;
            }

            if (operation is SafeMigrationOperation safe)
            {
                if (ordinal >= operations.Count || !ReferenceEquals(safe, operations[ordinal]))
                {
                    return;
                }

                if (pending is not null)
                {
                    (captured ??= []).Add(ordinal, pending);
                    pending = null;
                }

                ordinal++;

                continue;
            }

            if (operation is AddColumnOperation or AlterColumnOperation)
            {
                Record((ColumnOperation)operation);
            }
            else if (operation is CreateTableOperation table)
            {
                foreach (var column in table.Columns)
                {
                    Record(column);
                }
            }
        }

        if (ordinal == operations.Count)
        {
            _providerIndexColumnProposals = captured;
        }

        void Record(ColumnOperation operation)
        {
            if (!relevant.Contains(LiveColumnKey(operation.Table, operation.Schema, operation.Name)))
            {
                return;
            }

            try
            {
                // WHY: Declared type metadata certifies no presence or native
                // execution. Core alone selects its current accepted ordinary
                // definition; SQL, target models and future proposals cannot
                // create one. Reuse the same factory as that existing projection.
                var definition = SafeMigrationExpectedDefinitionFactory.From(operation);
                (pending ??= []).Add(new ProviderIndexColumnProposal(
                    operation.Table, operation.Schema, definition));
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                // WHY: Unsupported EF facets keep their original opaque barrier.
                // An incomplete proposal supplies no new native binding evidence.
            }
        }
    }

    /// <summary>An earlier typed declaration, never permission to apply or infer a physical column.</summary>
    private readonly record struct ProviderIndexColumnProposal(
        string Table, string? Schema, ExpectedColumnDefinition Definition);

    /// <summary>Distinguishes a disproved native input from incomplete binding evidence.</summary>
    private static SafeMigrationProviderAnalysis IndexInputRejected(bool unsupported)
        => new(unsupported ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, unsupported ? "index_input_operator_class"
                : "index_input_binding_unproven");

    /// <summary>
    /// One immutable requested native binding; authored type or model presence is no physical certificate.
    /// </summary>
    private readonly record struct IndexInputRequest(long TypeOid, string Method, string? OperatorClassName,
        string? OperatorClassSchema, PostgreSqlIndexRequiredFlags RequiredFlags,
        string? OwnerSchema, string? MemberSchema, string? AbsentRelationName = null);
}
