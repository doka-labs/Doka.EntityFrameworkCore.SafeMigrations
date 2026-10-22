namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Snapshots bounded model candidates without treating the future model as native evidence.</summary>
internal static class MySqlGeneratedColumnModelCandidates
{
    /// <summary>Binds complete untrusted model snapshots to exact operations without retaining model objects.</summary>
    /// <param name="operations">The full ordered stream, including ordinary-operation barriers.</param>
    /// <param name="targetModels">Exact historical targets, or null for the explicitly supplied default model.</param>
    /// <param name="defaultModel">The manual generation model; never a substituted current design-time model.</param>
    /// <param name="runtimeInitializer">Initializes each exact historical target once without replacing it.</param>
    /// <returns>Whole-model candidate sets reused by exact operation identity within this invocation.</returns>
    internal static IReadOnlyDictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>> CaptureTargets(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        IModel? defaultModel,
        IModelRuntimeInitializer? runtimeInitializer = null
    )
    {
        if (targetModels is not null
            && targetModels.Count != operations.Count)
        {
            throw new ArgumentException("The target-model list must match the operation stream.", nameof(targetModels));
        }

        Dictionary<IModel, List<MigrationOperation>>? requests = null;
        // WHY: Keep captured proofs and plan slots tied to the exact owning operation ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index] is not SafeMigrationOperation { Intent: DropColumnIntent or RenameColumnIntent }
                || (targetModels is null ? defaultModel : targetModels[index]) is not { } model
                || model is RuntimeModel)
            {
                continue;
            }

            requests ??= new(ReferenceEqualityComparer.Instance);
            if (!requests.TryGetValue(model, out var modelOperations))
            {
                modelOperations = [];
                requests.Add(model, modelOperations);
            }

            modelOperations.Add(operations[index]);
        }

        var result = new Dictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>>(
            ReferenceEqualityComparer.Instance);

        if (requests is null)
        {
            return result;
        }

        // WHY: Thousands of operations can share one historical target. Read
        // its relevant relational owners once; do not rescan or retain IModel
        // in the later runtime catalogue, and never union different targets.
        foreach (var request in requests)
        {
            // WHY: Migration.TargetModel is not yet initialized during pending
            // analysis. Preserve that exact historical design-time model and
            // initialize it once per reference before reading relational facets.
            var model = runtimeInitializer?.Initialize(request.Key, designTime: true) ?? request.Key;
            var candidates = Capture(model, request.Value);
            if (candidates.Count == 0)
            {
                continue;
            }

            foreach (var operation in request.Value)
            {
                result.TryAdd((SafeMigrationOperation)operation, candidates);
            }
        }

        return result;
    }

    /// <summary>Captures generated members only for owners with a dependency-sensitive operation.</summary>
    /// <param name="model">The optional model accompanying this invocation.</param>
    /// <param name="operations">The complete immutable operation stream.</param>
    /// <returns>Untrusted generated-member candidates, qualified later against the complete native catalog.</returns>
    internal static IReadOnlyList<ExpectedTableDefinition> Capture(
        IModel? model,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        if (model is null or RuntimeModel
            || !operations.Any(static operation =>
                operation is SafeMigrationOperation { Intent: DropColumnIntent or RenameColumnIntent }))
        {
            return [];
        }

        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (operation is SafeMigrationOperation { Intent: DropColumnIntent drop })
            {
                requested.Add(drop.Table);
            }
            else if (operation is SafeMigrationOperation { Intent: RenameColumnIntent rename })
            {
                requested.Add(rename.Table);
            }
        }

        List<ExpectedTableDefinition>? candidates = null;
        foreach (var table in model.GetRelationalModel()
                     .Tables)
        {
            if (!requested.Contains(table.Name))
            {
                continue;
            }

            List<ExpectedColumnDefinition>? columns = null;
            var hasUnknownFacets = false;
            foreach (var column in table.Columns)
            {
                if (column.ComputedColumnSql is not { } sql)
                {
                    continue;
                }

                if (column.PropertyMappings.Count == 0
                    || column.PropertyMappings[0].Property is RuntimeProperty)
                {
                    // WHY: EF deliberately omits comment/collation from its
                    // runtime property metadata. Absence is unknown, not the
                    // known-null value required by a complete native proof.
                    hasUnknownFacets = true;

                    break;
                }

                // WHY: Parsing records dependency roles, not equivalence with
                // the server. The original SQL and every generated facet must
                // still pass the fresh complete-set certificate before use.
                var operation = new AddColumnOperation
                {
                    Name = column.Name,
                    ClrType = column.ProviderClrType,
                    ColumnType = column.StoreType,
                    IsNullable = column.IsNullable,
                    IsUnicode = column.IsUnicode,
                    MaxLength = column.MaxLength,
                    IsFixedLength = column.IsFixedLength,
                    IsRowVersion = column.IsRowVersion,
                    Precision = column.Precision,
                    Scale = column.Scale,
                    Collation = column.Collation,
                    Comment = column.Comment,
                    ComputedColumnSql = sql,
                    IsStored = column.IsStored,
                };

                (columns ??= []).Add(SafeMigrationExpectedDefinitionFactory.From(operation));
            }

            if (!hasUnknownFacets
                && columns is not null)
            {
                (candidates ??= []).Add(new ExpectedTableDefinition(table.Name, columns, table.Schema));
            }
        }

        return candidates ?? (IReadOnlyList<ExpectedTableDefinition>)[];
    }
}
