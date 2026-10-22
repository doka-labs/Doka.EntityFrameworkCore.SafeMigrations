namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Selects connection-free transition contracts only after runtime schema identity is established.</summary>
internal sealed class PostgreSqlSafeMigrationGenerationIdentityContract
{
    private readonly Segment?[] _segments;

    private PostgreSqlSafeMigrationGenerationIdentityContract(Segment?[] segments) => _segments = segments;

    /// <summary>Partitions transition evidence at ordinary provider operations which can change its identity.</summary>
    /// <param name="operations">The complete ordered generation stream.</param>
    /// <returns>Invocation-local, indexed contracts without retaining generated SQL plans.</returns>
    public static PostgreSqlSafeMigrationGenerationIdentityContract Create(IReadOnlyList<MigrationOperation> operations)
    {
        var segments = new Segment?[operations.Count];
        var pending = new List<(int Ordinal, SafeMigrationOperation Operation)>();
        // WHY: Retain original operation ordinals when forming identity segments.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (operation is SafeMigrationOperation safeOperation)
            {
                if (safeOperation.Intent is EnsureSchemaIntent or DropSchemaIntent)
                {
                    Flush(pending, segments);
                    pending.Add((ordinal, safeOperation));
                    Flush(pending, segments);

                    continue;
                }

                pending.Add((ordinal, safeOperation));

                continue;
            }

            if (operation is SafeMigrationDesignTimeServicesRequiredOperation
                || PostgreSqlSafeMigrationProviderAnalyzer.ProviderOperationPreservesTableState(operation))
            {
                continue;
            }

            Flush(pending, segments);
        }

        Flush(pending, segments);

        return new PostgreSqlSafeMigrationGenerationIdentityContract(segments);
    }

    /// <summary>Builds the same physical transition contract under the server's fresh schema decision.</summary>
    /// <param name="builder">The provider builder carrying literal and identifier semantics.</param>
    /// <param name="operation">The immutable safe operation being generated.</param>
    /// <param name="ordinal">The operation's original position, including ordinary provider barriers.</param>
    /// <returns>A guarded plan without assuming that an authored qualifier is current.</returns>
    public PostgreSqlSafeMigrationRuntimePlan Build(
        PostgreSqlSafeMigrationCatalogSqlBuilder builder,
        SafeMigrationOperation operation,
        int ordinal
    )
    {
        var segment = _segments[ordinal]
            ?? throw new InvalidOperationException("The PostgreSQL generation contract has no safe operation owner.");

        var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(operation.Intent);
        var family = owner.Table is null ? null : segment.FindFamily(owner.Table);
        var exact = operation.Intent is EnsureTableIntent table
            ? segment.Exact.GetValueOrDefault((table.Definition.Schema, table.Definition.Table)) : null;

        if (family is not { HasUnqualifiedOwner: true } || family.QualifiedSchemas.Count == 0)
        {
            return builder.Build(operation, exact, includeAnalysisEvidence: false, includeTransitionEvidence: true);
        }

        var alternatives = new List<(string Schema, SafeMigrationExpectedTableConstraints? Contract)>();
        var rejected = new List<string>();
        var guards = new List<string>();
        var postExecutionGuards = new List<string>();
        if (family is { HasUnqualifiedOwner: true })
        {
            foreach (var schema in family.QualifiedSchemas.Order(StringComparer.Ordinal))
            {
                if (owner.Schema is not null && !StringComparer.Ordinal.Equals(owner.Schema, schema))
                {
                    continue;
                }

                var candidate = family.Resolve(schema);
                if (candidate is null)
                {
                    rejected.Add(schema);

                    continue;
                }

                if (operation.Intent is EnsureTableIntent ensure)
                {
                    alternatives.Add((schema,
                        candidate.GetValueOrDefault((ensure.Definition.Schema, ensure.Definition.Table))));
                }

                var familyGuard = family.BuildIdentityGuard(builder, operation.Intent, schema);
                if (familyGuard != "TRUE")
                {
                    guards.Add(builder.BuildConditionalAliasGuard(schema, familyGuard));
                }

                var postExecutionGuard = family.BuildPostExecutionIdentityGuard(builder, operation.Intent, schema);
                if (postExecutionGuard != "TRUE")
                {
                    postExecutionGuards.Add(builder.BuildConditionalAliasGuard(schema, postExecutionGuard));
                }
            }
        }

        var plan = operation.Intent is EnsureTableIntent
            ? builder.BuildWithTableIdentityContracts(operation, exact, alternatives)
            : builder.Build(operation, includeAnalysisEvidence: false, includeTransitionEvidence: true);

        var rejectedGuard = builder.BuildRejectedAliasGuard(rejected);
        if (rejectedGuard != "TRUE")
        {
            guards.Add(rejectedGuard);
            postExecutionGuards.Add(rejectedGuard);
        }

        if (guards.Count == 0)
        {
            return plan;
        }

        if (plan.ObjectIdentityGuardExpression != "TRUE")
        {
            guards.Add(plan.ObjectIdentityGuardExpression);
        }

        if (plan.PostExecutionObjectIdentityGuardExpression != "TRUE")
        {
            postExecutionGuards.Add(plan.PostExecutionObjectIdentityGuardExpression);
        }

        return plan with
        {
            ObjectIdentityGuardExpression = "(" + string.Join(" AND ", guards) + ")",
            PostExecutionObjectIdentityGuardExpression = postExecutionGuards.Count == 0
                ? "TRUE" : "(" + string.Join(" AND ", postExecutionGuards) + ")",
            ObjectIdentityGuardFailureCodeExpression = rejected.Count == 0
                ? plan.ObjectIdentityGuardFailureCodeExpression
                : "CASE WHEN NOT COALESCE((" + rejectedGuard + "), FALSE) "
                    + "THEN 'schema_alias_transition_invalid' ELSE 'schema_search_path_mismatch' END",
        };
    }

    /// <summary>Publishes one immutable segment shared by its operation ordinals.</summary>
    private static void Flush(
        List<(int Ordinal, SafeMigrationOperation Operation)> pending,
        Segment?[] segments
    )
    {
        if (pending.Count == 0)
        {
            return;
        }

        var segment = new Segment(pending.Select(static entry => entry.Operation).ToArray());
        foreach (var entry in pending)
        {
            segments[entry.Ordinal] = segment;
        }

        pending.Clear();
    }

    /// <summary>Indexes the connected table families instead of rebuilding the complete stream per qualifier.</summary>
    private sealed class Segment
    {
        private readonly Dictionary<string, Family> _families = new(StringComparer.Ordinal);

        public Segment(SafeMigrationOperation[] operations)
        {
            SafeMigrationExpectedIndexTransitions.Validate(operations);
            Exact = SafeMigrationExpectedTableConstraints.FromOperations(operations, static schema => schema,
                StringComparer.Ordinal, '"', foldUnquotedCapturedSqlIdentifiers: true);
            foreach (var operation in operations)
            {
                var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(operation.Intent);
                if (owner.Table is null)
                {
                    continue;
                }

                _ = GetFamily(owner.Table);
                if (operation.Intent is RenameTableIntent rename)
                {
                    Join(owner.Table, rename.NewName ?? rename.Name);
                }

                if (operation.Intent is EnsureForeignKeyIntent foreignKey)
                {
                    Join(owner.Table, foreignKey.Definition.PrincipalTable);
                }

                if (operation.Intent is EnsureTableIntent table)
                {
                    foreach (var foreignKeyDefinition in table.Definition.ForeignKeys)
                    {
                        Join(owner.Table, foreignKeyDefinition.PrincipalTable);
                    }
                }
            }

            foreach (var operation in operations)
            {
                var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(operation.Intent);
                if (owner.Table is null)
                {
                    // WHY: DROP SCHEMA participates in index-lifetime validation.
                    // It is shared only by the bounded connected families which
                    // already own relevant transition evidence in this segment.
                    if (operation.Intent is DropSchemaIntent)
                    {
                        foreach (var candidateFamily in _families.Values.Distinct())
                        {
                            candidateFamily.Operations.Add(operation);
                        }
                    }

                    continue;
                }

                var family = GetFamily(owner.Table);
                family.Operations.Add(operation);
                PostgreSqlSafeMigrationSchemaIdentity.CollectUnqualifiedTables(operation.Intent,
                    family.UnqualifiedTables);
                family.HasUnqualifiedOwner |= owner.Schema is null;
                if (owner.Schema is not null)
                {
                    family.QualifiedSchemas.Add(owner.Schema);
                }

                if (operation.Intent is RenameTableIntent { NewSchema: { } newSchema })
                {
                    family.QualifiedSchemas.Add(newSchema);
                }
            }
        }

        public IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints> Exact
        {
            get;
        }

        public Family FindFamily(string table) => _families[table];

        private Family GetFamily(string table)
        {
            if (_families.TryGetValue(table, out var family))
            {
                return family;
            }

            family = new Family();
            family.Tables.Add(table);
            _families.Add(table, family);

            return family;
        }

        private void Join(string source, string target)
        {
            var left = GetFamily(source);
            var right = GetFamily(target);
            if (ReferenceEquals(left, right))
            {
                return;
            }

            // WHY: Union by size bounds remapping while collecting FK/rename
            // components. Alias replay then consumes only this connected
            // family, never all operations multiplied by all stream schemas.
            if (left.Tables.Count < right.Tables.Count)
            {
                (left, right) = (right, left);
            }

            foreach (var table in right.Tables)
            {
                left.Tables.Add(table);
                _families[table] = left;
            }
        }
    }

    /// <summary>Keeps authored definitions and lazily shared qualifier alternatives for one table component.</summary>
    private sealed class Family
    {
        private readonly Dictionary<string,
            IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>?> _resolved
            = new(StringComparer.Ordinal);

        private string? _identityGuard;

        public HashSet<string> Tables { get; } = new(StringComparer.Ordinal);
        public HashSet<string> QualifiedSchemas { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnqualifiedTables { get; } = new(StringComparer.Ordinal);
        public List<SafeMigrationOperation> Operations { get; } = [];
        public bool HasUnqualifiedOwner { get; set; }

        public IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>?
            Resolve(string schema)
        {
            if (_resolved.TryGetValue(schema, out var result))
            {
                return result;
            }

            string? Normalize(string? value) => PostgreSqlSafeMigrationSchemaIdentity.Normalize(value, schema);

            try
            {
                SafeMigrationExpectedIndexTransitions.Validate(Operations, Normalize);
                result = SafeMigrationExpectedTableConstraints.FromOperations(Operations, Normalize,
                    StringComparer.Ordinal, '"', foldUnquotedCapturedSqlIdentifiers: true);
            }
            catch (InvalidOperationException)
            {
                // WHY: A cross-qualifier transition can be invalid only when
                // the candidate really is current. Keep foreign schemas legal
                // and reject this exact candidate before runtime mutation.
                result = null;
            }

            _resolved.Add(schema, result);

            return result;
        }

        public string BuildIdentityGuard(
            PostgreSqlSafeMigrationCatalogSqlBuilder builder,
            SafeMigrationIntent intent,
            string schema
        )
        {
            var optionalAbsentBinding = PostgreSqlSafeMigrationSchemaIdentity.GetOptionalAbsentNativeBinding(
                intent, schema);

            if (optionalAbsentBinding is not null)
            {
                return builder.BuildObjectIdentityGuard(UnqualifiedTables, optionalAbsentBinding);
            }

            // WHY: The generator uses one stable literal/identifier builder.
            // Repeated operations in a connected family share this bounded
            // catalog-only guard, not a retained runtime plan or row proof.

            return _identityGuard ??= builder.BuildObjectIdentityGuard(UnqualifiedTables);
        }

        /// <summary>Rechecks connected bindings without treating consumed names as surviving owners.</summary>
        public string BuildPostExecutionIdentityGuard(
            PostgreSqlSafeMigrationCatalogSqlBuilder builder,
            SafeMigrationIntent intent,
            string schema
        )
        {
            var bindings = new HashSet<string>(UnqualifiedTables, StringComparer.Ordinal);
            PostgreSqlSafeMigrationSchemaIdentity.AdvancePostExecutionBindings(intent, bindings, schema);

            return builder.BuildObjectIdentityGuard(bindings);
        }
    }
}
