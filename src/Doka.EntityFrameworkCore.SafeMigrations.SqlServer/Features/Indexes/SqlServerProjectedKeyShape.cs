namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Captures only physical column facets needed by ordered key validation.</summary>
/// <param name="StoreType">The live base store type.</param>
/// <param name="DeclaredBytes">The maximum stored width in bytes.</param>
/// <param name="IsNullable">Whether the live column accepts NULL.</param>
/// <param name="KeyEligible">Whether the live physical shape supports a key.</param>
/// <param name="IncludeEligible">Whether the live physical shape supports an included column.</param>
/// <param name="Collation">The live collation, if the type has one.</param>
/// <param name="UsesDefaultCollation">Whether the live collation equals the database default.</param>
internal sealed record SqlServerProjectedKeyColumn(
    string StoreType,
    int DeclaredBytes,
    bool IsNullable,
    bool KeyEligible,
    bool IncludeEligible,
    string? Collation,
    bool UsesDefaultCollation
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
        SafeMigrationSqlExpression? normalizedFilter = null
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

        if (live.IsInvariantUnsupported && !qualifiedFilterReplacement)
        {
            return live;
        }

        if (snapshot is { PhysicalSupported: false, })
        {
            return Unsupported("physical_table_unproven");
        }

        if (live.IsOpaqueProjectionUnknown || projected.IsOpaqueProjectionUnknown
            || projected.ObservedState is SafeMigrationObservedState.Different
                or SafeMigrationObservedState.DataBlocked)
        {
            return projected;
        }

        var request = Request.From(intent, normalizedFilter);
        var tableState = default(SafeMigrationProjectedTableState);
        var hasTableProof = source is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(request.Table, request.Schema, out tableState);

        if (tableState.HasUnknownStructure
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
        for (var ordinal = 0; ordinal < request.ColumnCount; ordinal++)
        {
            var name = request.Column(ordinal);
            SqlServerProjectedKeyColumn? original = null;
            _ = snapshot?.Columns.TryGetValue(name, out original);
            var hasProjected = source.TryGetProjectedColumn(request.Table, request.Schema, name, out var definition);
            var physical = hasProjected ? Resolve(definition!, mappingSource) : original;
            if (physical is null)
            {
                return Unknown("projected_key_column_unknown");
            }

            if (!physical.KeyEligible || physical.DeclaredBytes <= 0
                || physical.DeclaredBytes > request.MaximumBytes - bytes)
            {
                return Unsupported("key_unproven_width");
            }

            if (request.Primary && physical.IsNullable)
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
                    && !definition.IsRowVersion
                    && definition.ComputedColumnSql is null
                    && definition.ComputedExpression is null)
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
                ? Resolve(definition, mappingSource)
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

        if (filterFailure is not null && !qualifiedFilterReplacement)
        {
            return filterFailure;
        }

        if (request.Primary && snapshot?.PrimaryKeyName is not null
            && !tableState.IsNewlyCreated
            && !tableState.PrimaryKeyWasDropped
            && projected.ObservedState == SafeMigrationObservedState.Missing)
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None,
                postconditionSatisfied: false, "primary_key_already_exists");
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
            if (tableState.IsNewlyCreated && seedProof is not null)
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
            or "unique_constraint_replacement_data_blocked" or "index_replacement_data_blocked")
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.DataBlocked, SafeMigrationRepairCapability.None,
                postconditionSatisfied: false, live.Code);
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
                    SafeMigrationObservedState.DataBlocked, SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false, "projected_unique_null_duplicates")
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
        IRelationalTypeMappingSource mappingSource
    )
    {
        var mapping = mappingSource.FindMapping(
            definition.ClrType, definition.StoreType, unicode: definition.IsUnicode,
            size: definition.MaxLength, rowVersion: definition.IsRowVersion,
            fixedLength: definition.IsFixedLength, precision: definition.Precision, scale: definition.Scale);

        if (mapping is null || definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
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
                <= 9 => 5,
                <= 19 => 9,
                <= 28 => 13,
                _ => 17,
            },
            "uniqueidentifier" => 16,
            _ => -1,
        };

        return new SqlServerProjectedKeyColumn(type, length, definition.IsNullable,
            length > 0, type is not ("text" or "ntext" or "image"),
            definition.Collation?.Name, definition.Collation is null);
    }

    private static int TemporalBytes(int scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    private static bool ValuesArePreserved(
        SqlServerProjectedKeyColumn original,
        SqlServerProjectedKeyColumn target,
        ExpectedColumnDefinition definition
    )
        => StringComparer.Ordinal.Equals(original.StoreType, target.StoreType)
            && target.DeclaredBytes >= original.DeclaredBytes
            && (!original.IsNullable || target.IsNullable)
            && (original.Collation is null
                || definition.Collation is null && original.UsesDefaultCollation
                || StringComparer.Ordinal.Equals(original.Collation, definition.Collation?.Name));

    private static bool RejectsAddedNull(
        SafeMigrationSqlExpression? filter,
        HashSet<string> addedColumns
    )
        => filter switch
        {
            SafeMigrationSqlNullTestExpression
            {
                Negated: true, Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1, } identifier,
            } => addedColumns.Contains(identifier.Parts[0]),
            SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And, } binary
                => RejectsAddedNull(binary.Left, addedColumns) || RejectsAddedNull(binary.Right, addedColumns),
            _ => false,
        };

    private static SafeMigrationProviderAnalysis Actionable(SafeMigrationProviderAnalysis analysis)
        => analysis.ObservedState is SafeMigrationObservedState.PrerequisiteMissing
            or SafeMigrationObservedState.Unsupported
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, postconditionSatisfied: false, "projected_missing")
            : analysis;

    private static SafeMigrationProviderAnalysis Unknown(string code)
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None,
            postconditionSatisfied: false, code);

    private static SafeMigrationProviderAnalysis Unsupported(string code)
        => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None,
            postconditionSatisfied: false, code);

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

        public string Column(int ordinal) => Columns is null ? IndexKeys![ordinal].Column! : Columns[ordinal];

        public static Request From(
            SafeMigrationIntent intent,
            SafeMigrationSqlExpression? normalizedFilter
        )
            => intent switch
            {
                EnsurePrimaryKeyIntent value => new(value.Definition.Table,
                    value.Definition.Schema, value.Definition.Columns, null, [], true, true, null),
                EnsureUniqueConstraintIntent value => new(value.Definition.Table,
                    value.Definition.Schema, value.Definition.Columns, null, [], false, true, null),
                EnsureIndexIntent value => new(value.Definition.Table,
                    value.Definition.Schema, null, value.Definition.Keys,
                    value.Definition.IncludedColumns, false,
                    value.Definition.Unique, normalizedFilter ?? value.Definition.StructuredFilter),
                _ => throw new ArgumentException("Only physical key operations can be projected.", nameof(intent)),
            };
    }
}
