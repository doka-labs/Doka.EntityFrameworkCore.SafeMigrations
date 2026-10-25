namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Tracks accepted physical FK dependencies without snapshotting complete table definitions.</summary>
internal sealed partial class SqlServerProjectedDependencyGraph : ISafeMigrationProjectedDependencyAnalyzer,
    ISafeMigrationLivePrerequisiteSource
{
    private readonly Dictionary<TableKey, int> _tables = [];
    private readonly HashSet<int> _missingTables = [];
    private readonly HashSet<TableKey> _nonTableNames = [];
    private readonly HashSet<int> _renameSafeTables = [];
    private readonly Dictionary<string, int> _schemas = new(StringComparer.Ordinal);
    private readonly HashSet<int> _missingSchemas = [];
    private readonly HashSet<int> _schemasWithOtherObjects = [];
    private readonly Dictionary<int, int> _tableSchemas = [];
    private readonly Dictionary<int, ForeignKeyEdge> _foreignKeys = [];
    private readonly Dictionary<ForeignKeyKey, int> _foreignKeyNames = [];
    private readonly Dictionary<ColumnKey, ColumnSafety> _columns = [];
    private readonly Dictionary<ColumnKey, int> _columnIds = [];
    private readonly HashSet<ColumnIdentity> _invalidatedStorage = [];
    private readonly Dictionary<int, TriggerSafety> _triggers = [];
    private readonly Dictionary<int, ExpectedTableDefinition> _createdTables = [];
    private readonly Dictionary<InlineProofKey, InlineStorageProof> _liveInlineReady = [];
    private readonly Dictionary<ExpectedForeignKeyDefinition, List<SparseStorageProof>> _sparseLiveReady = [];
    private readonly HashSet<ExpectedForeignKeyDefinition> _sparseCaptured = [];
    private readonly HashSet<SchemaObjectKey> _standaloneNames = [];
    private readonly HashSet<string> _standaloneObjectNames = [];
    private readonly Dictionary<SchemaObjectKey, ConstraintOccupancy> _constraintNames = [];
    private readonly SqlServerProjectedCandidateKeys _candidateKeys = new();
    private readonly Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> _inlinePhysicalWidthSupported;

    private Func<ExpectedColumnDefinition, ExpectedColumnDefinition, bool> _storageEquals = static (
            left,
            right
        ) => left.StoreType == right.StoreType
        && left.ClrType == right.ClrType
        && left.IsUnicode == right.IsUnicode
        && left.MaxLength == right.MaxLength
        && left.IsFixedLength == right.IsFixedLength
        && left.IsRowVersion == right.IsRowVersion
        && left.Precision == right.Precision
        && left.Scale == right.Scale
        && Equals(left.Collation, right.Collation);

    private int _nextTableId = -1;
    private int _nextForeignKeyId = -1;
    private int _nextSchemaId = -1;
    private bool _unknown;

    /// <summary>Creates an ordered graph using the provider's authored inline key-width proof.</summary>
    /// <param name="inlinePhysicalWidthSupported">The provider's bounded physical width and arity validator.</param>
    public SqlServerProjectedDependencyGraph(
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> inlinePhysicalWidthSupported
    )
    {
        _inlinePhysicalWidthSupported = inlinePhysicalWidthSupported;
    }

    /// <summary>Recovers only complete provider graph proofs after an accepted ordinary-table rename.</summary>
    /// <param name="operation">The operation following an opaque generic provider postcondition.</param>
    /// <param name="liveAnalysis">The immutable provider assessment before ordered mutations.</param>
    /// <param name="columns">The surviving accepted authored column contracts.</param>
    /// <returns>A complete ordered assessment, or null when the graph cannot prove it.</returns>
    internal SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (_unknown
            || liveAnalysis.IsInvariantUnsupported
            || liveAnalysis.ObservedState is SafeMigrationObservedState.Unsupported
                or SafeMigrationObservedState.DataBlocked)
        {
            return null;
        }

        switch (operation.Intent)
        {
            case EnsureSchemaIntent or DropSchemaIntent:
                return ValidateSchemaOperation(operation.Intent, liveAnalysis);
            case EnsureTableIntent table when liveAnalysis.ObservedState is SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.PrerequisiteMissing:
                var key = new TableKey(table.Definition.Schema ?? "dbo", table.Definition.Table);

                if (!_missingTables.Contains(GetTable(table.Definition.Table, table.Definition.Schema))
                    || _nonTableNames.Contains(key))
                {
                    return null;
                }

                if (!SchemaExists(table.Definition.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                var assessment = ValidateForeignKeys(
                    table.Definition.ForeignKeys,
                    liveAnalysis,
                    columns,
                    table.Definition);

                // WHY: Core cannot retain a complete existing-table model after
                // a rename. This graph instead binds each surviving parent key
                // and storage proof to its physical ids; no stale name or absent
                // row proof is promoted merely because the original table was missing.
                return assessment is
                    {
                        ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
                        Code: "classified_prerequisite_missing" or "inline_foreign_key_prerequisite"
                    }
                    && HasAllInlinePrerequisites(table.Definition, columns)
                        ? new SafeMigrationProviderAnalysis(
                            SafeMigrationObservedState.Missing,
                            SafeMigrationRepairCapability.None,
                            false,
                            "projected_inline_prerequisites_ready")
                        : assessment;
            case RenameTableIntent table:
                return liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                    ? null
                    : ValidateOrderedRename(table);
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is EnsureSchemaIntent or DropSchemaIntent)
        {
            return ValidateSchemaOperation(operation.Intent, projectedAnalysis);
        }

        var incomingOnlyDrop = operation.Intent is DropTableIntent
            && projectedAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different, Code: "incoming_foreign_key_dependency"
            };

        var incomingOnlyKeyDrop = operation.Intent is DropPrimaryKeyIntent or DropUniqueConstraintIntent
            && projectedAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different, Code: "incoming_foreign_key_key_dependency"
            };

        if (projectedAnalysis.IsInvariantUnsupported
            || projectedAnalysis.ObservedState is SafeMigrationObservedState.Unsupported
                or SafeMigrationObservedState.DataBlocked
            || (projectedAnalysis.ObservedState == SafeMigrationObservedState.Different
                && !incomingOnlyDrop
                && !incomingOnlyKeyDrop))
        {
            return projectedAnalysis;
        }

        if (_unknown
            && (incomingOnlyKeyDrop
                || operation.Intent is EnsureForeignKeyIntent or DropTableIntent
                || operation.Intent is EnsureTableIntent { Definition.ForeignKeys.Count: > 0 }))
        {
            return Rejected("projected_dependency_state_unknown", unknown: true);
        }

        switch (operation.Intent)
        {
            case DropPrimaryKeyIntent key when incomingOnlyKeyDrop:
                return ReconcileReferencedKeyDrop(key.Table, key.Schema, key.Name, projectedAnalysis);
            case DropUniqueConstraintIntent key when incomingOnlyKeyDrop:
                return ReconcileReferencedKeyDrop(key.Table, key.Schema, key.Name, projectedAnalysis);
            case EnsureTableIntent table when _missingTables.Contains(
                GetTable(table.Definition.Table, table.Definition.Schema)):
                if (!SchemaExists(table.Definition.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                var tableAnalysis = ValidateForeignKeys(
                    table.Definition.ForeignKeys,
                    projectedAnalysis,
                    columns,
                    table.Definition);

                return tableAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
                        Code: "classified_prerequisite_missing" or "inline_foreign_key_prerequisite"
                    }
                    && HasAllInlinePrerequisites(table.Definition, columns)
                        ? new SafeMigrationProviderAnalysis(
                            SafeMigrationObservedState.Missing,
                            SafeMigrationRepairCapability.None,
                            false,
                            "projected_inline_prerequisites_ready")
                        : tableAnalysis;
            case EnsureForeignKeyIntent foreignKey
                when projectedAnalysis.ObservedState != SafeMigrationObservedState.Matching:
                return ValidateStandaloneForeignKey(foreignKey.Definition, projectedAnalysis, columns);
            case DropTableIntent table:
                var tableId = GetTable(table.Table, table.Schema);
                if (_foreignKeys.Values.Any(edge => edge.Principal == tableId && edge.Dependent != tableId))
                {
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.None,
                        false,
                        "projected_incoming_foreign_key");
                }

                if (incomingOnlyDrop)
                {
                    // WHY: Only this provider classification proves every non-FK
                    // blocker absent. Reconcile the immutable FK snapshot with
                    // accepted removals without weakening other DROP contracts.
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None,
                        true,
                        "projected_incoming_foreign_keys_removed");
                }

                break;
            case RenameTableIntent table:
                if (!SchemaExists(table.NewSchema ?? table.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                if (projectedAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
                        Code: "classified_prerequisite_missing"
                    }
                    && !_missingTables.Contains(GetTable(table.Name, table.Schema))
                    && _missingTables.Contains(GetTable(table.NewName ?? table.Name, table.NewSchema ?? table.Schema)))
                {
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None,
                        false,
                        "projected_schema_transfer_ready");
                }

                break;
        }

        return projectedAnalysis;
    }

    /// <summary>Discharges only the accepted removals referencing this exact physical key.</summary>
    private SafeMigrationProviderAnalysis ReconcileReferencedKeyDrop(
        string table,
        string? schema,
        string name,
        SafeMigrationProviderAnalysis analysis
    )
    {
        var tableId = GetTable(table, schema);
        if (!_candidateKeys.TryGetIndex(tableId, name, out var index)
            || _foreignKeys.Values.Any(edge => edge.Principal == tableId
                && (edge.PrincipalKeyIndex is null or <= 0 || edge.PrincipalKeyIndex == index)))
        {
            return analysis;
        }

        // WHY: Another FK may legitimately reference an independent UNIQUE
        // key on the same table. New edges without a resolved backing index
        // remain blockers; mere table identity is neither proof nor rejection.
        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            false,
            "projected_incoming_key_references_removed");
    }

    private SafeMigrationProviderAnalysis? ValidateOrderedRename(
        RenameTableIntent intent
    )
    {
        var source = GetTable(intent.Name, intent.Schema);
        var targetName = new TableKey(intent.NewSchema ?? intent.Schema ?? "dbo", intent.NewName ?? intent.Name);
        var target = GetTable(targetName.Table, targetName.Schema);
        if (!_renameSafeTables.Contains(source)
            || _missingTables.Contains(source))
        {
            return null;
        }

        if (!SchemaExists(targetName.Schema))
        {
            return Rejected("projected_schema_missing");
        }

        if (target != source
            && (!_missingTables.Contains(target) || _nonTableNames.Contains(targetName)))
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None,
                false,
                "projected_rename_target_occupied");
        }

        if (targetName.Table != intent.Name
            && targetName.Schema != (intent.Schema ?? "dbo"))
        {
            var intermediate = new TableKey(intent.Schema ?? "dbo", targetName.Table);
            if (!_tables.TryGetValue(intermediate, out var intermediateId)
                || !_missingTables.Contains(intermediateId)
                || _nonTableNames.Contains(intermediate))
            {
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.None,
                    false,
                    "projected_rename_target_occupied");
            }
        }

        // WHY: Only an accepted prior rename carries this proof that the same
        // ordinary physical table has no unsafe textual dependencies. Catalog
        // absence under the later source name alone cannot authorize a rename.
        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            target == source,
            "projected_rename_source_ready");
    }

    private int GetTable(
        string table,
        string? schema
    )
    {
        var key = new TableKey(schema ?? "dbo", table);
        if (!_tables.TryGetValue(key, out var id))
        {
            id = _nextTableId--;
            _tables[key] = id;
        }

        return id;
    }

    private int GetSchema(
        string schema
    )
    {
        if (!_schemas.TryGetValue(schema, out var id))
        {
            id = _nextSchemaId--;
            _schemas[schema] = id;
        }

        return id;
    }

    private bool SchemaExists(
        string? schema
    ) => !_missingSchemas.Contains(GetSchema(schema ?? "dbo"));

    private SafeMigrationProviderAnalysis ValidateSchemaOperation(
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (analysis.ObservedState is SafeMigrationObservedState.Unsupported or SafeMigrationObservedState.DataBlocked)
        {
            return analysis;
        }

        if (_unknown)
        {
            return Rejected("projected_schema_state_unknown", unknown: true);
        }

        var name = intent is EnsureSchemaIntent ensure ? ensure.Name : ((DropSchemaIntent)intent).Name;
        var schema = GetSchema(name);
        var exists = !_missingSchemas.Contains(schema);

        if (intent is DropSchemaIntent
            && exists
            && (_schemasWithOtherObjects.Contains(schema)
                || _tableSchemas.Any(pair => pair.Value == schema && !_missingTables.Contains(pair.Key))))
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None,
                false,
                "projected_schema_not_empty");
        }

        // WHY: Schema existence is ordered state, not the immutable analyzer
        // snapshot. Every non-table object remains a blocker; table drops and
        // transfers change emptiness only after their decisions were accepted.

        return new SafeMigrationProviderAnalysis(
            exists ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            intent is EnsureSchemaIntent ? exists : !exists,
            exists ? "projected_schema_present" : "projected_schema_absent");
    }

    private static SafeMigrationProviderAnalysis Rejected(
        string code,
        bool unknown = false
    ) => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false, code)
    {
        IsOpaqueProjectionUnknown = unknown,
    };

    private readonly record struct SchemaObjectKey(
        string Schema,
        string Name
    );

    private readonly record struct ConstraintOccupancy(
        int Object,
        int Owner,
        string Kind
    );

    private readonly record struct ColumnIdentity(
        int Table,
        int Column
    );

    private readonly record struct TableKey(
        string Schema,
        string Table
    );

    private readonly record struct ForeignKeyKey(
        int Table,
        string Name
    );

    private readonly record struct ColumnKey(
        int Table,
        string Column
    );
}
