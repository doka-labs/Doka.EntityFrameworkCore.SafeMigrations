namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Finds unsupported column capabilities in feature-wide failure order.</summary>
    /// <param name="intent">The operation whose immutable column definitions are inspected.</param>
    /// <param name="features">The active engine profile's supported migration capabilities.</param>
    /// <param name="serverVersion">The engine identity used for provider-specific restrictions.</param>
    /// <returns>The first unsupported feature code, or null when every column is supported.</returns>
    private string? GetUnsupportedColumnFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features,
        MySqlServerVersion serverVersion
    )
    {
        var isMariaDb = serverVersion.IsMariaDb;
        var definitions = intent switch
        {
            EnsureTableIntent value => value.Definition.Columns,
            EnsureColumnIntent value => [value.Definition],
            AlterColumnIntent value => [value.Definition],
            _ => [],
        };

        // WHY: Separate passes preserve feature-wide failure precedence.
        // Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (!CanMap(definition))
            {
                return "column_type_mapping";
            }
        }

        // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (definition.Collation?.Schema is not null)
            {
                return "schema_qualified_collation";
            }
        }

        // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (!CanRepresentLiteralDefault(definition, serverVersion))
            {
                return "literal_default_catalog_representation";
            }
        }

        // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (HasUnsupportedProviderColumnAnnotation(definition))
            {
                return "provider_column_annotation";
            }
        }

        // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (definition.ComputedExpression is { } expression
                && MySqlGeneratedColumnExpressionCapability.GetUnsupportedFeature(expression) is { } capability)
            {
                return capability;
            }

            if (definition.ComputedExpression is not null
                && !MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(definition.ComputedExpression))
            {
                return "generated_column_dependency_qualification";
            }

            if (intent is EnsureTableIntent table
                && definition.ComputedExpression is not null
                && !MySqlGeneratedColumnDependencies.DeclaredSourcesExist(definition, table.Definition))
            {
                return "generated_column_dependency_missing";
            }

            if (!isMariaDb
                && intent is EnsureTableIntent identityTable
                && definition.ComputedExpression is not null
                && MySqlGeneratedColumnDependencies.HasDeclaredIdentitySource(definition, identityTable.Definition))
            {
                return "generated_column_identity_dependency";
            }

            if ((definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
                && !Supported(
                    features,
                    definition.IsStored == true
                        ? MySqlMigrationFeature.StoredGeneratedColumns
                        : MySqlMigrationFeature.VirtualGeneratedColumns))
            {
                return "generated_column";
            }
        }

        if (isMariaDb)
        {
            // WHY: MariaDB has not generated NOT NULL grammar. Only a bounded
            // semantic proof can satisfy the logical required-column contract;
            // actual dependency metadata is rechecked in matching and ADD.
            // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                if (!definition.IsNullable
                    && (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
                    && (!MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out _)
                        || (intent is EnsureTableIntent table
                            && !MariaDbGeneratedColumnNullability.CanProveForTable(definition, table.Definition))))
                {
                    return "generated_column_nullability";
                }
            }
        }

        // WHY: Indexing avoids the measured IReadOnlyList enumerator allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql)
            {
                return !Supported(features, MySqlMigrationFeature.ExpressionDefaults) ? "expression_default" : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether a captured provider annotation cannot be represented
    /// by the current MySQL/MariaDB catalog comparison contract.
    /// </summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when the annotation must fail closed.</returns>
    internal static bool HasUnsupportedProviderColumnAnnotation(
        ExpectedColumnDefinition definition
    ) => !MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _);

    private bool CanMap(
        ExpectedColumnDefinition definition
    )
    {
        try
        {
            return _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private bool CanRepresentLiteralDefault(
        ExpectedColumnDefinition definition,
        MySqlServerVersion serverVersion
    )
    {
        if (definition is { IsNullable: false, DefaultValue.IsNullLiteral: true })
        {
            return false;
        }

        if (!definition.IsNullable
            && serverVersion.IsMariaDb
            && definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            var expression = definition.DefaultValue.StructuredExpression;
            if (expression is null
                && definition.DefaultValue.SqlExpression is { } sql)
            {
                _ = SafeMigrationSqlExpressionParser.TryParse(sql, out expression);
            }

            if (expression is SafeMigrationSqlLiteralExpression { Value: null, StoreType: null })
            {
                return false;
            }
        }

        if (definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.Literal)
        {
            return true;
        }

        var value = definition.DefaultValue.GetLiteralValue();
        if (value is null)
        {
            return true;
        }

        var mapping = _typeMappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            keyOrIndex: false,
            definition.IsUnicode,
            definition.MaxLength,
            definition.IsRowVersion,
            definition.IsFixedLength,
            definition.Precision,
            definition.Scale);

        var storeType = definition.StoreType ?? mapping?.StoreType;
        if (value is Guid
            && storeType?.StartsWith("binary", StringComparison.OrdinalIgnoreCase) == true)
        {
            var version = serverVersion.Version;

            return serverVersion.IsMariaDb && version is { Major: 11, Minor: 8 } or { Major: 12, Minor: 3 };
        }

        return true;
    }
}
