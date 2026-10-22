namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Builds one metadata-only classifier retaining the shared physical-engine boundary.</summary>
    /// <param name="ordinal">The original operation ordinal.</param>
    /// <param name="plan">The captured catalog contract.</param>
    /// <returns>A selection suitable for a bounded UNION ALL statement.</returns>
    internal static string BuildCatalogSelection(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan
    ) => BuildCatalogSelection(ordinal.ToString(CultureInfo.InvariantCulture), plan, includePhysicalTableSupport: true);

    private static string BuildCatalogSelection(
        string ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        bool includePhysicalTableSupport
    )
    {
        var guardedState = plan.StateEvaluationGuardFailureExpression is null
            ? plan.RenderStateExpression()
            : $"CASE WHEN COALESCE(({plan.StateEvaluationGuardExpression}), 0) = 1 "
            + $"THEN ({plan.RenderStateExpression()}) "
            + $"ELSE ({plan.StateEvaluationGuardFailureExpression}) END";

        var state = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
            + $"THEN ({guardedState}) ELSE N'prerequisite_missing' END";

        var post = plan.Postcondition;
        var repair = plan.RenderRepairPrecondition();
        var code = plan.RenderClassificationCodeExpression() ?? "CONVERT(nvarchar(128), NULL)";
        if (plan.PrerequisiteFailureCodeExpression is { } failureCode)
        {
            code = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 THEN ({code}) "
                + $"ELSE ({failureCode}) END";
        }

        var rows = plan.ModelManagedRowEvidenceExpression ?? "CONVERT(nvarchar(max), NULL)";
        var dependencies = plan.ModelManagedDependencyCountsExpression ?? "CONVERT(nvarchar(max), NULL)";
        var diagnostics = plan.DiagnosticEvidenceExpression ?? "CONVERT(nvarchar(max), NULL)";
        var matched = plan.MatchedObjectNameExpression ?? "CONVERT(nvarchar(128), NULL)";
        var physicalTableSupport = includePhysicalTableSupport ? plan.PhysicalTableSupportExpression : null;
        var defaultValueSupport = includePhysicalTableSupport ? plan.DefaultValueSupportExpression : null;
        var columnCollationSupport = includePhysicalTableSupport ? plan.ColumnCollationSupportExpression : null;
        var indexFilterSupport = includePhysicalTableSupport ? plan.IndexFilterSupportExpression : null;
        var columnLayoutFailure = includePhysicalTableSupport ? plan.ColumnLayoutFailureExpression : null;
        if (columnLayoutFailure is not null)
        {
            state = $"CASE WHEN doka_layout.failure IS NULL THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_layout.failure IS NULL THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_layout.failure IS NULL THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_layout.failure IS NULL THEN ({code}) ELSE doka_layout.failure END";
        }

        if (indexFilterSupport is not null)
        {
            state = $"CASE WHEN doka_filter.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_filter.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_filter.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_filter.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode}' END";
        }

        if (defaultValueSupport is not null)
        {
            state = $"CASE WHEN doka_default.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_default.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_default.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_default.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode}' END";
        }

        if (columnCollationSupport is not null)
        {
            state = $"CASE WHEN doka_collation.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_collation.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_collation.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_collation.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode}' END";
        }

        if (physicalTableSupport is not null)
        {
            // WHY: Pure catalog expressions need no delayed name binding.
            // Keep their bounded UNION ALL transport and share one engine
            // predicate across state, code, and proof results. Row-reading and
            // preamble classifiers use the outer IF boundary instead.
            state = $"CASE WHEN doka_physical.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_physical.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_physical.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_physical.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode}' END";
        }

        var supportSource = physicalTableSupport is null
            ? string.Empty
            : $"(SELECT COALESCE(({physicalTableSupport}), 0) AS supported) doka_physical";

        if (defaultValueSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({defaultValueSupport}), 0) AS supported) doka_default";
        }

        if (columnCollationSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({columnCollationSupport}), 0) AS supported) doka_collation";
        }

        if (indexFilterSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({indexFilterSupport}), 0) AS supported) doka_filter";
        }

        if (columnLayoutFailure is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT ({columnLayoutFailure}) AS failure) doka_layout";
        }

        return $"SELECT {ordinal}, ({state}), "
            + $"COALESCE(({post}), 0), COALESCE(({repair}), 0), ({code}), "
            + $"({rows}), ({dependencies}), ({diagnostics}), ({matched})"
            + (supportSource.Length == 0 ? string.Empty : " FROM " + supportSource);
    }

    /// <summary>Builds one delayed classifier within the complete statement payload bound.</summary>
    /// <param name="ordinal">The original operation ordinal.</param>
    /// <param name="plan">The already captured operation contract.</param>
    /// <param name="dispatchSlot">An optional statement-local slot owning the transport parameters.</param>
    /// <returns>One isolated classifier returning exactly one nine-column result set.</returns>
    internal static string BuildDelayedCatalogSelection(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        int? dispatchSlot = null
    )
    {
        var definitions = BuildDelayedParameterDefinitions(plan);
        var template = BuildDelayedCatalogTemplate(plan, definitions);
        var selection = BuildDelayedCatalogInvocation(
            ordinal,
            plan,
            dispatchSlot,
            "N'" + template.Replace("'", "''", StringComparison.Ordinal) + "'",
            definitions);

        var bytes = Encoding.UTF8.GetByteCount(selection);
        if (bytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
        {
            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                ordinal,
                plan.AnalysisParameters.Count + (dispatchSlot is not null && UsesDelayedOrdinalParameter(plan) ? 1 : 0),
                bytes);
        }

        return selection;
    }

    /// <summary>Invokes a complete classifier using either a Unicode literal or a statement-local template.</summary>
    private static string BuildDelayedCatalogInvocation(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        int? dispatchSlot,
        string statement,
        string definitions
    )
    {
        var arguments = new StringBuilder("@doka_ordinal = ");
        var bindOrdinal = dispatchSlot is not null && UsesDelayedOrdinalParameter(plan);
        arguments.Append(
            bindOrdinal && dispatchSlot is { } slot
                ? DelayedOrdinalParameterName(slot)
                : ordinal.ToString(CultureInfo.InvariantCulture));

        // WHY: Preserve source parameter positions in the delayed invocation namespace.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < plan.AnalysisParameters.Count; index++)
        {
            var value = plan.AnalysisParameters[index];
            arguments
                .Append(", ")
                .Append(value.Name)
                .Append(" = ")
                .Append(
                    dispatchSlot is { } valueSlot ? DelayedSourceParameterName(valueSlot, index) : value.ExternalName);
        }

        // WHY: The heavyweight guarded body is compiled independently of neighboring classifiers.
        // Original ordinals and source values are inputs, never template identities or cached results.
        return "EXEC sys.sp_executesql " + statement + ", N'" + definitions + "', " + arguments + ";";
    }

    /// <summary>Builds an ordinal-independent classifier with private proof and preamble variables.</summary>
    /// <param name="plan">The complete captured operation contract.</param>
    /// <returns>The guarded dynamic body; each branch returns one owned result set.</returns>
    internal static string BuildDelayedCatalogTemplate(
        SqlServerSafeMigrationRuntimePlan plan
    ) => BuildDelayedCatalogTemplate(plan, BuildDelayedParameterDefinitions(plan));

    /// <summary>Builds both dynamic scopes using one shared immutable parameter-definition buffer.</summary>
    private static string BuildDelayedCatalogTemplate(
        SqlServerSafeMigrationRuntimePlan plan,
        string definitions
    )
    {
        // WHY: SQL Server binds table and column references in a skipped IF
        // branch before execution. Dynamic SQL is required after the catalog
        // prerequisite has succeeded, not merely an IF wrapper.
        var inner = (plan.CatalogPreambleSql is null ? string.Empty : plan.CatalogPreambleSql + "\n")
            + BuildCatalogSelection("@doka_ordinal", plan, includePhysicalTableSupport: false);

        var escaped = inner.Replace("'", "''", StringComparison.Ordinal);
        var parameterArguments = plan.AnalysisParameters.Count == 0
            ? string.Empty
            : ", " + string.Join(", ", plan.AnalysisParameters.Select(static value => value.Name + " = " + value.Name));

        var outerStateGuard = plan.AnalysisOuterStateGuardExpression ?? plan.StateEvaluationGuardExpression;
        var outerStateFailure = plan.AnalysisOuterStateGuardFailureExpression
            ?? plan.StateEvaluationGuardFailureExpression;

        var physicalGate = plan.PhysicalTableSupportExpression is null
            ? string.Empty
            : BuildDelayedScalarProof(
                plan.PhysicalTableSupportExpression,
                "@doka_physical",
                "int",
                definitions,
                parameterArguments)
            + "IF COALESCE(@doka_physical, 0) <> 1 "
            + "SELECT @doka_ordinal, "
            + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode}', "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var collationGate = plan.ColumnCollationSupportExpression is null
            ? string.Empty
            : BuildDelayedScalarProof(
                plan.ColumnCollationSupportExpression,
                "@doka_collation",
                "int",
                definitions,
                parameterArguments)
            + "IF COALESCE(@doka_collation, 0) <> 1 "
            + "SELECT @doka_ordinal, "
            + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode}', "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        const string layoutVariable = "@doka_layout";
        var layoutSetup = plan.ColumnLayoutFailureExpression is null
            ? string.Empty
            : BuildDelayedScalarProof(
                plan.ColumnLayoutFailureExpression,
                layoutVariable,
                "nvarchar(128)",
                definitions,
                parameterArguments);

        var layoutGate = plan.ColumnLayoutFailureExpression is null
            ? string.Empty
            : $"IF {layoutVariable} IS NOT NULL "
            + $"SELECT @doka_ordinal, N'unsupported', 0, 0, {layoutVariable}, "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var defaultGate = plan.DefaultValueSupportExpression is null
            ? string.Empty
            : BuildDelayedScalarProof(
                plan.DefaultValueSupportExpression,
                "@doka_default",
                "int",
                definitions,
                parameterArguments)
            + "IF COALESCE(@doka_default, 0) <> 1 "
            + "SELECT @doka_ordinal, "
            + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode}', "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var indexFilterGate = plan.IndexFilterSupportExpression is null
            ? string.Empty
            : BuildDelayedScalarProof(
                plan.IndexFilterSupportExpression,
                "@doka_filter",
                "int",
                definitions,
                parameterArguments)
            + "IF COALESCE(@doka_filter, 0) <> 1 "
            + "SELECT @doka_ordinal, "
            + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode}', "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var prerequisite = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
            + (outerStateFailure is null ? string.Empty : $"AND COALESCE(({outerStateGuard}), 0) = 1 ")
            + "THEN 1 ELSE 0 END";

        var fallback = "SELECT @doka_ordinal, "
            + (outerStateFailure is null
                ? "N'prerequisite_missing'"
                : $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
                + $"THEN ({outerStateFailure}) ELSE N'prerequisite_missing' END")
            + ", 0, 0, "
            + (plan.PrerequisiteFailureCodeExpression ?? "CONVERT(nvarchar(128), NULL)")
            + ", CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL);";

        var selection = physicalGate
            + layoutSetup
            + layoutGate
            + collationGate
            + defaultGate
            + indexFilterGate
            + BuildDelayedScalarProof(prerequisite, "@doka_prerequisite", "int", definitions, parameterArguments)
            + "IF @doka_prerequisite = 1 "
            // WHY: Returning the nested SELECT directly avoids INSERT EXEC and a statement-sized
            // table variable. Transport validates one result set per classifier before accepting evidence.
            + $"EXEC sys.sp_executesql N'{escaped}', "
            + $"N'{definitions}', @doka_ordinal = @doka_ordinal"
            + parameterArguments
            + " "
            + "ELSE EXEC sys.sp_executesql N'"
            + fallback.Replace("'", "''", StringComparison.Ordinal)
            + $"', N'{definitions}', @doka_ordinal = @doka_ordinal"
            + parameterArguments
            + ";";

        // WHY: An ELSE protects only one T-SQL statement. Every admitted support gate must enclose
        // all subsequent declarations, scalar EXEC calls and the final classifier, not just a DECLARE.
        var gateCount = (physicalGate.Length > 0 ? 1 : 0)
            + (layoutGate.Length > 0 ? 1 : 0)
            + (collationGate.Length > 0 ? 1 : 0)
            + (defaultGate.Length > 0 ? 1 : 0)
            + (indexFilterGate.Length > 0 ? 1 : 0);

        var closures = gateCount switch
        {
            0 => string.Empty,
            1 => " END;",
            2 => " END; END;",
            3 => " END; END; END;",
            4 => " END; END; END; END;",
            5 => " END; END; END; END; END;",
            _ => throw new UnreachableException(),
        };

        return selection + closures;
    }

    /// <summary>Retains stable local source mappings in both levels of delayed name binding.</summary>
    private static string BuildDelayedParameterDefinitions(
        SqlServerSafeMigrationRuntimePlan plan
    ) => "@doka_ordinal int"
        + (plan.AnalysisParameters.Count == 0
            ? string.Empty
            : ", "
            + string.Join(
                ", ",
                plan.AnalysisParameters.Select(static value => value.Name + " " + value.Mapping.StoreType)));

    /// <summary>Gets the statement-local original-ordinal transport marker.</summary>
    private static string DelayedOrdinalParameterName(
        int slot
    ) => "@doka_ordinal" + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>Preserves the complete source-parameter admission boundary without raising the wire limit.</summary>
    private static bool UsesDelayedOrdinalParameter(
            SqlServerSafeMigrationRuntimePlan plan
        )
        // WHY: A previously admissible 2,000-source capture has no spare RPC parameter. Its generated
        // integer label can be literal only in the small outer dispatcher; the heavy classifier remains stable.
        => plan.AnalysisParameters.Count < SqlServerCatalogParameterBindings.MaximumParameters;

    /// <summary>Gets a source transport marker independent of the migration's global ordinal.</summary>
    private static string DelayedSourceParameterName(
        int slot,
        int index
    ) => "@doka_source"
        + slot.ToString(CultureInfo.InvariantCulture)
        + "_"
        + index.ToString(CultureInfo.InvariantCulture);
}
