namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Creates provider-native projection views without changing captured source or public contracts.</summary>
internal static class SafeMigrationCapturedExpressionNormalization
{
    /// <summary>
    /// Builds a provider-local native view while the caller retains the original operation as a capture key.
    /// </summary>
    internal static SafeMigrationIntent NormalizeIntent(
        SafeMigrationIntent intent,
        bool foldUnquotedIdentifiers
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!foldUnquotedIdentifiers)
        {
            return intent;
        }

        return intent switch
        {
            EnsureColumnIntent column => Normalize(column),
            AlterColumnIntent column => Normalize(column),
            EnsureTableIntent table => Normalize(table),
            EnsureCheckConstraintIntent check => Normalize(check),
            _ => intent,
        };
    }

    /// <summary>Normalizes only captured expressions without copying an unaffected table contract.</summary>
    internal static ExpectedTableDefinition NormalizeTable(
        ExpectedTableDefinition definition,
        bool foldUnquotedIdentifiers
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!foldUnquotedIdentifiers)
        {
            return definition;
        }

        ExpectedColumnDefinition[]? columns = null;
        // WHY: Replace normalized columns at their original table ordinals without reordering the snapshot.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            var original = definition.Columns[ordinal];
            var normalized = NormalizeColumn(original, true);
            if (!ReferenceEquals(original, normalized))
            {
                columns ??= definition.Columns.ToArray();
                columns[ordinal] = normalized;
            }
        }

        ExpectedCheckConstraintDefinition[]? checks = null;
        // WHY: Replace normalized checks at their original positions while retaining unchanged entries.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < definition.CheckConstraints.Count; ordinal++)
        {
            var original = definition.CheckConstraints[ordinal];
            var normalized = NormalizeCheck(original, true);
            if (!ReferenceEquals(original, normalized))
            {
                checks ??= definition.CheckConstraints.ToArray();
                checks[ordinal] = normalized;
            }
        }

        return columns is null && checks is null
            ? definition
            : new ExpectedTableDefinition(
                definition.Table,
                columns ?? definition.Columns,
                definition.Schema,
                definition.Comment,
                definition.PrimaryKey,
                definition.UniqueConstraints,
                checks ?? definition.CheckConstraints,
                definition.ForeignKeys);
    }

    private static EnsureColumnIntent Normalize(
        EnsureColumnIntent intent
    )
    {
        var column = NormalizeColumn(intent.Definition, true);

        return ReferenceEquals(column, intent.Definition)
            ? intent
            : new EnsureColumnIntent(intent.Table, column, intent.Schema);
    }

    private static AlterColumnIntent Normalize(
        AlterColumnIntent intent
    )
    {
        var target = NormalizeColumn(intent.Definition, true);
        var previous = intent.OldDefinition is null ? null : NormalizeColumn(intent.OldDefinition, true);

        return ReferenceEquals(target, intent.Definition) && ReferenceEquals(previous, intent.OldDefinition)
            ? intent
            : new AlterColumnIntent(intent.Table, target, previous, intent.Schema);
    }

    private static EnsureTableIntent Normalize(
        EnsureTableIntent intent
    )
    {
        var table = NormalizeTable(intent.Definition, true);

        return ReferenceEquals(table, intent.Definition) ? intent : new EnsureTableIntent(table, intent.Mode);
    }

    private static EnsureCheckConstraintIntent Normalize(
        EnsureCheckConstraintIntent intent
    )
    {
        var check = NormalizeCheck(intent.Definition, true);

        return ReferenceEquals(check, intent.Definition) ? intent : new EnsureCheckConstraintIntent(check);
    }

    /// <summary>
    /// Folds only captured unquoted identifier roles while preserving every column facet and source byte.
    /// </summary>
    internal static ExpectedColumnDefinition NormalizeColumn(
        ExpectedColumnDefinition definition,
        bool foldUnquotedIdentifiers
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!foldUnquotedIdentifiers
            || definition.CapturedComputedSql is not { } source
            || source.AsSpan().IndexOfAnyInRange('A', 'Z') < 0)
        {
            return definition;
        }

        var expression = ParseNativeSource(source);
        if (SafeMigrationSqlExpressionContract.Equivalent(definition.ComputedExpression, expression))
        {
            return definition;
        }

        // WHY: Source spelling remains the DDL and fingerprint contract.
        // This immutable copy changes only the provider-native identifier
        // roles consulted by projection, including dependency invalidation.
        return new ExpectedColumnDefinition(
            definition.Name,
            definition.ClrType,
            definition.IsNullable,
            definition.StoreType,
            definition.IsUnicode,
            definition.MaxLength,
            definition.IsFixedLength,
            definition.IsRowVersion,
            definition.Precision,
            definition.Scale,
            definition.Collation,
            definition.Comment,
            definition.DefaultValue,
            definition.ComputedColumnSql,
            definition.IsStored,
            expression)
        {
            ProviderAnnotations = definition.ProviderAnnotations,
            CapturedComputedSql = source,
        };
    }

    /// <summary>Folds captured CHECK identifier roles without weakening explicit opaque or typed contracts.</summary>
    internal static ExpectedCheckConstraintDefinition NormalizeCheck(
        ExpectedCheckConstraintDefinition definition,
        bool foldUnquotedIdentifiers
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!foldUnquotedIdentifiers
            || definition.CapturedSql is not { } source
            || source
                .AsSpan()
                .IndexOfAnyInRange('A', 'Z')
            < 0)
        {
            return definition;
        }

        var expression = ParseNativeSource(source);

        return SafeMigrationSqlExpressionContract.Equivalent(definition.Expression, expression)
            ? definition
            : ExpectedCheckConstraintDefinition.FromCapturedExpression(
                definition.Name,
                definition.Table,
                expression,
                source,
                definition.Schema);
    }

    /// <summary>A native normalization failure cannot fall back to the differently bound portable tree.</summary>
    private static SafeMigrationSqlExpression ParseNativeSource(
        string source
    ) => SafeMigrationSqlExpressionParser.TryParseCapturedSource(source, out var parsed, foldUnquotedIdentifiers: true)
        ? parsed
        : SafeMigrationSql.Opaque(source);
}
