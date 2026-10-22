namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Captures only physical column facets needed by ordered key validation.</summary>
/// <param name="StoreType">The live base store type.</param>
/// <param name="DeclaredBytes">The maximum stored width in bytes.</param>
/// <param name="IsNullable">Whether the live column accepts NULL.</param>
/// <param name="KeyEligible">Whether the live physical shape supports a key.</param>
/// <param name="IncludeEligible">Whether the live physical shape supports an included column.</param>
/// <param name="Collation">The live collation, if the type has one.</param>
/// <param name="UsesDefaultCollation">Whether the live collation equals the database default.</param>
/// <param name="CheckEligible">Whether native integer CHECK validation supports every live operand facet.</param>
internal sealed record SqlServerProjectedKeyColumn(
    string StoreType,
    int DeclaredBytes,
    bool IsNullable,
    bool KeyEligible,
    bool IncludeEligible,
    string? Collation,
    bool UsesDefaultCollation,
    bool CheckEligible = false
);

/// <summary>Contains one bounded immutable live-table proof for projected keys.</summary>
/// <param name="Exists">Whether the table existed before the operation stream.</param>
/// <param name="RowCount">The exact row count capped at two, or minus one when no row proof was requested.</param>
/// <param name="PrimaryKeyName">The existing physical primary key, if any.</param>
/// <param name="Columns">The requested physical column shapes.</param>
/// <param name="PhysicalSupported">Whether the original table uses the ordinary supported engine contract.</param>
internal sealed record SqlServerProjectedKeyTable(
    bool Exists,
    int RowCount,
    string? PrimaryKeyName,
    IReadOnlyDictionary<string, SqlServerProjectedKeyColumn> Columns,
    bool PhysicalSupported = true
);

/// <summary>Retains a native computed-index proof together with its immutable operand bindings.</summary>
/// <param name="Shape">The result shape proven by the current native compiler and SET contract.</param>
/// <param name="Operands">Every referenced name and its authored binding, or null for unchanged live metadata.</param>
/// <param name="Table">The exact qualified owner captured for this proof.</param>
/// <param name="Schema">The captured owner schema, with null denoting dbo.</param>
/// <param name="ColumnSupported">Whether native result facets support the column independently of indexing.</param>
internal sealed record SqlServerProjectedComputedKeyProof(
    SqlServerProjectedKeyColumn Shape,
    IReadOnlyDictionary<string, ExpectedColumnDefinition?> Operands,
    string Table,
    string? Schema = null,
    bool ColumnSupported = true
)
{
    /// <summary>Gets exact original native identities only for operands absent from the authored stream.</summary>
    internal IReadOnlyDictionary<string, int>? LiveOperandIdentities { get; init; }

    /// <summary>Gets the invocation-owned bindings invalidated by accepted column operations.</summary>
    internal IReadOnlyDictionary<string, int>? CurrentColumnBindings { get; init; }

    /// <summary>Gets only immutable authored copies created by an explicit earlier source rename.</summary>
    internal IReadOnlySet<ExpectedColumnDefinition>? RenamedOperandDefinitions { get; init; }

    /// <summary>Rejects reuse after any accepted operand replacement or unknown owner recreation.</summary>
    internal bool Matches(
        ISafeMigrationProjectedColumnSource source,
        string table,
        string? schema
    )
    {
        // WHY: The same immutable definition can be reused by several owners.
        // A source id is table-local; another owner's precision or collation
        // certificate cannot qualify this one merely because its ids match.
        if (!StringComparer.Ordinal.Equals(Table, table)
            || !StringComparer.Ordinal.Equals(Schema ?? "dbo", schema ?? "dbo"))
        {
            return false;
        }

        var state = default(SafeMigrationProjectedTableState);
        var hasTableState = source is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(table, schema, out state);
        var recreated = hasTableState && state.IsNewlyCreated;

        foreach (var (name, binding) in Operands)
        {
            if (source.TryGetProjectedColumn(table, schema, name, out var projected))
            {
                if (!ReferenceEquals(projected, binding)
                    && (binding is null
                        || RenamedOperandDefinitions?.Contains(binding) != true
                        || !SafeMigrationDefinitionEquivalence.Column(projected, binding)))
                {
                    return false;
                }
            }
            else if (binding is not null || recreated)
            {
                return false;
            }
            else if (LiveOperandIdentities is not null)
            {
                if (!LiveOperandIdentities.TryGetValue(name, out var original)
                    || original <= 0
                    || CurrentColumnBindings?.TryGetValue(name, out var current) != true
                    || current != original)
                {
                    return false;
                }
            }
            else if (hasTableState && state.HasUnknownStructure)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Qualifies Core's ordered key projection with SQL Server physical and row semantics.</summary>
internal static class SqlServerProjectedKeyShape
{
    /// <summary>Validates one projected primary key, unique constraint, or index.</summary>
    /// <param name="intent">The projected key operation.</param>
    /// <param name="source">The accepted ordered column and table proofs.</param>
    /// <param name="live">The immutable original catalog result.</param>
    /// <param name="projected">The Core-projected result.</param>
    /// <param name="snapshot">The bounded original live-table proof.</param>
    /// <param name="mappingSource">The active SQL Server type mapping source.</param>
    /// <param name="seedProof">A provider-proven, exactly bound authored insert lineage, when available.</param>
    /// <param name="filterFailure">An invalid or unresolved provider-qualified filter contract, when present.</param>
    /// <param name="normalizedFilter">The predicate with the actual emitted raw-SQL literal types preserved.</param>
    /// <param name="computedColumns">Invocation-owned native proofs tied to exact authored operand bindings.</param>
    /// <param name="structuralIdentityProven">
    /// Whether a narrow native source proof replaced generic uncertainty.
    /// </param>
    /// <returns>The provider-qualified analysis without weakening unknown-state boundaries.</returns>
    internal static SafeMigrationProviderAnalysis Validate(
        SafeMigrationIntent intent,
        ISafeMigrationProjectedColumnSource source,
        SafeMigrationProviderAnalysis live,
        SafeMigrationProviderAnalysis projected,
        SqlServerProjectedKeyTable? snapshot,
        IRelationalTypeMappingSource mappingSource,
        SafeMigrationProviderAnalysis? seedProof = null,
        SafeMigrationProviderAnalysis? filterFailure = null,
        SafeMigrationSqlExpression? normalizedFilter = null,
        IReadOnlyDictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>>? computedColumns = null,
        bool structuralIdentityProven = false
    )
    {
        // WHY: Only an exact accepted predicate-column replacement can make
        // the original filter rejection obsolete. Other invariant failures,
        // opaque boundaries, physical limits, and row proofs remain mandatory.
        var qualifiedFilterReplacement = live.Code == SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode
            && filterFailure is
            {
                ObservedState: SafeMigrationObservedState.Missing,
                Code: SqlServerProjectedIndexFilterProof.SupportedReplacementCode,
            };

        if (live.IsInvariantUnsupported
            && !qualifiedFilterReplacement)
        {
            return live;
        }

        if (snapshot is { PhysicalSupported: false, })
        {
            return Unsupported("physical_table_unproven");
        }

        if (live.IsOpaqueProjectionUnknown
            || projected.IsOpaqueProjectionUnknown
            || projected.ObservedState is SafeMigrationObservedState.Different
                or SafeMigrationObservedState.DataBlocked)
        {
            return projected;
        }

        var request = Request.From(intent, normalizedFilter);
        var tableState = default(SafeMigrationProjectedTableState);
        var hasTableProof = source is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(request.Table, request.Schema, out tableState);

        if ((!structuralIdentityProven && tableState.HasUnknownStructure)
            || projected.Code is "projected_structure_state_unknown" or "projected_data_state_unknown")
        {
            return projected;
        }

        if (request.ColumnCount > 32)
        {
            return Unsupported("key_too_many_columns");
        }

        var bytes = 0;
        HashSet<string>? addedNullColumns = null;
        var valuesPreserved = true;

        // WHY: Read key members by the request's shared positional protocol.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < request.ColumnCount; ordinal++)
        {
            var name = request.Column(ordinal);
            SqlServerProjectedKeyColumn? original = null;
            _ = snapshot?.Columns.TryGetValue(name, out original);
            var hasProjected = source.TryGetProjectedColumn(request.Table, request.Schema, name, out var definition);
            var physical = hasProjected
                ? ResolveProjected(definition!, mappingSource, computedColumns, source, request.Table, request.Schema)
                : original;
            if (physical is null)
            {
                return Unknown("projected_key_column_unknown");
            }

            if (!physical.KeyEligible
                || physical.DeclaredBytes <= 0
                || physical.DeclaredBytes > request.MaximumBytes - bytes)
            {
                return Unsupported("key_unproven_width");
            }

            if (request.Primary
                && physical.IsNullable)
            {
                // WHY: SQL Server rejects nullable PK columns even when the
                // current table is empty or a row scan finds no NULL values.
                return Unsupported("primary_key_nullable_column");
            }

            bytes += physical.DeclaredBytes;
            if (hasProjected && original is null)
            {
                valuesPreserved = false;
                if (definition!.IsNullable
                    && definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
                    && definition is { IsRowVersion: false, ComputedColumnSql: null, ComputedExpression: null })
                {
                    addedNullColumns ??= new HashSet<string>(StringComparer.Ordinal);
                    addedNullColumns.Add(name);
                }
            }
            else if (hasProjected && original is not null)
            {
                valuesPreserved &= ValuesArePreserved(original, physical, definition!);
            }
        }

        foreach (var name in request.IncludedColumns)
        {
            SqlServerProjectedKeyColumn? original = null;
            _ = snapshot?.Columns.TryGetValue(name, out original);
            var physical = source.TryGetProjectedColumn(request.Table, request.Schema, name, out var definition)
                ? ResolveProjected(definition, mappingSource, computedColumns, source, request.Table, request.Schema)
                : original;

            if (physical is null)
            {
                return Unknown("projected_index_include_unknown");
            }

            if (!physical.IncludeEligible)
            {
                return Unsupported("index_include_unproven_type");
            }
        }

        if (filterFailure is not null
            && !qualifiedFilterReplacement)
        {
            return filterFailure;
        }

        if (request.Primary
            && snapshot?.PrimaryKeyName is not null
            && tableState is { IsNewlyCreated: false, PrimaryKeyWasDropped: false }
            && projected.ObservedState == SafeMigrationObservedState.Missing)
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                "primary_key_already_exists");
        }

        if (!request.Unique)
        {
            return Actionable(projected);
        }

        if (projected.Code == "projected_primary_key_replacement_unproven"
            && !tableState.PrimaryKeyWasDropped)
        {
            return projected;
        }

        if (tableState.HasDataMutation)
        {
            if (tableState.IsNewlyCreated
                && seedProof is not null)
            {
                return seedProof;
            }

            // WHY: The bounded original row count is not a proof after an
            // earlier unanalysed insert, update, delete, or opaque SQL step.

            return Unknown("projected_key_data_state_unknown");
        }

        var emptyNewTable = hasTableProof && tableState.IsNewlyCreated;
        if (emptyNewTable)
        {
            // WHY: Accepted table recreation discards both the original key
            // and its original row evidence. Only the ordered empty-table
            // proof, not a stale duplicate classification, remains relevant.
            return Actionable(projected);
        }

        if (live.Code is "primary_key_replacement_data_blocked"
            or "unique_constraint_replacement_data_blocked"
            or "index_replacement_data_blocked")
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                live.Code);
        }

        if (snapshot is { Exists: true, RowCount: >= 0 and <= 1, })
        {
            return Actionable(projected);
        }

        if (addedNullColumns is { Count: > 0, })
        {
            if (RejectsAddedNull(request.Filter, addedNullColumns))
            {
                return Actionable(projected);
            }

            // WHY: Unlike MySQL and PostgreSQL's default unique semantics,
            // SQL Server treats NULL tuples as equal. A new nullable column
            // is therefore not a blanket uniqueness proof for existing rows.

            return request.ColumnCount == 1 && snapshot is { Exists: true, RowCount: 2, }
                ? new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.DataBlocked,
                    SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false,
                    "projected_unique_null_duplicates")
                : Unknown("projected_key_data_state_unknown");
        }

        if (valuesPreserved
            && (live.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching
                || live.Code == "key_replacement_data_safe"))
        {
            return Actionable(projected);
        }

        return Unknown("projected_key_data_state_unknown");
    }

    private static SqlServerProjectedKeyColumn? Resolve(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappingSource,
        bool nativeComputedProof = false
    )
    {
        if (SqlServerComputedColumnContract.IsComputed(definition)
            && !nativeComputedProof)
        {
            return null;
        }

        var mapping = mappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            unicode: definition.IsUnicode,
            size: definition.MaxLength,
            rowVersion: definition.IsRowVersion,
            fixedLength: definition.IsFixedLength,
            precision: definition.Precision,
            scale: definition.Scale);

        if (mapping is null)
        {
            return null;
        }

        var type = mapping.StoreTypeNameBase.ToLowerInvariant();
        var length = type switch
        {
            "binary" or "char" or "varbinary" or "varchar" => mapping.Size ?? -1,
            "nchar" or "nvarchar" => mapping.Size is > 0 and <= 4000 ? mapping.Size.Value * 2 : -1,
            "bigint" or "datetime" or "money" or "timestamp" or "rowversion" => 8,
            "datetime2" => TemporalBytes(mapping.Precision ?? 7) + 3,
            "datetimeoffset" => TemporalBytes(mapping.Precision ?? 7) + 5,
            "date" => 3,
            "bit" or "tinyint" => 1,
            "int" or "real" or "smalldatetime" or "smallmoney" => 4,
            "smallint" => 2,
            "time" => TemporalBytes(mapping.Precision ?? 7),
            "float" => mapping.Precision is <= 24 ? 4 : 8,
            "decimal" or "numeric" => (mapping.Precision ?? 18) switch
            {
                <= 9 => 5, <= 19 => 9, <= 28 => 13, _ => 17,
            },
            "uniqueidentifier" => 16,
            _ => -1,
        };

        return new SqlServerProjectedKeyColumn(
            type,
            length,
            definition.IsNullable,
            length > 0,
            type is not ("text" or "ntext" or "image"),
            definition.Collation?.Name,
            definition.Collation is null);
    }

    private static SqlServerProjectedKeyColumn? ResolveProjected(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappingSource,
        IReadOnlyDictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>>? computedColumns,
        ISafeMigrationProjectedColumnSource source,
        string table,
        string? schema
    )
    {
        if (!SqlServerComputedColumnContract.IsComputed(definition))
        {
            return Resolve(definition, mappingSource);
        }

        return computedColumns is not null && computedColumns.TryGetValue(definition, out var candidates)
            ? candidates.FirstOrDefault(proof => proof.Matches(source, table, schema))
                ?.Shape
            : null;
    }

    /// <summary>Retains mapped width only after native compilation proves the exact authored result type.</summary>
    internal static SqlServerProjectedKeyColumn? CreateComputedColumnProof(
        ExpectedColumnDefinition definition,
        IRelationalTypeMappingSource mappingSource,
        int nativeProof
    )
    {
        var shape = Resolve(definition, mappingSource, nativeComputedProof: true);

        return shape is null
            ? null
            : shape with
            {
                IsNullable = nativeProof is 2 or 4 or 6,
                KeyEligible = nativeProof is 1 or 2 && shape.KeyEligible,
                IncludeEligible = nativeProof is 1 or 2 or 5 or 6 && shape.IncludeEligible,
            };
    }

    private static int TemporalBytes(
        int scale
    ) => scale <= 2
        ? 3
        : scale <= 4
            ? 4
            : 5;

    private static bool ValuesArePreserved(
        SqlServerProjectedKeyColumn original,
        SqlServerProjectedKeyColumn target,
        ExpectedColumnDefinition definition
    ) => StringComparer.Ordinal.Equals(original.StoreType, target.StoreType)
        && target.DeclaredBytes >= original.DeclaredBytes
        && (!original.IsNullable || target.IsNullable)
        && (original.Collation is null
            || (definition.Collation is null && original.UsesDefaultCollation)
            || StringComparer.Ordinal.Equals(original.Collation, definition.Collation?.Name));

    private static bool RejectsAddedNull(
        SafeMigrationSqlExpression? filter,
        HashSet<string> addedColumns
    )
    {
        var pending = default(SafeMigrationWorkStack<SafeMigrationSqlExpression>);
        try
        {
            do
            {
                if (filter is SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And } binary)
                {
                    pending.Push(binary.Right);
                    filter = binary.Left;
                    continue;
                }

                if (filter is SafeMigrationSqlNullTestExpression
                    {
                        Negated: true, Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } identifier
                    }
                    && addedColumns.Contains(identifier.Parts[0]))
                {
                    return true;
                }

                if (!pending.TryPop(out var next))
                {
                    return false;
                }

                filter = next;
            } while (true);
        }
        finally
        {
            pending.Dispose();
        }
    }

    private static SafeMigrationProviderAnalysis Actionable(
        SafeMigrationProviderAnalysis analysis
    ) => analysis.ObservedState is SafeMigrationObservedState.PrerequisiteMissing
        or SafeMigrationObservedState.Unsupported
        ? new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "projected_missing")
        : analysis;

    private static SafeMigrationProviderAnalysis Unknown(
        string code
    ) => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code);

    private static SafeMigrationProviderAnalysis Unsupported(
        string code
    ) => new(
        SafeMigrationObservedState.Unsupported,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code);

    private readonly record struct Request(
        string Table,
        string? Schema,
        IReadOnlyList<string>? Columns,
        IReadOnlyList<ExpectedIndexKeyDefinition>? IndexKeys,
        IReadOnlyList<string> IncludedColumns,
        bool Primary,
        bool Unique,
        SafeMigrationSqlExpression? Filter
    )
    {
        public int MaximumBytes => Primary ? 900 : 1700;

        public int ColumnCount => Columns?.Count ?? IndexKeys!.Count;

        public string Column(
            int ordinal
        ) => Columns is null ? IndexKeys![ordinal].Column! : Columns[ordinal];

        public static Request From(
            SafeMigrationIntent intent,
            SafeMigrationSqlExpression? normalizedFilter
        ) => intent switch
        {
            EnsurePrimaryKeyIntent value =>
                new Request(
                    value.Definition.Table,
                    value.Definition.Schema,
                    value.Definition.Columns,
                    null,
                    [],
                    true,
                    true,
                    null),
            EnsureUniqueConstraintIntent value => new Request(
                value.Definition.Table,
                value.Definition.Schema,
                value.Definition.Columns,
                null,
                [],
                false,
                true,
                null),
            EnsureIndexIntent value => new Request(
                value.Definition.Table,
                value.Definition.Schema,
                null,
                value.Definition.Keys,
                value.Definition.IncludedColumns,
                false,
                value.Definition.Unique,
                normalizedFilter ?? value.Definition.StructuredFilter),
            _ => throw new ArgumentException("Only physical key operations can be projected.", nameof(intent)),
        };
    }
}
